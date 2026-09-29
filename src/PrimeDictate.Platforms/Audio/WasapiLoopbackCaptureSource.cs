using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Channels;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using PrimeDictate.Core.Audio;
using PrimeDictate.Core.Providers;
using CoreAudioFormat = PrimeDictate.Core.Audio.AudioFormat;

namespace PrimeDictate.Platforms.Audio;

/// <summary>
/// Captures what a Windows output device is playing through WASAPI loopback (shared mode). No
/// driver, no virtual cable, and no permission prompt is needed on Windows.
/// </summary>
/// <remarks>
/// WASAPI loopback delivers no packets while nothing plays, so a raw capture has no sample clock
/// during silence. The lease keeps the timeline continuous by inserting zeros for any stretch with
/// no data (measured against a monotonic clock), so it stays aligned with a microphone. Device IDs
/// are WASAPI endpoint IDs. Not yet run on Windows: see docs/architecture/system-audio-capture.md.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WasapiLoopbackCaptureSource : ISystemAudioSource
{
    /// <summary>Maximum queued audio before capture reports an overflow gap.</summary>
    public TimeSpan MaxQueuedAudio { get; init; } = TimeSpan.FromSeconds(30);

    public ValueTask<IReadOnlyList<AudioInputDevice>> ListDevicesAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            string? defaultId = null;
            try
            {
                using var def = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                defaultId = def.ID;
            }
            catch (COMException)
            {
                // No default output device.
            }

            var devices = new List<AudioInputDevice>();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (device)
                {
                    devices.Add(new AudioInputDevice(device.ID, device.FriendlyName, device.ID == defaultId, AudioDeviceKind.SystemAudio));
                }
            }

            return ValueTask.FromResult<IReadOnlyList<AudioInputDevice>>(devices);
        }
        catch (COMException ex)
        {
            throw new AudioSourceException(AudioSourceErrorKind.Unknown, "Output devices could not be listed.", ex);
        }
    }

    public ValueTask<IAudioCaptureLease> OpenAsync(string? deviceId, CancellationToken cancellationToken)
    {
        var lease = new LoopbackLease(deviceId, this.MaxQueuedAudio);
        lease.Start();
        return ValueTask.FromResult<IAudioCaptureLease>(lease);
    }

    private sealed class LoopbackLease : IAudioCaptureLease
    {
        private static readonly TimeSpan SilenceThreshold = TimeSpan.FromMilliseconds(50);

        private readonly string? requestedId;
        private readonly Channel<AudioFrame> frames;
        private readonly object sync = new();
        private WasapiLoopbackCapture? capture;
        private Timer? silenceTimer;
        private TaskCompletionSource? stopped;
        private CoreAudioFormat format;
        private long lastDataTimestamp;
        private long sequence;
        private long sampleOffset;
        private long droppedSamples;
        private bool disposed;

        public LoopbackLease(string? deviceId, TimeSpan maxQueued)
        {
            this.requestedId = deviceId;
            this.DeviceId = deviceId ?? "default-output";
            this.DeviceName = "System audio";
            var capacity = Math.Max(16, (int)(maxQueued.TotalMilliseconds / 10));
            this.frames = Channel.CreateBounded<AudioFrame>(new BoundedChannelOptions(capacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });
        }

        public CoreAudioFormat Format => this.format;

        public string DeviceId { get; }

        public string DeviceName { get; private set; }

        public long DroppedSamples => Interlocked.Read(ref this.droppedSamples);

        public void Start()
        {
            lock (this.sync)
            {
                ObjectDisposedException.ThrowIf(this.disposed, this);
                if (this.capture is not null)
                {
                    return;
                }

                WasapiLoopbackCapture opened;
                try
                {
                    using var enumerator = new MMDeviceEnumerator();
                    using var device = this.requestedId is null
                        ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                        : enumerator.GetDevice(this.requestedId);
                    this.DeviceName = device.FriendlyName;
                    opened = new WasapiLoopbackCapture(device);
                }
                catch (COMException ex)
                {
                    // ELEMENTNOTFOUND (0x80070490) or no endpoint at all.
                    throw new AudioSourceException(AudioSourceErrorKind.DeviceRemoved, "The selected output device is not available.", ex);
                }

                try
                {
                    this.format = ToCoreFormat(opened.WaveFormat);
                }
                catch
                {
                    opened.Dispose();
                    throw;
                }

                this.stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                opened.DataAvailable += this.OnData;
                opened.RecordingStopped += this.OnStopped;
                this.lastDataTimestamp = Stopwatch.GetTimestamp();
                try
                {
                    opened.StartRecording();
                }
                catch (COMException ex)
                {
                    opened.Dispose();
                    throw new AudioSourceException(AudioSourceErrorKind.Unknown, "System audio capture could not start.", ex);
                }

                this.capture = opened;
                this.silenceTimer = new Timer(this.FillSilence, null, 20, 20);
            }
        }

        public async IAsyncEnumerable<AudioFrame> ReadFramesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var frame in this.frames.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return frame;
            }
        }

        public async ValueTask PauseAsync(CancellationToken cancellationToken) => await this.CloseAsync().ConfigureAwait(false);

        public ValueTask ResumeAsync(CancellationToken cancellationToken)
        {
            this.Start();
            return ValueTask.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            lock (this.sync)
            {
                if (this.disposed)
                {
                    return;
                }

                this.disposed = true;
            }

            await this.CloseAsync().ConfigureAwait(false);
            this.frames.Writer.TryComplete();
        }

        private async ValueTask CloseAsync()
        {
            WasapiLoopbackCapture? closing;
            Timer? timer;
            TaskCompletionSource? done;
            lock (this.sync)
            {
                closing = this.capture;
                timer = this.silenceTimer;
                done = this.stopped;
                this.capture = null;
                this.silenceTimer = null;
            }

            if (timer is not null)
            {
                await timer.DisposeAsync().ConfigureAwait(false);
            }

            if (closing is null)
            {
                return;
            }

            closing.DataAvailable -= this.OnData;
            closing.StopRecording();
            // The last buffer is delivered on the capture thread before RecordingStopped; wait so it is not lost.
            await Task.WhenAny(done!.Task, Task.Delay(TimeSpan.FromSeconds(1))).ConfigureAwait(false);
            closing.RecordingStopped -= this.OnStopped;
            closing.Dispose();
        }

        private static CoreAudioFormat ToCoreFormat(WaveFormat wave)
        {
            if (!IsFloat32(wave) && !(wave.Encoding == WaveFormatEncoding.Pcm && wave.BitsPerSample == 16))
            {
                throw new AudioSourceException(AudioSourceErrorKind.FormatUnsupported, $"The output device mix format ({wave}) is not supported.");
            }

            return new CoreAudioFormat(wave.SampleRate, wave.Channels, AudioSampleFormat.Float32);
        }

        private static bool IsFloat32(WaveFormat wave) =>
            wave.BitsPerSample == 32 &&
            (wave is WaveFormatExtensible ext ? ext.ToStandardWaveFormat().Encoding : wave.Encoding) == WaveFormatEncoding.IeeeFloat;

        // These run on the capture thread and a timer thread, where an escaping exception would take down the
        // whole process. Fail the lease instead, so the session reports an error and keeps what it recorded.
        private void Fail(Exception ex) =>
            this.frames.Writer.TryComplete(new AudioSourceException(AudioSourceErrorKind.Unknown, "System audio capture failed: " + ex.Message, ex));

        private void OnData(object? sender, WaveInEventArgs e)
        {
            try
            {
                this.HandleData(sender, e);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or COMException or ObjectDisposedException)
            {
                this.Fail(ex);
            }
        }

        private void HandleData(object? sender, WaveInEventArgs e)
        {
            if (e.BytesRecorded == 0)
            {
                return;
            }

            var wave = ((WasapiLoopbackCapture)sender!).WaveFormat;
            float[] samples;
            if (IsFloat32(wave))
            {
                samples = MemoryMarshal.Cast<byte, float>(e.Buffer.AsSpan(0, e.BytesRecorded)).ToArray();
            }
            else
            {
                samples = new float[e.BytesRecorded / 2];
                AudioConversion.Pcm16ToFloat(e.Buffer.AsSpan(0, samples.Length * 2), samples);
            }

            lock (this.sync)
            {
                this.lastDataTimestamp = Stopwatch.GetTimestamp();
                this.Publish(samples);
            }
        }

        // Loopback sends nothing during silence. Insert zeros for the stretch since the last packet so the
        // sample clock keeps running and stays aligned with the microphone.
        private void FillSilence(object? state)
        {
            try
            {
                this.FillSilenceCore();
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or OutOfMemoryException or ObjectDisposedException)
            {
                this.Fail(ex);
            }
        }

        private void FillSilenceCore()
        {
            lock (this.sync)
            {
                if (this.capture is null)
                {
                    return;
                }

                var now = Stopwatch.GetTimestamp();
                var quiet = Stopwatch.GetElapsedTime(this.lastDataTimestamp, now);
                if (quiet < SilenceThreshold)
                {
                    return;
                }

                var perChannel = (int)(quiet.TotalSeconds * this.format.SampleRate);
                this.lastDataTimestamp = now;
                this.Publish(new float[perChannel * this.format.Channels], synthetic: true);
            }
        }

        private void Publish(float[] samples, bool synthetic = false)
        {
            var frame = AudioFrame.CopyFrom(samples, this.format, this.sequence, this.sampleOffset, synthetic);
            this.sampleOffset += frame.SamplesPerChannel;
            this.sequence++;
            if (!this.frames.Writer.TryWrite(frame))
            {
                // Never block the audio thread. The skipped sequence number and offset make the loss explicit downstream.
                Interlocked.Add(ref this.droppedSamples, frame.SamplesPerChannel);
            }
        }

        private void OnStopped(object? sender, StoppedEventArgs e)
        {
            this.stopped?.TrySetResult();
            if (e.Exception is not null && !this.disposed)
            {
                var kind = e.Exception is COMException { HResult: unchecked((int)0x88890004) } // AUDCLNT_E_DEVICE_INVALIDATED
                    ? AudioSourceErrorKind.DeviceRemoved
                    : AudioSourceErrorKind.Unknown;
                this.frames.Writer.TryComplete(new AudioSourceException(kind, "System audio capture stopped unexpectedly.", e.Exception));
            }
        }
    }
}
