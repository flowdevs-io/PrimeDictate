using System.Runtime.CompilerServices;
using System.Threading.Channels;
using PrimeDictate.Core.Audio;
using PrimeDictate.Core.Providers;
using SoundFlow.Abstracts.Devices;
using SoundFlow.Backends.MiniAudio;
using SoundFlow.Backends.MiniAudio.Devices;
using SoundFlow.Backends.MiniAudio.Enums;
using SoundFlow.Enums;
using SoundFlow.Structs;
using CoreAudioFormat = PrimeDictate.Core.Audio.AudioFormat;
using SfAudioFormat = SoundFlow.Structs.AudioFormat;

namespace PrimeDictate.Platforms.Audio;

/// <summary>
/// Microphone capture through miniaudio (via SoundFlow): WASAPI on Windows, CoreAudio on macOS, PulseAudio or ALSA
/// on Linux. Dictation, the wake word and meetings all record through it.
/// </summary>
/// <remarks>
/// Device IDs are device names, because miniaudio's native IDs are not stable across
/// enumerations. On Windows a WPF-saved endpoint id is also accepted and looked up by its name.
/// Frames are copied out of the native callback before they are queued.
/// </remarks>
public sealed class MiniAudioCaptureSource : IAudioSource, IDisposable
{
    private readonly MiniAudioEngine engine;

    public MiniAudioCaptureSource(IEnumerable<MiniAudioBackend>? backendPriority = null)
    {
        try
        {
            this.engine = new MiniAudioEngine(backendPriority ?? DefaultBackends());
        }
        catch (InvalidOperationException ex)
        {
            throw new AudioSourceException(AudioSourceErrorKind.Unknown, "No audio system is available for microphone capture.", ex);
        }
    }

    public string ActiveBackend => this.engine.ActiveBackend.ToString();

    /// <summary>Capture rate requested from the device; conversion to the speech timeline happens in Core.</summary>
    public int CaptureSampleRate { get; init; } = 48_000;

    /// <summary>Requested device period; small values keep Stop and Pause from losing buffered audio.</summary>
    public uint PeriodMilliseconds { get; init; } = 20;

    /// <summary>Maximum queued audio before capture reports an overflow gap.</summary>
    public TimeSpan MaxQueuedAudio { get; init; } = TimeSpan.FromSeconds(30);

    public ValueTask<IReadOnlyList<AudioInputDevice>> ListDevicesAsync(CancellationToken cancellationToken)
    {
        this.engine.UpdateAudioDevicesInfo();
        IReadOnlyList<AudioInputDevice> devices = this.engine.CaptureDevices
            .Select(d => new AudioInputDevice(d.Name, d.Name, d.IsDefault))
            .ToList();
        return ValueTask.FromResult(devices);
    }

    public ValueTask<IAudioCaptureLease> OpenAsync(string? deviceId, CancellationToken cancellationToken) =>
        this.OpenAsync(deviceId, MicAccessMode.Shared, cancellationToken);

    /// <summary>
    /// Exclusive is honoured only on Windows (WASAPI exclusive mode). If the device refuses it, the open is retried
    /// shared and logged, as the WPF app does; the lease's <see cref="IAudioCaptureLease.AccessMode"/> reports the result.
    /// </summary>
    public ValueTask<IAudioCaptureLease> OpenAsync(string? deviceId, MicAccessMode access, CancellationToken cancellationToken)
    {
        this.engine.UpdateAudioDevicesInfo();
        DeviceInfo? device = null;
        if (deviceId is not null)
        {
            var name = deviceId;
            if (OperatingSystem.IsWindows() && WindowsAudioEndpoints.IsEndpointId(deviceId))
            {
                name = WindowsAudioEndpoints.FriendlyName(deviceId) ?? deviceId;
            }

            var match = this.engine.CaptureDevices.Where(d => d.Name == name).ToArray();
            if (match.Length == 0)
            {
                throw new AudioSourceException(AudioSourceErrorKind.DeviceRemoved, "The selected microphone is not connected.");
            }

            device = match[0];
        }

        var format = new SfAudioFormat
        {
            Format = SampleFormat.F32,
            Channels = 1,
            SampleRate = this.CaptureSampleRate,
            Layout = SfAudioFormat.GetLayoutFromChannels(1)
        };

        AudioCaptureDevice OpenDevice(bool exclusive)
        {
            try
            {
                // WASAPI exclusive mode needs a concrete endpoint: miniaudio's "default" entry (automatic stream
                // routing) cannot be exclusive (ShareModeNotSupported). Resolve the Windows default by its name.
                var chosen = device;
                if (chosen is null && exclusive && OperatingSystem.IsWindows() && WindowsAudioEndpoints.DefaultCaptureName() is { } defaultName)
                {
                    chosen = this.engine.CaptureDevices.FirstOrDefault(d => d.Name == defaultName);
                }

                return this.engine.InitializeCaptureDevice(chosen, format, new MiniAudioDeviceConfig
                {
                    // Without an explicit period, PulseAudio defaults to roughly 2 s of buffering,
                    // which delays the first frame and is lost when the device closes.
                    PeriodSizeInMilliseconds = this.PeriodMilliseconds,
                    Periods = 3,
                    Capture = new DeviceSubConfig { ShareMode = exclusive ? ShareMode.Exclusive : ShareMode.Shared }
                });
            }
            catch (Exception ex)
            {
                throw new AudioSourceException(AudioSourceErrorKind.Unknown, "The microphone could not be opened.", ex);
            }
        }

        var exclusive = access == MicAccessMode.Exclusive && OperatingSystem.IsWindows();
        var lease = new CaptureLease(OpenDevice, exclusive, new CoreAudioFormat(this.CaptureSampleRate, 1, AudioSampleFormat.Float32), device?.Name, this.MaxQueuedAudio);
        lease.Start();
        return ValueTask.FromResult<IAudioCaptureLease>(lease);
    }

