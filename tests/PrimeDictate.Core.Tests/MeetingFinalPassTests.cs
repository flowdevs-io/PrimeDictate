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

    public void Dispose()
    {
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
        var store = SqliteTranscriptionSessionStore.Create(new AppDataPaths(this.root));
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
