using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using PrimeDictate.Core.Audio;
using PrimeDictate.Core.Coordination;
using PrimeDictate.Core.Pipeline;
using PrimeDictate.Core.Providers;
using PrimeDictate.Core.Sessions;
using PrimeDictate.Core.Storage;
using PrimeDictate.Core.Transcripts;

namespace PrimeDictate.Core.Tests;

public sealed class LiveSourceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pd-live-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(this.root))
        {
            Directory.Delete(this.root, recursive: true);
        }
    }

    /// <param name="holdAfterFrames">With <paramref name="release"/>: stop delivering after this many frames until the task completes.</param>
    internal sealed class FakeSource(int channels, float[] interleaved, int holdAfterFrames = 0, Task? release = null) : IAudioSource
    {
        public ValueTask<IReadOnlyList<AudioInputDevice>> ListDevicesAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<AudioInputDevice>>([]);

        public ValueTask<IAudioCaptureLease> OpenAsync(string? deviceId, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IAudioCaptureLease>(new Lease(channels, interleaved, holdAfterFrames, release));

        private sealed class Lease(int channels, float[] data, int holdAfterFrames, Task? release) : IAudioCaptureLease
        {
            private readonly Channel<AudioFrame> queue = Channel.CreateUnbounded<AudioFrame>();

            public AudioFormat Format { get; } = new(16_000, channels, AudioSampleFormat.Float32);

            public string DeviceId => "fake";

            public string DeviceName => "fake";

            public async IAsyncEnumerable<AudioFrame> ReadFramesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
            {
                var step = 1600 * channels;
                long seq = 0, offset = 0;
                for (var i = 0; i < data.Length; i += step)
                {
                    if (release is not null && seq == holdAfterFrames)
                    {
                        await release.WaitAsync(cancellationToken);
                    }

                    var part = data.AsMemory(i, Math.Min(step, data.Length - i));
                    yield return AudioFrame.CopyFrom(part.Span, this.Format, seq++, offset);
                    offset += part.Length / channels;
                    await Task.Yield();
                }

                // Hold the stream open until the lease is disposed, like a live device.
                await foreach (var frame in this.queue.Reader.ReadAllAsync(cancellationToken))
                {
                    yield return frame;
                }
            }

            public ValueTask PauseAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

            public ValueTask ResumeAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

            public ValueTask DisposeAsync()
            {
                this.queue.Writer.TryComplete();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class SilentProvider : ITranscriptionProvider
    {
        public string ModelId => "fake:model";

        public TranscriptionProviderCapabilities Capabilities { get; } = new(true, LiveRecognitionMode.BufferedWindows, TimingCapabilities.SegmentTimestamps, false, null, TimeSpan.FromSeconds(30), ["en"], 16_000);

        public EffectiveRuntime Runtime { get; } = new("fake", "1", "cpu", "cpu", null);

        public ValueTask<IReadOnlyList<RecognizedSegment>> RecognizeWindowAsync(ReadOnlyMemory<float> samples, string? language, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<RecognizedSegment>>([new RecognizedSegment(TimeSpan.Zero, TimeSpan.FromSeconds(samples.Length / 16_000d), "hello", null, null, null, TimingProvenance.ApproximateChunk)]);

        public ValueTask<IStreamingRecognitionSession> StartStreamingAsync(string? language, bool diarize, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class GatedProvider(TaskCompletionSource gate) : ITranscriptionProvider
    {
        public string ModelId => "fake:model";

        public TranscriptionProviderCapabilities Capabilities { get; } = new(true, LiveRecognitionMode.BufferedWindows, TimingCapabilities.SegmentTimestamps, false, null, TimeSpan.FromSeconds(30), ["en"], 16_000);

        public EffectiveRuntime Runtime { get; } = new("fake", "1", "cpu", "cpu", null);

        public async ValueTask<IReadOnlyList<RecognizedSegment>> RecognizeWindowAsync(ReadOnlyMemory<float> samples, string? language, CancellationToken cancellationToken)
        {
            await gate.Task.WaitAsync(cancellationToken);
            return [new RecognizedSegment(TimeSpan.Zero, TimeSpan.FromSeconds(samples.Length / 16_000d), "hello", null, null, null, TimingProvenance.ApproximateChunk)];
        }

        public ValueTask<IStreamingRecognitionSession> StartStreamingAsync(string? language, bool diarize, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeStream(bool diarize, string text) : IStreamingRecognitionSession
    {
        private readonly Channel<StreamingUpdate> updates = Channel.CreateUnbounded<StreamingUpdate>();
        private long voicedStart = -1;
        private long voicedEnd;
        private int committed;

        public bool Diarize { get; } = diarize;

        public bool Disposed { get; private set; }

        public long VoicedSamples { get; private set; }

        public ValueTask WriteAsync(AudioFrame frame, CancellationToken cancellationToken)
        {
            foreach (var x in frame.Samples.Span)
            {
                if (Math.Abs(x) > 0.01f)
                {
                    this.VoicedSamples++;
                }
            }

            if (frame.Samples.Span.ToArray().Any(x => Math.Abs(x) > 0.01f))
            {
                if (this.voicedStart < 0)
                {
                    this.voicedStart = frame.SampleOffset;
                }

                this.voicedEnd = frame.EndSampleOffset;
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask CommitAsync(CancellationToken cancellationToken)
        {
            if (this.voicedStart >= 0)
            {
                var segment = new RecognizedSegment(
                    TimeSpan.FromSeconds(this.voicedStart / 16_000d),
                    TimeSpan.FromSeconds(this.voicedEnd / 16_000d),
                    text,
                    null,
                    null,
                    this.Diarize ? "speaker-1" : null,
                    TimingProvenance.ApproximateChunk);
                this.updates.Writer.TryWrite(new StreamingUpdate($"{this.committed++}.0", segment, true));
                this.voicedStart = -1;
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask CompleteAsync(CancellationToken cancellationToken)
        {
            this.updates.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }

        public IAsyncEnumerable<StreamingUpdate> ReadUpdatesAsync(CancellationToken cancellationToken) => this.updates.Reader.ReadAllAsync(cancellationToken);

        public ValueTask DisposeAsync()
        {
            this.Disposed = true;
            this.updates.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeStreamProvider(int failOnCall = 0, string micText = "hello from mic", string systemText = "remote talk") : ITranscriptionProvider
    {
        private int calls;

        public List<FakeStream> Streams { get; } = [];

        public string ModelId => "fake:stream";

        public TranscriptionProviderCapabilities Capabilities { get; } = new(true, LiveRecognitionMode.NativeStreaming, TimingCapabilities.SegmentTimestamps, true, 8, TimeSpan.FromSeconds(30), ["en"], 16_000);

        public EffectiveRuntime Runtime { get; } = new("fake", "1", "cpu", "cpu", null);

        public ValueTask<IReadOnlyList<RecognizedSegment>> RecognizeWindowAsync(ReadOnlyMemory<float> samples, string? language, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<IStreamingRecognitionSession> StartStreamingAsync(string? language, bool diarize, CancellationToken cancellationToken)
        {
            if (++this.calls == failOnCall)
            {
                throw new InvalidOperationException("worker busy");
            }

            var stream = new FakeStream(diarize, diarize ? systemText : micText);
            this.Streams.Add(stream);
            return ValueTask.FromResult<IStreamingRecognitionSession>(stream);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Consumer : IMicrophoneConsumer
    {
        public string Name => "wake word";

        public bool IsBusy => false;

        public bool IsActive => true;

        public int Suspended { get; private set; }

        public ValueTask SuspendAsync(string reason, CancellationToken cancellationToken)
        {
            this.Suspended++;
            return ValueTask.CompletedTask;
        }

        public ValueTask ResumeAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private static float[] Stereo(double seconds, float left, float right)
    {
        var n = (int)(seconds * 16_000);
        var data = new float[n * 2];
        for (var i = 0; i < n; i++)
        {
            var wave = MathF.Sin(i * 0.05f);
            data[2 * i] = left * wave;
            data[(2 * i) + 1] = right * wave;
        }

        return data;
    }

    private async Task<(TranscriptDocument Doc, Consumer Consumer)> RunAsync(TranscriptSourceType source, int channels, float[] audio)
    {
        await using var store = SqliteTranscriptionSessionStore.Create(new AppDataPaths(this.root));
        await store.InitializeAsync(default);
        var coordinator = new MicrophoneCoordinator();
        var consumer = new Consumer();
        coordinator.Register(consumer);
        var options = new LiveSessionOptions(
            new TranscriptionSessionOptions("fake:model", null, "cpu", "en", null, AudioRetention.KeepAudio, DownmixMode.Average, null, null, null),
            "t", store.GetSessionMediaDirectory, TimeSpan.FromSeconds(60), null, source);
        await using var session = new LiveTranscriptionSession(new FakeSource(channels, audio), new SilentProvider(), coordinator, new ModelLeaseScheduler(), store, options);
        await session.StartAsync(default);
        await Task.Delay(400);
        await session.StopAsync(default);
        return (session.Host!.Document, consumer);
    }

    [Fact]
    public async Task Slow_recognition_never_stalls_capture_or_drops_audio()
    {
        // 60 s of continuous speech-level audio fed as fast as the source allows: far more than the old
        // 256-item queue, with recognition completely blocked.
        var audio = Stereo(60, 0.5f, 0.5f).Where((_, i) => i % 2 == 0).ToArray();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var store = SqliteTranscriptionSessionStore.Create(new AppDataPaths(this.root));
        await store.InitializeAsync(default);
        var options = new LiveSessionOptions(
            new TranscriptionSessionOptions("fake:model", null, "cpu", "en", null, AudioRetention.KeepAudio, DownmixMode.Average, null, null, null),
            "t", store.GetSessionMediaDirectory, TimeSpan.FromMilliseconds(50), null, TranscriptSourceType.Microphone);
        await using var session = new LiveTranscriptionSession(new FakeSource(1, audio), new GatedProvider(gate), new MicrophoneCoordinator(), new ModelLeaseScheduler(), store, options);
        await session.StartAsync(default);

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (session.Elapsed < TimeSpan.FromSeconds(59.9) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.True(session.Elapsed >= TimeSpan.FromSeconds(59.9), $"capture stalled at {session.Elapsed}");
        Assert.True(session.Backlog > TimeSpan.FromSeconds(10), "backlog should be reported while recognition is blocked");

        gate.SetResult();
        await session.StopAsync(default);
        Assert.Equal(TimeSpan.Zero, session.Backlog);
        Assert.NotEmpty(session.Host!.Document.ActiveSegments);
        var wav = Assert.Single(session.Host.Document.Audio);
        Assert.True(new FileInfo(wav.Path).Length >= 60 * 16_000 * 2, "recording lost samples");
    }

    private async Task<(TranscriptDocument Doc, FakeStreamProvider Provider)> RunMeetingAsync(FakeStreamProvider provider)
    {
        // Left (microphone) talks 0-2 s, right (system audio) talks 1-3 s, so they overlap for a second; then silence.
        var frames = 6 * 16_000;
        var data = new float[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            var t = i / 16_000d;
            data[2 * i] = t < 2 ? 0.3f * MathF.Sin(i * 0.05f) : 0f;
            data[(2 * i) + 1] = t is >= 1 and < 3 ? 0.3f * MathF.Sin(i * 0.07f) : 0f;
        }

        await using var store = SqliteTranscriptionSessionStore.Create(new AppDataPaths(this.root));
        await store.InitializeAsync(default);
        var options = new LiveSessionOptions(
            new TranscriptionSessionOptions("fake:stream", null, "cpu", "en-US", null, AudioRetention.KeepAudio, DownmixMode.Average, null, null, null),
            "t", store.GetSessionMediaDirectory, TimeSpan.FromSeconds(60), null, TranscriptSourceType.Meeting);
        await using var session = new LiveTranscriptionSession(new FakeSource(2, data), provider, new MicrophoneCoordinator(), new ModelLeaseScheduler(), store, options);
        await session.StartAsync(default);
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (session.Elapsed < TimeSpan.FromSeconds(5.9) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        await session.StopAsync(default);
        return (session.Host!.Document, provider);
    }

    [Fact]
    public async Task Meetings_run_the_microphone_and_system_audio_as_separate_streams_that_overlap_in_time()
    {
        var (doc, provider) = await this.RunMeetingAsync(new FakeStreamProvider());

        Assert.Equal(2, provider.Streams.Count);
        Assert.False(provider.Streams[0].Diarize); // microphone: one local person, no diarization needed
        Assert.True(provider.Streams[1].Diarize);  // system audio: remote people, speaker detection on
        // Each stream heard only its own channel (about 2 s of speech each), not the 3 s blended mix.
        Assert.InRange(provider.Streams[0].VoicedSamples, 30_000, 36_000);
        Assert.InRange(provider.Streams[1].VoicedSamples, 30_000, 36_000);

        var lines = doc.ActiveSegments.ToList();
        var you = Assert.Single(lines, l => l.Speakers[0].SpeakerId == "local");
        var remote = Assert.Single(lines, l => l.Speakers[0].SpeakerId == "speaker-1");
        Assert.Equal("hello from mic", you.RawText);
        Assert.Equal("remote talk", remote.RawText);
        Assert.True(you.Start < remote.End && remote.Start < you.End, "the two rows overlap in time");
        Assert.Equal("You", doc.Speakers.Single(s => s.Id == "local").Name);
        Assert.Equal("Speaker 1", doc.Speakers.Single(s => s.Id == "speaker-1").Name);
        Assert.Contains(doc.Notes, n => n.Contains("two separate streams"));
    }

    [Fact]
    public async Task Microphone_lines_that_repeat_the_system_audio_are_hidden_but_kept()
    {
        // The microphone hears the speakers: same words out of both streams, at slightly different times.
        var (doc, _) = await this.RunMeetingAsync(new FakeStreamProvider(micText: "let it do its thing", systemText: "yeah it's still setting up let it do its thing"));

        var mic = Assert.Single(doc.ActiveSegments, l => l.Speakers[0].SpeakerId == "local");
        Assert.Equal("let it do its thing", mic.RawText);   // recognized text is kept
        Assert.Equal(string.Empty, mic.DisplayText);        // but it is hidden
        Assert.Contains(doc.ActiveSegments, l => l.Speakers[0].SpeakerId == "speaker-1" && l.DisplayText.Length > 0);
        Assert.Contains(doc.Notes, n => n.Contains("hears the speakers"));
        var text = PrimeDictate.Core.Export.TranscriptExporter.Export(doc, new PrimeDictate.Core.Export.ExportOptions(PrimeDictate.Core.Export.ExportFormat.Text));
        Assert.DoesNotContain("let it do its thing\n", text.Replace("yeah it's still setting up let it do its thing", string.Empty));
    }

    [Fact]
    public async Task Different_words_on_the_two_streams_are_both_kept()
    {
        var (doc, _) = await this.RunMeetingAsync(new FakeStreamProvider());

        Assert.All(doc.ActiveSegments, l => Assert.NotEmpty(l.DisplayText));
        Assert.Equal(2, doc.ActiveSegments.Count());
    }

    [Fact]
    public async Task Meetings_fall_back_to_one_mixed_stream_when_a_second_stream_cannot_open()
    {
        var (doc, provider) = await this.RunMeetingAsync(new FakeStreamProvider(failOnCall: 2));

        Assert.True(provider.Streams[0].Disposed); // the microphone stream that was opened first is released
        Assert.True(provider.Streams[1].Diarize);  // the single mixed stream
        Assert.Equal(2, provider.Streams.Count);
        Assert.Contains(doc.Notes, n => n.Contains("could not be opened"));
        Assert.Contains(doc.ActiveSegments, l => l.RawText == "remote talk");
        // The mixed stream carries the blend of both channels: about 3 s of speech.
        Assert.InRange(provider.Streams[1].VoicedSamples, 44_000, 52_000);
    }

    [Fact]
    public async Task Meeting_sessions_keep_stereo_audio_and_are_labeled_as_meetings()
    {
        var (doc, consumer) = await this.RunAsync(TranscriptSourceType.Meeting, 2, Stereo(1.0, 0.5f, 0.0f));

        Assert.Equal(TranscriptSourceType.Meeting, doc.SourceType);
        Assert.Equal("meeting", doc.Media!.ContainerFormat);
        Assert.Equal("left=microphone, right=system audio", doc.Media.ChannelMapping);
        Assert.Equal(1, consumer.Suspended);
        var audio = Assert.Single(doc.Audio);
        var frames = new List<AudioFrame>();
        await foreach (var f in new WavAudioDecoder().DecodeAsync(audio.Path, 0, default))
        {
            frames.Add(f);
        }

        Assert.All(frames, f => Assert.Equal(2, f.Format.Channels));
        var samples = frames.SelectMany(f => f.Samples.ToArray()).ToArray();
        var leftEnergy = samples.Where((_, i) => i % 2 == 0).Sum(Math.Abs);
        var rightEnergy = samples.Where((_, i) => i % 2 == 1).Sum(Math.Abs);
        Assert.True(leftEnergy > 100 * Math.Max(rightEnergy, 1e-3f));
        Assert.NotEmpty(doc.ActiveSegments);
    }

    [Fact]
    public async Task System_audio_only_sessions_do_not_take_the_microphone()
    {
        var (doc, consumer) = await this.RunAsync(TranscriptSourceType.SystemAudio, 1, Stereo(1.0, 0.5f, 0.5f).Where((_, i) => i % 2 == 0).ToArray());

        Assert.Equal(TranscriptSourceType.SystemAudio, doc.SourceType);
        Assert.Equal("system-audio", doc.Media!.ContainerFormat);
        Assert.Equal(0, consumer.Suspended);
    }

    [Fact]
    public async Task Microphone_sessions_still_pause_background_microphone_users()
    {
        var (doc, consumer) = await this.RunAsync(TranscriptSourceType.Microphone, 1, Stereo(1.0, 0.5f, 0.5f).Where((_, i) => i % 2 == 0).ToArray());

        Assert.Equal(TranscriptSourceType.Microphone, doc.SourceType);
        Assert.Equal(1, consumer.Suspended);
    }
}