    public void Dispose() => this.engine.Dispose();

    private static MiniAudioBackend[] DefaultBackends() =>
        OperatingSystem.IsMacOS() ? [MiniAudioBackend.CoreAudio]
        : OperatingSystem.IsWindows() ? [MiniAudioBackend.Wasapi]
        : [MiniAudioBackend.PulseAudio, MiniAudioBackend.Alsa];

    /// <remarks>
    /// Pause closes the device and Resume opens a new one. Measured on Linux x64 with PulseAudio
    /// 16.1: stopping and restarting a miniaudio capture device delivered about 3 s of audio from
    /// the paused period in a burst on Resume, so Pause would not mean "nothing is recorded".
    /// Reopening delivered no paused audio but took about 1.2 s before the first frame, so the UI
    /// must show Resuming until frames arrive. See docs/architecture/stage-3-spikes.md.
    /// </remarks>
    private sealed class CaptureLease : IAudioCaptureLease
    {
        private readonly Func<bool, AudioCaptureDevice> openDevice;
        private volatile bool exclusive;
        private readonly Channel<AudioFrame> frames;
        private readonly object sync = new();
        private AudioCaptureDevice? device;
        private long sequence;
        private long sampleOffset;
        private long droppedSamples;
        private bool disposed;

        public CaptureLease(Func<bool, AudioCaptureDevice> openDevice, bool exclusive, CoreAudioFormat format, string? name, TimeSpan maxQueued)
        {
            this.openDevice = openDevice;
            this.exclusive = exclusive;
            this.Format = format;
            this.DeviceId = name ?? "default";
            this.DeviceName = name ?? "Default microphone";
            // Callbacks deliver roughly 10 ms each; size the queue by duration.
            var capacity = Math.Max(16, (int)(maxQueued.TotalMilliseconds / 10));
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

        public MicAccessMode AccessMode => this.exclusive ? MicAccessMode.Exclusive : MicAccessMode.Shared;

        /// <summary>Samples lost because the consumer fell behind; the offset skips ahead so the gap is visible.</summary>
        public long DroppedSamples => Interlocked.Read(ref this.droppedSamples);

        public void Start()
        {
            lock (this.sync)
            {
                ObjectDisposedException.ThrowIf(this.disposed, this);
                if (this.device is not null)
                {
                    return;
                }

                AudioCaptureDevice opened;
                try
                {
                    opened = this.OpenAndStart(this.exclusive);
                }
                catch (Exception ex) when (this.exclusive)
                {
                    // The device refused exclusive mode (in use, or no exclusive format): shared still records.
                    PrimeDictate.Core.Diagnostics.AppLog.Event("microphone", $"Exclusive microphone mode failed ({ex.GetType().Name}: {ex.InnerException?.Message ?? ex.Message}); falling back to shared mode.");
                    this.exclusive = false;
                    opened = this.OpenAndStart(false);
                }

                this.device = opened;
            }
        }

        private AudioCaptureDevice OpenAndStart(bool exclusiveMode)
        {
            var opened = this.openDevice(exclusiveMode);
            try
            {
                opened.OnAudioProcessed += this.OnAudio;
                opened.Start();
                return opened;
            }
            catch
            {
                opened.OnAudioProcessed -= this.OnAudio;
                opened.Dispose();
                throw;
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
            lock (this.sync)
            {
                this.CloseDevice();
            }

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
                this.CloseDevice();
            }

            this.frames.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }

        private void CloseDevice()
        {
            if (this.device is null)
            {
                return;
            }

            this.device.OnAudioProcessed -= this.OnAudio;
            this.device.Stop();
            this.device.Dispose();
            this.device = null;
        }

        private void OnAudio(Span<float> samples, Capability capability)
        {
            if (samples.IsEmpty)
            {
                return;
            }

            // Copy before returning: the span belongs to the native callback.
            var frame = AudioFrame.CopyFrom(samples, this.Format, this.sequence, this.sampleOffset);
            this.sampleOffset += samples.Length;
            this.sequence++;
            if (!this.frames.Writer.TryWrite(frame))
            {
                // Never block the audio thread. The skipped sequence number and offset make the loss explicit downstream.
                Interlocked.Add(ref this.droppedSamples, samples.Length);
            }
        }
    }
}
