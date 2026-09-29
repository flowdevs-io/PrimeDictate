using System.Runtime.CompilerServices;
using System.Threading.Channels;
using PrimeDictate.Core.Providers;

namespace PrimeDictate.Core.Audio;

public enum CombinedChannelLayout
{
    /// <summary>Channel 0 is the microphone and channel 1 is system audio, sample-aligned.</summary>
    Stereo = 0,

    /// <summary>One channel with both summed.</summary>
    Mixed = 1
}

/// <summary>A lease that reports how much of each side had to be filled with silence.</summary>
public interface ICombinedAudioCaptureLease : IAudioCaptureLease
{
    /// <summary>Output samples of silence inserted for the microphone side because it stalled or lost samples.</summary>
    long MicrophonePaddedSamples { get; }

    /// <summary>Output samples of silence inserted for the system-audio side because it stalled or lost samples.</summary>
    long SystemAudioPaddedSamples { get; }
}

public sealed record CombinedAudioOptions
{
    public CombinedChannelLayout Layout { get; init; } = CombinedChannelLayout.Stereo;

    /// <summary>Common rate both sides are resampled to. Speech engines consume 16 kHz, so no second conversion is needed.</summary>
    public int OutputSampleRate { get; init; } = AudioFormat.SpeechTimeline.SampleRate;

