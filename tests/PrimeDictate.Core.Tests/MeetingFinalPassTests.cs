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
    internal sealed class ChannelProvider(List<string> calls) : ITranscriptionProvider
    {
        public string ModelId => "nemotron:fake";

        public TranscriptionProviderCapabilities Capabilities { get; } = new(true, LiveRecognitionMode.NativeStreaming, TimingCapabilities.WordTimestamps, false, null, TimeSpan.FromSeconds(30), ["en"], 16_000);

        public EffectiveRuntime Runtime { get; } = new("fake", "1", "cuda:0", "cuda:0", null);

        public ValueTask<IReadOnlyList<RecognizedSegment>> RecognizeWindowAsync(ReadOnlyMemory<float> samples, string? language, CancellationToken cancellationToken)
        {
            // A faithful recognizer: one word per half second that holds sound, placed where the sound is. The letter says how
            // loud it was (m = microphone, a = 0.2 amplitude, b = quieter), so a test can tell whose audio a word came from.
            var span = samples.Span;
            var peak = span.ToArray().Max(Math.Abs);
            lock (calls)
            {
                calls.Add(peak > 0.25f ? "mic" : "system");
            }

            var timings = new List<WordTiming>();
            for (var block = 0; block * 8_000 < span.Length; block++)
            {
                var part = span.Slice(block * 8_000, Math.Min(8_000, span.Length - (block * 8_000)));
                var loud = part.ToArray().Max(Math.Abs);
                if (loud < 0.02f)
                {
                    continue;
                }

                var letter = loud > 0.3f ? "m" : loud > 0.15f ? "a" : "b";
                timings.Add(new WordTiming(letter, TimeSpan.FromSeconds(block * 0.5), TimeSpan.FromSeconds((block * 0.5) + 0.4), null, TimingProvenance.Model));
            }

            if (timings.Count == 0)
            {
                return ValueTask.FromResult<IReadOnlyList<RecognizedSegment>>([]);
            }

            var segment = new RecognizedSegment(timings[0].Start, timings[^1].End, string.Join(' ', timings.Select(w => w.Text)), timings, null, null, TimingProvenance.Model);
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
            samples[(i * 2) + 1] = t is >= 2 and < 5 ? 0.2f * tone : t is >= 5.4 and < 8 ? 0.1f * tone : 0f;
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
        Task.FromResult<(DiarizationOverlay?, string?)>((new DiarizationOverlay([new("speaker_1", 2.0, 5.0), new("speaker_2", 5.4, 8.2)]), null));

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
        var mic = lines.Single(l => l.Id.StartsWith("fm", StringComparison.Ordinal));
        Assert.All(mic.Words!, w => Assert.StartsWith("m", w.Text));
        Assert.InRange(mic.Start.TotalSeconds, 8.9, 9.6);
        var system = lines.Where(l => l.Id.StartsWith("ft", StringComparison.Ordinal)).OrderBy(l => l.Start).ToList();
        Assert.Equal(["speaker-1", "speaker-2"], system.Select(l => l.Speakers[0].SpeakerId));
        // Each word lands on the diarizer segment of its own speaker, at the time it was spoken.
        Assert.All(system[0].Words!, w => Assert.StartsWith("a", w.Text));
        Assert.All(system[1].Words!, w => Assert.StartsWith("b", w.Text));
        Assert.All(system[0].Words!, w => Assert.InRange(w.Start.TotalSeconds, 1.7, 5.2));
        Assert.All(system[1].Words!, w => Assert.InRange(w.Start.TotalSeconds, 5.1, 8.5));
        Assert.InRange(system[0].Words!.Count, 6, 7);
        Assert.Equal(["You", "Speaker 1", "Speaker 2"], new[] { "local", "speaker-1", "speaker-2" }.Select(id => doc.Speakers.Single(s => s.Id == id).Name));

        // The live draft is still stored as version 1.
        Assert.Contains(doc.Segments, s => s.ResultVersion == 1 && s.RawText == "live draft text");
        Assert.Equal(2, result.SpeakerCount);
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
    public async Task Words_heard_by_two_overlapping_turns_are_shown_once()
    {
        Directory.CreateDirectory(this.root);
        var path = Path.Combine(this.root, "stereo.wav");
        using (var writer = new WavFileWriter(path, 16_000, 2))
        {
            var samples = new float[10 * 16_000 * 2];
            for (var i = 0; i < 10 * 16_000; i++)
            {
                samples[(i * 2) + 1] = i / 16_000d is >= 2 and < 8 ? 0.1f * MathF.Sin(i * 0.07f) : 0f;
            }

            writer.Write(samples);
        }

        var store = SqliteTranscriptionSessionStore.Create(new AppDataPaths(this.root));
        await store.InitializeAsync(CancellationToken.None);
        var doc = NewDocument() with { SourceType = TranscriptSourceType.Meeting, Duration = TimeSpan.FromSeconds(10) };
        doc = TranscriptDocumentReducer.Apply(doc, new SessionStarted(doc.SessionId, Run(1, "whisper-onnx/tiny.en")), Now);
        var host = new SessionDocumentHost(doc, store);

        // Speaker 3 talks 2-5 s, speaker 4 talks 4-8 s: they overlap for a second, and both turns hear that second.
        var result = await new MeetingFinalPass(new ModelLeaseScheduler()).RunAsync(
            host, path, new MeetingFinalPassTests.ChannelProvider([]), _ => Task.FromResult<(DiarizationOverlay?, string?)>((new DiarizationOverlay([new("speaker_3", 2.0, 5.0), new("speaker_4", 4.0, 8.0)]), null)), "en-US", this.root, null, default);

        await store.DisposeAsync();
        SqliteConnection.ClearAllPools();
        var rows = host.Document.ActiveSegments.Where(l => l.Id.StartsWith("ft", StringComparison.Ordinal)).OrderBy(l => l.Start).ToList();
        Assert.Equal(["speaker-3", "speaker-4"], rows.Select(r => r.Speakers[0].SpeakerId));
        var visible = rows.SelectMany(r => r.Words!.Where(w => !w.Hidden)).ToList();
        // 2-8 s is twelve half-second blocks; the padded turns hold about 15 between them, and the shared ones are hidden once.
        Assert.InRange(visible.Count, 11, 14);
        Assert.Contains(rows.SelectMany(r => r.Words!), w => w.Hidden);
        Assert.Contains("system turns:", result.ChannelReport);
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
