using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Channels;
using PrimeDictate.Core.Audio;
using PrimeDictate.Core.Providers;
using CoreAudioFormat = PrimeDictate.Core.Audio.AudioFormat;

namespace PrimeDictate.Platforms.Audio;

/// <summary>
/// Captures what a Linux output plays by recording the sink's monitor source through the
/// PulseAudio client API. PipeWire is covered through pipewire-pulse, which speaks the same API.
/// </summary>
/// <remarks>
/// This talks to libpulse-simple directly instead of going through miniaudio: in the container the
/// miniaudio backend only enumerated ALSA plug-in names, never monitor sources, and a monitor cannot
/// be chosen without changing the user's default source. Device IDs are PulseAudio source names such
/// as "alsa_output.pci-0000_00_1f.3.analog-stereo.monitor"; null captures "@DEFAULT_MONITOR@", the
/// monitor of the default output. Devices are listed with <c>pactl</c> (run with an argument list, no
/// shell); capture itself does not need it. The stream shows up in pavucontrol as
/// "PrimeDictate system audio", which is the OS-level recording indicator.
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed class PulseMonitorCaptureSource : ISystemAudioSource
{
    public const string DefaultMonitor = "@DEFAULT_MONITOR@";

    public int CaptureSampleRate { get; init; } = 48_000;

    /// <summary>Audio requested per device read; also the latency floor.</summary>
    public int PeriodMilliseconds { get; init; } = 20;

    public TimeSpan MaxQueuedAudio { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>True when the PulseAudio client library can be loaded.</summary>
    public static bool IsAvailable() =>
        OperatingSystem.IsLinux() && NativeLibrary.TryLoad("libpulse-simple.so.0", out _);

    public async ValueTask<IReadOnlyList<AudioInputDevice>> ListDevicesAsync(CancellationToken cancellationToken)
    {
        var defaultDevice = new AudioInputDevice(DefaultMonitor, "Default output", true, AudioDeviceKind.SystemAudio);
        var list = await RunPactlAsync(["list", "sources"], cancellationToken).ConfigureAwait(false);
        if (list is null)
        {
            return [defaultDevice];
        }

        var defaultSink = (await RunPactlAsync(["get-default-sink"], cancellationToken).ConfigureAwait(false))?.Trim();
        var devices = new List<AudioInputDevice> { defaultDevice };
        string? name = null;
        foreach (var raw in list.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("Name: ", StringComparison.Ordinal))
            {
                name = line[6..];
            }
            else if (line.StartsWith("Description: ", StringComparison.Ordinal) && name is not null)
            {
                // Only monitors capture playback; other sources are microphones.
                if (name.EndsWith(".monitor", StringComparison.Ordinal))
                {
                    var isDefault = defaultSink is { Length: > 0 } && name == defaultSink + ".monitor";
                    devices.Add(new AudioInputDevice(name, line[13..], isDefault, AudioDeviceKind.SystemAudio));
                }

                name = null;
            }
        }

        return devices;
    }

    public ValueTask<IAudioCaptureLease> OpenAsync(string? deviceId, CancellationToken cancellationToken)
    {
        var lease = new PulseLease(deviceId ?? DefaultMonitor, this.CaptureSampleRate, this.PeriodMilliseconds, this.MaxQueuedAudio);
        lease.Start();
        return ValueTask.FromResult<IAudioCaptureLease>(lease);
    }

    private static async Task<string?> RunPactlAsync(string[] arguments, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo("pactl") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        // Stable English labels; pactl localizes "Name:" and "Description:".
        info.Environment["LC_ALL"] = "C";
        try
        {
            using var process = Process.Start(info);
            if (process is null)
            {
                return null;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return process.ExitCode == 0 ? output : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private static class Native
    {
        private const string Lib = "libpulse-simple.so.0";
        private const string Core = "libpulse.so.0";

        public const int SampleFloat32Le = 5;
        public const int StreamRecord = 2;

        [StructLayout(LayoutKind.Sequential)]
        public struct SampleSpec
        {
            public int Format;
            public uint Rate;
            public byte Channels;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BufferAttr
        {
            public uint MaxLength;
            public uint TLength;
            public uint PreBuf;
            public uint MinReq;
            public uint FragSize;
        }

        [DllImport(Lib, EntryPoint = "pa_simple_new")]
        public static extern nint SimpleNew(string? server, string name, int direction, string? device, string streamName, ref SampleSpec spec, nint map, ref BufferAttr attr, out int error);

        [DllImport(Lib, EntryPoint = "pa_simple_read")]
        public static extern int SimpleRead(nint handle, byte[] data, nuint bytes, out int error);

        [DllImport(Lib, EntryPoint = "pa_simple_free")]
        public static extern void SimpleFree(nint handle);

        [DllImport(Core, EntryPoint = "pa_strerror")]
        public static extern nint StrError(int error);
    }

    /// <remarks>Pause closes the stream and Resume opens a new one, as for the microphone adapter, so nothing is recorded while paused.</remarks>
    private sealed class PulseLease : IAudioCaptureLease
    {
        private readonly string device;
        private readonly int rate;
        private readonly int periodBytes;
        private readonly Channel<AudioFrame> frames;
        private readonly object sync = new();
        private nint handle;
        private Thread? reader;
        private volatile bool closing;
        private long sequence;
        private long sampleOffset;
        private long droppedSamples;
        private bool disposed;

        public PulseLease(string device, int rate, int periodMilliseconds, TimeSpan maxQueued)
        {
            this.device = device;
            this.rate = rate;
            this.periodBytes = rate * periodMilliseconds / 1000 * sizeof(float);
            this.Format = new CoreAudioFormat(rate, 1, AudioSampleFormat.Float32);
            this.DeviceId = device;
            this.DeviceName = device == DefaultMonitor ? "System audio (default output)" : "System audio (" + device + ")";
            var capacity = Math.Max(16, (int)(maxQueued.TotalMilliseconds / periodMilliseconds));
            this.frames = Channel.CreateBounded<AudioFrame>(new BoundedChannelOptions(capacity)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait
            });
        }

        public CoreAudioFormat Format { get; }

        public string DeviceId { get; }

        public string DeviceName { get; }

        public long DroppedSamples => Interlocked.Read(ref this.droppedSamples);

        public void Start()
        {
            lock (this.sync)
            {
                ObjectDisposedException.ThrowIf(this.disposed, this);
                if (this.handle != 0)
                {
                    return;
                }

                var spec = new Native.SampleSpec { Format = Native.SampleFloat32Le, Rate = (uint)this.rate, Channels = 1 };
                var attr = new Native.BufferAttr
                {
                    MaxLength = uint.MaxValue,
                    TLength = uint.MaxValue,
                    PreBuf = uint.MaxValue,
                    MinReq = uint.MaxValue,
                    FragSize = (uint)this.periodBytes
                };

                nint opened;
                int error;
                try
                {
                    opened = Native.SimpleNew(null, "PrimeDictate system audio", Native.StreamRecord, this.device, "Transcription capture (local)", ref spec, 0, ref attr, out error);
                }
                catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
                {
                    throw new AudioSourceException(AudioSourceErrorKind.NotSupported, "System audio capture needs the PulseAudio client library (libpulse-simple) or PipeWire's PulseAudio support.", ex);
                }

                if (opened == 0)
                {
                    throw ToException(error);
                }

                this.closing = false;
                this.handle = opened;
                this.reader = new Thread(() => this.ReadLoop(opened)) { IsBackground = true, Name = "PulseMonitorCapture" };
                this.reader.Start();
            }
        }

        public async IAsyncEnumerable<AudioFrame> ReadFramesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var frame in this.frames.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return frame;
            }
        }

        public ValueTask PauseAsync(CancellationToken cancellationToken)
        {
            this.Close();
            return ValueTask.CompletedTask;
        }

        public ValueTask ResumeAsync(CancellationToken cancellationToken)
        {
            this.Start();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            lock (this.sync)
            {
                if (this.disposed)
                {
                    return ValueTask.CompletedTask;
                }

                this.disposed = true;
            }

            this.Close();
            this.frames.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }

        private void Close()
        {
            Thread? thread;
            nint stream;
            lock (this.sync)
            {
                thread = this.reader;
                stream = this.handle;
                this.reader = null;
                this.handle = 0;
                this.closing = true;
            }

            if (stream == 0)
            {
                return;
            }

            // The reader wakes at least once per period while the server delivers data. If the server
            // is gone the read errors out. If the join times out the handle is leaked on purpose: freeing
            // a stream another thread is blocked in is undefined behavior.
            if (thread is null || thread.Join(TimeSpan.FromSeconds(2)))
            {
                Native.SimpleFree(stream);
            }
        }

        private void ReadLoop(nint stream)
        {
            var buffer = new byte[this.periodBytes];
            var floats = new float[this.periodBytes / sizeof(float)];
            while (!this.closing)
            {
                if (Native.SimpleRead(stream, buffer, (nuint)buffer.Length, out var error) < 0)
                {
                    if (!this.closing)
                    {
                        // The server went away or the source was removed.
                        this.frames.Writer.TryComplete(ToException(error));
                    }

                    return;
                }

                Buffer.BlockCopy(buffer, 0, floats, 0, buffer.Length);
                var frame = AudioFrame.CopyFrom(floats, this.Format, this.sequence, this.sampleOffset);
                this.sampleOffset += floats.Length;
                this.sequence++;
                if (!this.frames.Writer.TryWrite(frame))
                {
                    // Never block the audio thread. The skipped sequence number and offset make the loss explicit downstream.
                    Interlocked.Add(ref this.droppedSamples, floats.Length);
                }
            }
        }

        private static AudioSourceException ToException(int error)
        {
            string message;
            try
            {
                message = Marshal.PtrToStringUTF8(Native.StrError(error)) ?? "unknown error";
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                message = "error " + error;
            }

            // pa_error_code_t: 2 access denied, 5 no such entity, 6 connection refused, 7 protocol error.
            var kind = error switch
            {
                2 => AudioSourceErrorKind.PermissionDenied,
                5 => AudioSourceErrorKind.DeviceRemoved,
                _ => AudioSourceErrorKind.Unknown
            };
            return new AudioSourceException(kind, "System audio capture failed: " + message + ".");
        }
    }
}