    /// <summary>How long one side may deliver nothing while the other has audio before it is padded with silence.</summary>
    public TimeSpan MaxSkew { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Audio buffered per side before its reader waits, which pushes back on the device queue.</summary>
    public TimeSpan MaxBufferedPerSide { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long Pause waits for frames already in flight before flushing.</summary>
    public TimeSpan PauseDrainDelay { get; init; } = TimeSpan.FromMilliseconds(100);

    public TimeProvider Clock { get; init; } = TimeProvider.System;
}

/// <summary>
/// Opens a microphone and a system-audio source together and delivers them as one lease on one
/// sample clock. Each side is counted in output samples, so output sample N of the microphone and
/// of system audio are the same instant. Alignment is by arrival time: a side that starts late, or
/// stalls for longer than <see cref="CombinedAudioOptions.MaxSkew"/>, is padded with silence up to
/// the other side, so the timeline never drifts. Accuracy is bounded by device buffering, which
/// ranges from tens of milliseconds to a few hundred (and about 2 s for a PulseAudio null sink).
/// Samples a device reports as lost are padded too and the combined sequence number skips, so the
/// session records a gap. Silence inserted on either side is counted in
/// <see cref="ICombinedAudioCaptureLease"/>.
/// </summary>
public sealed class CombinedAudioSource(IAudioSource microphone, IAudioSource systemAudio, CombinedAudioOptions? options = null) : IAudioSource
{
    private readonly CombinedAudioOptions options = options ?? new();

    /// <summary>System-audio device to capture; null selects the default output.</summary>
    public string? SystemDeviceId { get; init; }

    /// <summary>Lists microphones. The system-audio device is chosen with <see cref="SystemDeviceId"/>.</summary>
    public ValueTask<IReadOnlyList<AudioInputDevice>> ListDevicesAsync(CancellationToken cancellationToken) =>
        microphone.ListDevicesAsync(cancellationToken);

    public async ValueTask<IAudioCaptureLease> OpenAsync(string? deviceId, CancellationToken cancellationToken)
    {
        var mic = await microphone.OpenAsync(deviceId, cancellationToken).ConfigureAwait(false);
        IAudioCaptureLease system;
        try
        {
            system = await systemAudio.OpenAsync(this.SystemDeviceId, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await mic.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return new CombinedLease(mic, system, this.options);
    }

    private sealed class Side(int capacity)
    {
        private readonly List<float> samples = [];
        private readonly List<bool> synthetic = [];
        public long LastDataTicks;
        public bool Done;
        public readonly Queue<long> GapPositions = new();
        public long Produced;

        public int Count => this.samples.Count;

        public int Capacity => capacity;

        public void Add(ReadOnlySpan<float> data, bool isSynthetic = false)
        {
            foreach (var s in data)
            {
                this.samples.Add(s);
                this.synthetic.Add(isSynthetic);
            }

            this.Produced += data.Length;
        }

        public void PadTo(int count)
        {
            while (this.samples.Count < count)
            {
                this.samples.Add(0f);
                this.synthetic.Add(true);
                this.Produced++;
            }
        }

        /// <summary>Takes samples; returns true when every one of them was synthetic silence.</summary>
        public bool Take(int count, Span<float> destination)
        {
            var allSynthetic = true;
            for (var i = 0; i < count; i++)
            {
                destination[i] = this.samples[i];
                allSynthetic &= this.synthetic[i];
            }

            this.samples.RemoveRange(0, count);
            this.synthetic.RemoveRange(0, count);
            return allSynthetic;
        }

        
    }

    private sealed class CombinedLease : ICombinedAudioCaptureLease
    {
        private readonly IAudioCaptureLease mic;
        private readonly IAudioCaptureLease system;
        private readonly CombinedAudioOptions options;
        private readonly int chunk;
        private readonly Side micSide;
        private readonly Side systemSide;
        private readonly object sync = new();
        private readonly Channel<bool> signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
        private readonly Channel<AudioFrame> output;
        private readonly CancellationTokenSource stop = new();
        private readonly Task micPump;
        private readonly Task systemPump;
        private readonly Task emitter;
        private TaskCompletionSource? flushRequest;
        private long sequence;
        private long sampleOffset;
        private bool disposed;

        public CombinedLease(IAudioCaptureLease mic, IAudioCaptureLease system, CombinedAudioOptions options)
        {
            this.mic = mic;
            this.system = system;
            this.options = options;
            this.chunk = Math.Max(1, options.OutputSampleRate / 50);
            var capacity = Math.Max(this.chunk * 2, (int)(options.MaxBufferedPerSide.TotalSeconds * options.OutputSampleRate));
            this.micSide = new Side(capacity);
            this.systemSide = new Side(capacity);
            this.Format = new AudioFormat(options.OutputSampleRate, options.Layout == CombinedChannelLayout.Stereo ? 2 : 1, AudioSampleFormat.Float32);
            this.DeviceId = $"{mic.DeviceId}+{system.DeviceId}";
            this.DeviceName = $"{mic.DeviceName} + {system.DeviceName}";
            // Output holds about 10 s of 20 ms chunks; beyond that the readers wait and device queues take over.
            this.output = Channel.CreateBounded<AudioFrame>(new BoundedChannelOptions(500) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
            var now = options.Clock.GetTimestamp();
            this.micSide.LastDataTicks = now;
            this.systemSide.LastDataTicks = now;
            this.micPump = Task.Run(() => this.PumpAsync(mic, this.micSide));
            this.systemPump = Task.Run(() => this.PumpAsync(system, this.systemSide));
            this.emitter = Task.Run(this.EmitLoopAsync);
        }

        public AudioFormat Format { get; }

        public string DeviceId { get; }

        public string DeviceName { get; }

        public long MicrophonePaddedSamples { get; private set; }

        public long SystemAudioPaddedSamples { get; private set; }

        public async IAsyncEnumerable<AudioFrame> ReadFramesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var frame in this.output.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return frame;
            }
        }

        public async ValueTask PauseAsync(CancellationToken cancellationToken)
        {
            await this.system.PauseAsync(cancellationToken).ConfigureAwait(false);
            await this.mic.PauseAsync(cancellationToken).ConfigureAwait(false);
            // Let frames already queued by the devices arrive, then emit everything including the shorter side's tail.
            await Task.Delay(this.options.PauseDrainDelay, this.options.Clock, cancellationToken).ConfigureAwait(false);
            TaskCompletionSource tcs;
            lock (this.sync)
            {
                tcs = this.flushRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            this.signal.Writer.TryWrite(true);
            await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask ResumeAsync(CancellationToken cancellationToken)
        {
            var now = this.options.Clock.GetTimestamp();
            lock (this.sync)
            {
                this.micSide.LastDataTicks = now;
                this.systemSide.LastDataTicks = now;
            }

            await this.mic.ResumeAsync(cancellationToken).ConfigureAwait(false);
            await this.system.ResumeAsync(cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            if (this.disposed)
            {
                return;
            }

            this.disposed = true;
            await this.mic.DisposeAsync().ConfigureAwait(false);
            await this.system.DisposeAsync().ConfigureAwait(false);
            try
            {
                // Pumps end when the device leases complete; the emitter then flushes the tail and completes the output.
                await Task.WhenAll(this.micPump, this.systemPump, this.emitter).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                await this.stop.CancelAsync().ConfigureAwait(false);
                this.output.Writer.TryComplete();
            }

            this.stop.Dispose();
        }

        private async Task PumpAsync(IAudioCaptureLease lease, Side side)
        {
            var resampler = new StreamingResampler(lease.Format.SampleRate, this.options.OutputSampleRate);
            long expectedOffset = 0;
            long expectedSequence = 0;
            try
            {
                await foreach (var frame in lease.ReadFramesAsync(this.stop.Token).ConfigureAwait(false))
                {
                    if (frame.SequenceNumber != expectedSequence)
                    {
                        // The device lost samples. Keep the two sides aligned by padding the hole with silence.
                        var lostInput = frame.SampleOffset - expectedOffset;
                        var lost = lostInput > 0
                            ? (int)Math.Min(side.Capacity, lostInput * this.options.OutputSampleRate / lease.Format.SampleRate)
                            : 0;
                        // Flag and padding go in under one lock so the emitter cannot send the padded hole before the flag.
                        this.AddSamples(side, new float[lost], padding: true, gap: true, synthetic: true);
                    }

                    expectedSequence = frame.SequenceNumber + 1;
                    expectedOffset = frame.EndSampleOffset;
                    var mono = AudioConversion.DownmixToMono(frame.Samples.Span, frame.Format.Channels);
                    await this.WaitForRoomAsync(side).ConfigureAwait(false);
                    this.AddSamples(side, resampler.Process(mono), padding: false, synthetic: frame.IsSyntheticSilence);
                }

                this.AddSamples(side, resampler.Flush(), padding: false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (AudioSourceException)
            {
                // The device failed. Finish this side; the emitter pads it and the other side keeps flowing until stopped.
            }
            finally
            {
                lock (this.sync)
                {
                    side.Done = true;
                }

                this.signal.Writer.TryWrite(true);
            }
        }

        private async Task WaitForRoomAsync(Side side)
        {
            while (!this.stop.IsCancellationRequested)
            {
                lock (this.sync)
                {
                    if (side.Count < side.Capacity)
                    {
                        return;
                    }
                }

                await Task.Delay(TimeSpan.FromMilliseconds(5), this.options.Clock, this.stop.Token).ConfigureAwait(false);
            }
        }

        private void AddSamples(Side side, ReadOnlySpan<float> samples, bool padding, bool gap = false, bool synthetic = false)
        {
            lock (this.sync)
            {
                if (gap)
                {
                    side.GapPositions.Enqueue(side.Produced);
                }

                side.Add(samples, synthetic);
                if (!padding)
                {
                    side.LastDataTicks = this.options.Clock.GetTimestamp();
                }
            }

            this.signal.Writer.TryWrite(true);
        }

        private async Task EmitLoopAsync()
        {
            try
            {
                var wait = TimeSpan.FromMilliseconds(Math.Max(10, this.options.MaxSkew.TotalMilliseconds / 4));
                while (true)
                {
                    using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(this.stop.Token))
                    {
                        timeout.CancelAfter(wait);
                        try
                        {
                            await this.signal.Reader.ReadAsync(timeout.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (!this.stop.IsCancellationRequested)
                        {
                        }
                    }

                    this.stop.Token.ThrowIfCancellationRequested();
                    bool finished;
                    TaskCompletionSource? flush;
                    var batch = new List<AudioFrame>();
                    lock (this.sync)
                    {
                        flush = this.flushRequest;
                        this.flushRequest = null;
                        finished = this.micSide.Done && this.systemSide.Done;
                        this.PadStalledSide();
                        var flushAll = flush is not null || finished;
                        if (flushAll)
                        {
                            var longest = Math.Max(this.micSide.Count, this.systemSide.Count);
                            this.PadSide(this.micSide, longest);
                            this.PadSide(this.systemSide, longest);
                        }

                        this.DrainChunks(batch, flushAll);
                    }

                    foreach (var frame in batch)
                    {
                        await this.output.Writer.WriteAsync(frame, this.stop.Token).ConfigureAwait(false);
                    }

                    flush?.TrySetResult();
                    if (finished)
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                this.output.Writer.TryComplete();
            }
        }

        // Called under the lock. A side with no data for MaxSkew while the other has a chunk ready is padded to match.
        private void PadStalledSide()
        {
            var now = this.options.Clock.GetTimestamp();
            var micStalled = this.micSide.Count < this.chunk && this.systemSide.Count >= this.chunk &&
                this.options.Clock.GetElapsedTime(this.micSide.LastDataTicks, now) >= this.options.MaxSkew;
            var systemStalled = this.systemSide.Count < this.chunk && this.micSide.Count >= this.chunk &&
                this.options.Clock.GetElapsedTime(this.systemSide.LastDataTicks, now) >= this.options.MaxSkew;
            if (micStalled)
            {
                this.PadSide(this.micSide, this.systemSide.Count);
            }

            if (systemStalled)
            {
                this.PadSide(this.systemSide, this.micSide.Count);
            }
        }

        private void PadSide(Side side, int count)
        {
            var missing = count - side.Count;
            if (missing <= 0)
            {
                return;
            }

            side.PadTo(count);
            if (ReferenceEquals(side, this.micSide))
            {
                this.MicrophonePaddedSamples += missing;
            }
            else
            {
                this.SystemAudioPaddedSamples += missing;
            }
        }

        private void DrainChunks(List<AudioFrame> batch, bool flushAll)
        {
            while (true)
            {
                var available = Math.Min(this.micSide.Count, this.systemSide.Count);
                var take = available >= this.chunk ? this.chunk : flushAll ? available : 0;
                if (take == 0)
                {
                    return;
                }

                var a = new float[take];
                var b = new float[take];
                var micSynthetic = this.micSide.Take(take, a);
                var systemSynthetic = this.systemSide.Take(take, b);
                float[] interleaved;
                if (this.options.Layout == CombinedChannelLayout.Stereo)
                {
                    interleaved = new float[take * 2];
                    for (var i = 0; i < take; i++)
                    {
                        interleaved[2 * i] = a[i];
                        interleaved[(2 * i) + 1] = b[i];
                    }
                }
                else
                {
                    interleaved = new float[take];
                    for (var i = 0; i < take; i++)
                    {
                        interleaved[i] = Math.Clamp(a[i] + b[i], -1f, 1f);
                    }
                }

                var end = this.sampleOffset + take;
                var gapInFrame = false;
                foreach (var side in new[] { this.micSide, this.systemSide })
                {
                    while (side.GapPositions.Count > 0 && side.GapPositions.Peek() < end)
                    {
                        side.GapPositions.Dequeue();
                        gapInFrame = true;
                    }
                }

                if (gapInFrame)
                {
                    // A device lost samples here: skip a sequence number so the session records a gap.
                    this.sequence++;
                }

                batch.Add(AudioFrame.CopyFrom(interleaved, this.Format, this.sequence++, this.sampleOffset, micSynthetic && systemSynthetic));
                this.sampleOffset += take;
            }
        }
    }
}
