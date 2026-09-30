using Microsoft.Data.Sqlite;
using PrimeDictate.Core.Audio;
using PrimeDictate.Core.Coordination;
using PrimeDictate.Core.Pipeline;
using PrimeDictate.Core.Providers;
using PrimeDictate.Core.Storage;
using PrimeDictate.Core.Transcripts;
using PrimeDictate.Platforms.Nemotron;
using static PrimeDictate.Core.Tests.TestData;

namespace PrimeDictate.Core.Tests;

public sealed class MeetingFinalPassTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pd-final-pass", Guid.NewGuid().ToString("N"));

    private SqliteTranscriptionSessionStore? store;

    public void Dispose()
    {
        // The store keeps sessions.db open (and the connection pool keeps it too); Windows will not delete an open file.
        this.store?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(this.root))
        {
            Directory.Delete(this.root, recursive: true);
        }
    }

    /// <summary>Tells the two channels apart by loudness: the microphone is loud, the system audio quieter.</summary>
    private sealed class ChannelProvider(List<string> calls) : ITranscriptionProvider
    {
        public string ModelId => "nemotron:fake";

        public TranscriptionProviderCapabilities Capabilities { get; } = new(true, LiveRecognitionMode.NativeStreaming, TimingCapabilities.WordTimestamps, false, null, TimeSpan.FromSeconds(30), ["en"], 16_000);

        public EffectiveRuntime Runtime { get; } = new("fake", "1", "cuda:0", "cuda:0", null);

        public ValueTask<IReadOnlyList<RecognizedSegment>> RecognizeWindowAsync(ReadOnlyMemory<float> samples, string? language, CancellationToken cancellationToken)
        {
            var peak = samples.Span.ToArray().Max(Math.Abs);
            lock (calls)
            {
                calls.Add(peak > 0.25f ? "mic" : "system");
            }

            // Words are placed where the tone is: the microphone speaks at 9-10.5 s, the system audio at 2-8 s.
            var words = peak > 0.25f
                ? new[] { ("hello", 9.0), ("there", 9.5), ("yeah", 10.0) }
                : Enumerable.Range(0, 12).Select(i => ($"word{i}", 2.0 + (i * 0.5))).ToArray();
            var timings = words.Select(w => new WordTiming(w.Item1, TimeSpan.FromSeconds(w.Item2), TimeSpan.FromSeconds(w.Item2 + 0.4), null, TimingProvenance.Model)).ToList();
            var segment = new RecognizedSegment(timings[0].Start, timings[^1].End, string.Join(' ', words.Select(w => w.Item1)), timings, null, null, TimingProvenance.Model);
            return ValueTask.FromResult<IReadOnlyList<RecognizedSegment>>([segment]);
        }

        public ValueTask<IStreamingRecognitionSession> StartStreamingAsync(string? language, bool diarize, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private string WriteStereo()
    {
        Directory.CreateDirectory(this.root);
        var path = Path.Combine(this.root, "recording-16k-stereo.wav");
        using var writer = new WavFileWriter(path, 16_000, 2);
        var seconds = 12;
        var samples = new float[seconds * 16_000 * 2];
        for (var i = 0; i < seconds * 16_000; i++)
        {
            var t = i / 16_000d;
            var tone = MathF.Sin(i * 0.07f);
            samples[i * 2] = t is >= 9 and < 11 ? 0.4f * tone : 0f;
            samples[(i * 2) + 1] = t is >= 2 and < 8 ? 0.2f * tone : 0f;
        }

        writer.Write(samples);
        return path;
    }

    private async Task<SessionDocumentHost> DraftAsync()
    {
        var store = this.store = SqliteTranscriptionSessionStore.Create(new AppDataPaths(this.root));
        await store.InitializeAsync(CancellationToken.None);
        var doc = NewDocument() with { SourceType = TranscriptSourceType.Meeting, Duration = TimeSpan.FromSeconds(12) };
        doc = TranscriptDocumentReducer.Apply(doc, new SessionStarted(doc.SessionId, Run(1, "whisper-onnx/tiny.en")), Now);
        doc = TranscriptDocumentReducer.Apply(doc, new SegmentFinalized(doc.SessionId, Segment("u0", "live draft text", 2, 9)), Now);
        doc = TranscriptDocumentReducer.Apply(doc, new SessionCompleted(doc.SessionId, TimeSpan.FromSeconds(12)), Now);
        return new SessionDocumentHost(doc, store);
    }

    private static Task<(DiarizationOverlay?, string?)> TwoSpeakers(CancellationToken _) =>
        Task.FromResult<(DiarizationOverlay?, string?)>((new DiarizationOverlay([new("speaker_1", 2.0, 5.0), new("speaker_2", 4.8, 8.2)]), null));

    [Fact]
    public async Task The_final_pass_replaces_the_draft_with_you_and_diarized_system_lines_and_keeps_the_draft_as_the_earlier_result()
    {
        var host = await this.DraftAsync();
        var calls = new List<string>();

        var result = await new MeetingFinalPass(new ModelLeaseScheduler()).RunAsync(host, this.WriteStereo(), new ChannelProvider(calls), TwoSpeakers, "en-US", this.root, null, default);

        var doc = host.Document;
        Assert.Equal(2, result.ResultVersion);
        Assert.Equal(2, doc.ActiveResultVersion);
        Assert.Equal(TranscriptSessionStatus.Completed, doc.Status);
        Assert.Contains("mic", calls);
        Assert.Contains("system", calls);

        var lines = doc.ActiveSegments.ToList();
        Assert.Equal(3, lines.Count);
        Assert.Equal("local", lines.Single(l => l.Id.StartsWith("fm", StringComparison.Ordinal)).Speakers[0].SpeakerId);
        Assert.Equal("hello there yeah", lines.Single(l => l.Id.StartsWith("fm", StringComparison.Ordinal)).DisplayText);
        var system = lines.Where(l => l.Id.StartsWith("fs", StringComparison.Ordinal)).ToList();
        Assert.Equal(["speaker-1", "speaker-2"], system.Select(l => l.Speakers[0].SpeakerId));
        Assert.Equal("word0 word1 word2 word3 word4 word5", system[0].DisplayText);
        Assert.Equal("word6 word7 word8 word9 word10 word11", system[1].DisplayText);
        Assert.Equal(["You", "Speaker 1", "Speaker 2"], new[] { "local", "speaker-1", "speaker-2" }.Select(id => doc.Speakers.Single(s => s.Id == id).Name));

        // The live draft is still stored as version 1.
        Assert.Contains(doc.Segments, s => s.ResultVersion == 1 && s.RawText == "live draft text");
        Assert.Equal(2, result.SpeakerCount);
        Assert.True(result.OverlapSeconds > 0.15);
        Assert.True(File.Exists(Path.Combine(this.root, DiarizationOverlay.FileName)));
        Assert.Equal(["speaker-1", "speaker-2"], result.Overlay!.MapTo(doc).Select(b => b.SpeakerId).Distinct());
    }

    [Fact]
    public async Task Without_diarizer_output_the_system_lines_are_remote_and_the_problem_is_reported()
    {
        var host = await this.DraftAsync();

        var result = await new MeetingFinalPass(new ModelLeaseScheduler()).RunAsync(
            host, this.WriteStereo(), new ChannelProvider([]), _ => Task.FromResult<(DiarizationOverlay?, string?)>((null, "the diarizer exited with code 2")), null, this.root, null, default);

        var system = host.Document.ActiveSegments.Where(l => l.Id.StartsWith("fs", StringComparison.Ordinal)).ToList();
        Assert.Single(system);
        Assert.Equal("remote", system[0].Speakers[0].SpeakerId);
        Assert.Equal("the diarizer exited with code 2", result.DiarizerProblem);
        Assert.Null(result.Overlay);
    }

    [Fact]
    public async Task A_failing_recognizer_leaves_the_draft_untouched()
    {
        var host = await this.DraftAsync();
        var before = host.Document;

        await Assert.ThrowsAsync<InvalidOperationException>(() => new MeetingFinalPass(new ModelLeaseScheduler()).RunAsync(
            host, this.WriteStereo(), new BrokenProvider(), TwoSpeakers, null, this.root, null, default));

        Assert.Equal(1, host.Document.ActiveResultVersion);
        Assert.Equal(before.Segments.Count, host.Document.Segments.Count);
        Assert.Equal(TranscriptSessionStatus.Completed, host.Document.Status);
    }

    private sealed class BrokenProvider : ITranscriptionProvider
    {
        public string ModelId => "nemotron:broken";

        public TranscriptionProviderCapabilities Capabilities { get; } = new(true, LiveRecognitionMode.NativeStreaming, TimingCapabilities.WordTimestamps, false, null, TimeSpan.FromSeconds(30), ["en"], 16_000);

        public EffectiveRuntime Runtime { get; } = new("fake", "1", "cpu", "cpu", null);

        public ValueTask<IReadOnlyList<RecognizedSegment>> RecognizeWindowAsync(ReadOnlyMemory<float> samples, string? language, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("worker went away");

        public ValueTask<IStreamingRecognitionSession> StartStreamingAsync(string? language, bool diarize, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

public sealed class SystemChannelTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pd-syschan", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(this.root))
        {
            Directory.Delete(this.root, recursive: true);
        }
    }

    /// <summary>Loud speech-like right channel (rms about 0.06, peak 0.84) and a very quiet left one.</summary>
    private string LoudRightQuietLeft(int seconds = 8)
    {
        Directory.CreateDirectory(this.root);
        var path = Path.Combine(this.root, "stereo.wav");
        using var writer = new WavFileWriter(path, 16_000, 2);
        var samples = new float[seconds * 16_000 * 2];
        for (var i = 0; i < seconds * 16_000; i++)
        {
            samples[i * 2] = 0.003f * MathF.Sin(i * 0.05f);
            samples[(i * 2) + 1] = 0.84f * MathF.Sin(i * 0.07f) * (0.5f + (0.5f * MathF.Sin(i * 0.0004f)));
        }

        writer.Write(samples);
        return path;
    }

    [Fact]
    public async Task The_extracted_file_is_the_right_channel_at_16k_mono_16_bit()
    {
        var stereo = this.LoudRightQuietLeft();
        var mono = Path.Combine(this.root, "mono.wav");

        await NemotronDiarizer.ExtractChannelAsync(stereo, 1, mono, default);

        var probe = await new WavAudioDecoder().ProbeAsync(mono, default);
        var stream = probe.AudioStreams[0];
        Assert.Equal(1, stream.Channels);
        Assert.Equal(16_000, stream.SampleRate);
        Assert.Equal("pcm_s16le", stream.Codec);
        Assert.Equal(8.0, probe.Duration!.Value.TotalSeconds, 2);
        float peak = 0;
        await foreach (var frame in new WavAudioDecoder().DecodeAsync(mono, 0, default))
        {
            peak = Math.Max(peak, frame.Samples.Span.ToArray().Max(Math.Abs));
        }

        Assert.InRange(peak, 0.8f, 0.86f);
    }

    [Fact]
    public async Task The_recognizer_gets_every_loud_system_window_and_the_report_says_so()
    {
        var stereo = this.LoudRightQuietLeft(40);
        var provider = new SeenProvider();
        var store = SqliteTranscriptionSessionStore.Create(new AppDataPaths(this.root));
        await store.InitializeAsync(CancellationToken.None);
        var doc = NewDocument() with { SourceType = TranscriptSourceType.Meeting, Duration = TimeSpan.FromSeconds(40) };
        doc = TranscriptDocumentReducer.Apply(doc, new SessionStarted(doc.SessionId, Run(1, "whisper-onnx/tiny.en")), Now);
        var host = new SessionDocumentHost(doc, store);

        var result = await new MeetingFinalPass(new ModelLeaseScheduler()).RunAsync(host, stereo, provider, _ => Task.FromResult<(DiarizationOverlay?, string?)>((null, null)), "en-US", this.root, null, default);

        await store.DisposeAsync();
        SqliteConnection.ClearAllPools();
        Assert.Equal(2, provider.LoudWindows); // 40 s = two windows of the right channel
        Assert.Equal(0, provider.QuietSent);   // the quiet left channel is below the silence threshold
        Assert.Contains("system: rms", result.ChannelReport);
        Assert.Contains("2 of 2 windows sent", result.ChannelReport);
        Assert.Contains("microphone: rms", result.ChannelReport);
        Assert.Contains("0 of 2 windows sent", result.ChannelReport);
    }

    private sealed class SeenProvider : ITranscriptionProvider
    {
        public int LoudWindows;
        public int QuietSent;

        public string ModelId => "nemotron:fake";

        public TranscriptionProviderCapabilities Capabilities { get; } = new(true, LiveRecognitionMode.NativeStreaming, TimingCapabilities.WordTimestamps, false, null, TimeSpan.FromSeconds(30), ["en"], 16_000);

        public EffectiveRuntime Runtime { get; } = new("fake", "1", "cuda:0", "cuda:0", null);

        public ValueTask<IReadOnlyList<RecognizedSegment>> RecognizeWindowAsync(ReadOnlyMemory<float> samples, string? language, CancellationToken cancellationToken)
        {
            if (samples.Span.ToArray().Max(Math.Abs) > 0.5f)
            {
                LoudWindows++;
            }
            else
            {
                QuietSent++;
            }

            var word = new WordTiming("hi", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1.3), null, TimingProvenance.Model);
            return ValueTask.FromResult<IReadOnlyList<RecognizedSegment>>([new RecognizedSegment(word.Start, word.End, "hi", [word], null, null, TimingProvenance.Model)]);
        }

        public ValueTask<IStreamingRecognitionSession> StartStreamingAsync(string? language, bool diarize, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public void Diarizer_json_is_told_apart_from_no_speech_and_tolerates_log_lines()
    {
        Assert.False(DiarizationOverlay.TryParseJson("not json", out _));
        Assert.False(DiarizationOverlay.TryParseJson("{\"oops\":1}", out _));
        Assert.True(DiarizationOverlay.TryParseJson("{\"segments\":[]}", out var none));
        Assert.Empty(none);
        Assert.True(DiarizationOverlay.TryParseJson("loading model...\n{\"segments\":[{\"start\":0.5,\"end\":2.0,\"speaker\":1}]}", out var one));
        Assert.Single(one);
    }
}
