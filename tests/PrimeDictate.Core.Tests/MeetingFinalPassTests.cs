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
            samples[(i * 2) + 1] = t is >= 2 and < 5 ? 0.2f * tone : t is >= 5.6 and < 8 ? 0.1f * tone : 0f;
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
        var system = lines.Where(l => l.Id.StartsWith("fs", StringComparison.Ordinal)).OrderBy(l => l.Start).ToList();
        Assert.Equal(["speaker-1", "speaker-2"], system.Select(l => l.Speakers[0].SpeakerId));
        // Each word lands on the diarizer segment of its own speaker, at the time it was spoken.
        Assert.All(system[0].Words!, w => Assert.StartsWith("a", w.Text));
        Assert.All(system[1].Words!, w => Assert.StartsWith("b", w.Text));
        Assert.All(system[0].Words!, w => Assert.InRange(w.Start.TotalSeconds, 1.7, 5.2));
        Assert.All(system[1].Words!, w => Assert.InRange(w.Start.TotalSeconds, 5.4, 8.5));
        Assert.Equal(6, system[0].Words!.Count);
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

    /// <summary>
    /// Word times (seconds) from Justin's 9:14 PM record-only meeting on build cbde440, 47.5 s. Only the times are kept: the
    /// microphone said one 12 s stretch with one 1.04 s pause, then two quick words at 40.8 s; one remote speaker talked in
    /// three diarizer turns. The old pass put all three turns in one row, 24.5-46.9 s, around the You line at 40.8 s.
    /// </summary>
    private static class Meeting914
    {
        public const double Seconds = 47.5;

        public static readonly (double Start, double End)[] Microphone =
        [
            (2.24, 2.32), (2.56, 2.64), (2.72, 2.80), (2.80, 2.96), (3.60, 3.68), (4.24, 4.32), (4.48, 4.56), (4.56, 4.72), (5.12, 5.20),
            (5.20, 5.28), (5.28, 5.36), (5.44, 5.52), (5.68, 5.76), (6.08, 6.16), (6.32, 6.40), (6.48, 6.56), (6.72, 6.80), (6.80, 7.04),
            (7.12, 7.20), (7.28, 7.36), (7.60, 7.68), (7.84, 7.92), (7.92, 8.00), (8.00, 8.08), (8.08, 8.16), (8.32, 8.72), (8.72, 9.04),
            (9.12, 9.20), (9.28, 9.44), (9.52, 9.60), (9.60, 9.68), (9.76, 9.84), (9.92, 10.08),
            (11.12, 11.20), (11.36, 11.44), (11.68, 11.76), (11.76, 11.84), (11.84, 11.92), (11.92, 12.00), (12.00, 12.08), (12.08, 12.16),
            (12.24, 12.32), (12.32, 12.40), (12.40, 12.48), (12.64, 12.72), (12.96, 13.04), (13.04, 13.12), (13.12, 13.20), (13.20, 13.28),
            (13.52, 13.60), (13.60, 13.84), (14.08, 14.16), (14.32, 14.40), (14.48, 14.64),
            (40.755, 40.835), (40.915, 40.995)
        ];

        public static readonly (double Start, double End)[] System =
        [
            (24.455, 24.535), (24.695, 24.775), (24.935, 25.095), (25.095, 25.175), (25.175, 25.255), (25.335, 25.655),
            (34.455, 34.535), (34.855, 34.935), (35.015, 35.255), (35.575, 35.975), (36.775, 37.095), (37.895, 37.975), (38.295, 38.695),
            (41.255, 41.335), (41.575, 41.655), (41.655, 41.735), (42.055, 42.295), (42.375, 42.615), (42.695, 42.775), (42.855, 42.935),
            (43.095, 43.175), (43.335, 43.415), (43.495, 43.575), (43.655, 43.735), (43.735, 43.815), (44.055, 44.295), (44.615, 44.695),
            (44.695, 44.775), (45.015, 45.175), (45.575, 45.655), (45.735, 45.815), (45.895, 45.975), (46.055, 46.135), (46.215, 46.375),
            (46.375, 46.455), (46.615, 46.855)
        ];

        /// <summary>The diarizer's segments for that run, as saved in its system-diarization.json.</summary>
        public static readonly DiarizationSegment[] Diarizer = [new("speaker_1", 23.531, 25.699), new("speaker_1", 33.651, 38.739), new("speaker_1", 40.671, 46.929)];
    }

    /// <summary>A stereo file with a tone for each word, stopping 20 ms early so words that touch stay apart: loud on the left, quieter on the right.</summary>
    private string WriteWords(double seconds, IEnumerable<(double Start, double End)> microphone, IEnumerable<(double Start, double End)> system)
    {
        Directory.CreateDirectory(this.root);
        var path = Path.Combine(this.root, "recording-16k-stereo.wav");
        var samples = new float[(int)(seconds * 16_000) * 2];
        void Tone(int channel, float amplitude, (double Start, double End) word)
        {
            for (var i = (int)Math.Round(word.Start * 16_000); i < (int)Math.Round((word.End - 0.02) * 16_000); i++)
            {
                samples[(i * 2) + channel] = amplitude * MathF.Sin(i * 0.07f);
            }
        }

        foreach (var word in microphone)
        {
            Tone(0, 0.4f, word);
        }

        foreach (var word in system)
        {
            Tone(1, 0.2f, word);
        }

        using var writer = new WavFileWriter(path, 16_000, 2);
        writer.Write(samples);
        return path;
    }

    /// <summary>A faithful recognizer with exact times: one word per burst of sound, from its first to its last loud 10 ms; m = microphone, s = system.</summary>
    private sealed class BurstProvider : ITranscriptionProvider
    {
        public string ModelId => "nemotron:fake";

        public TranscriptionProviderCapabilities Capabilities { get; } = new(true, LiveRecognitionMode.NativeStreaming, TimingCapabilities.WordTimestamps, false, null, TimeSpan.FromSeconds(30), ["en"], 16_000);

        public EffectiveRuntime Runtime { get; } = new("fake", "1", "cuda:0", "cuda:0", null);

        public ValueTask<IReadOnlyList<RecognizedSegment>> RecognizeWindowAsync(ReadOnlyMemory<float> samples, string? language, CancellationToken cancellationToken)
        {
            var span = samples.Span;
            var words = new List<WordTiming>();
            int? first = null;
            var loudest = 0f;
            var frames = (span.Length + 159) / 160;
            for (var frame = 0; frame <= frames; frame++)
            {
                var peak = 0f;
                for (var i = frame * 160; i < Math.Min(span.Length, (frame + 1) * 160); i++)
                {
                    peak = Math.Max(peak, Math.Abs(span[i]));
                }

                if (peak > 0.01f)
                {
                    first ??= frame;
                    loudest = Math.Max(loudest, peak);
                }
                else if (first is { } from)
                {
                    words.Add(new WordTiming(loudest > 0.3f ? "m" : "s", TimeSpan.FromSeconds(from / 100d), TimeSpan.FromSeconds(frame / 100d), null, TimingProvenance.Model));
                    first = null;
                    loudest = 0;
                }
            }

            return ValueTask.FromResult<IReadOnlyList<RecognizedSegment>>(words.Count == 0
                ? []
                : [new RecognizedSegment(words[0].Start, words[^1].End, string.Join(' ', words.Select(w => w.Text)), words, null, null, TimingProvenance.Model)]);
        }

        public ValueTask<IStreamingRecognitionSession> StartStreamingAsync(string? language, bool diarize, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task The_9_14_PM_meeting_gets_one_row_per_diarizer_turn_in_start_order_around_the_You_line()
    {
        var store = this.store = SqliteTranscriptionSessionStore.Create(new AppDataPaths(this.root));
        await store.InitializeAsync(CancellationToken.None);
        var doc = NewDocument() with { SourceType = TranscriptSourceType.Meeting, Duration = TimeSpan.FromSeconds(Meeting914.Seconds) };
        doc = TranscriptDocumentReducer.Apply(doc, new SessionStarted(doc.SessionId, Run(1, "record-only")), Now);
        doc = TranscriptDocumentReducer.Apply(doc, new SessionCompleted(doc.SessionId, TimeSpan.FromSeconds(Meeting914.Seconds)), Now);
        var host = new SessionDocumentHost(doc, store);
        var stereo = this.WriteWords(Meeting914.Seconds, Meeting914.Microphone, Meeting914.System);

        var result = await new MeetingFinalPass(new ModelLeaseScheduler()).RunAsync(
            host, stereo, new BurstProvider(), _ => Task.FromResult<(DiarizationOverlay?, string?)>((new DiarizationOverlay(Meeting914.Diarizer), null)), "en-US", this.root, null, default);

        var document = host.Document;
        Assert.True(document.Runs.Single(r => r.ResultVersion == result.ResultVersion).SegmentsAreRows);
        // The recognizer saw the system side as one window, 22-47.5 s, holding all three turns, as in the real run.
        Assert.Contains("22.0-47.5s window: 36 words", result.ChannelReport);

        var system = document.ActiveSegments.Where(s => s.Id.StartsWith("fs", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, system.Count);
        Assert.All(system, row => Assert.Equal("speaker-1", row.Speakers[0].SpeakerId));
        Assert.All(system.SelectMany(row => row.Words!), w => Assert.Equal("speaker-1", w.SpeakerId));
        Assert.Equal([6, 7, 23], system.Select(row => row.Words!.Count));
        // Each row is one diarizer turn: 23.5-25.7, 33.7-38.7 and 40.7-46.9 s.
        Assert.All(system.Zip(Meeting914.Diarizer), pair => Assert.InRange(pair.First.Start.TotalSeconds, pair.Second.Start, pair.Second.End));
        Assert.All(system.Zip(Meeting914.Diarizer), pair => Assert.InRange(pair.First.End.TotalSeconds, pair.Second.Start, pair.Second.End));

        // Justin's 12 s stretch splits at its 1 s pause; the two quick words at 40.8 s are their own row.
        var mic = document.ActiveSegments.Where(s => s.Id.StartsWith("fm", StringComparison.Ordinal)).ToList();
        Assert.Equal([33, 21, 2], mic.Select(row => row.Words!.Count));
        Assert.InRange(mic[2].Start.TotalSeconds, 40.745, 40.765);

        // On screen: one row per line, in the order things were said; the You line sits between the second and third turn.
        var rows = TranscriptTurns.Group(document, TimeSpan.FromSeconds(5));
        Assert.All(rows, row => Assert.Single(row.Segments));
        Assert.Equal(["local", "local", "speaker-1", "speaker-1", "local", "speaker-1"], rows.Select(row => row.Segments[0].Speakers[0].SpeakerId));
        Assert.Equal(rows.Select(row => row.Segments[0].Start).Order(), rows.Select(row => row.Segments[0].Start));
        Assert.DoesNotContain(rows, row => row.Segments[0].Speakers[0].SpeakerId != "local" && row.Segments[0].Start < mic[2].Start && row.Segments[0].End > mic[2].Start);
        Assert.Equal(3, result.MicrophoneLines);
        Assert.Equal(3, result.SystemLines);

        // Reopening gives the same rows.
        await host.CheckpointAsync(CancellationToken.None);
        var reloaded = await store.LoadAsync(document.SessionId, CancellationToken.None);
        Assert.Equal(rows.Select(row => row.Segments[0].Id), TranscriptTurns.Group(reloaded!, TimeSpan.FromSeconds(5)).Select(row => row.Segments[0].Id));
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
    public async Task A_window_without_word_times_is_redone_by_turn_and_shared_words_are_shown_once()
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
            host, path, new NoWordsForLongWindows(), _ => Task.FromResult<(DiarizationOverlay?, string?)>((new DiarizationOverlay([new("speaker_3", 2.0, 5.0), new("speaker_4", 4.0, 8.0)]), null)), "en-US", this.root, null, default);

        await store.DisposeAsync();
        SqliteConnection.ClearAllPools();
        var rows = host.Document.ActiveSegments.Where(l => l.Id.StartsWith("ft", StringComparison.Ordinal)).OrderBy(l => l.Start).ToList();
        Assert.Equal(["speaker-3", "speaker-4"], rows.Select(r => r.Speakers[0].SpeakerId));
        var visible = rows.SelectMany(r => r.Words!.Where(w => !w.Hidden)).ToList();
        // 2-8 s is twelve half-second blocks; the padded turns hold about 15 between them, and the shared ones are hidden once.
        Assert.InRange(visible.Count, 11, 14);
        Assert.Contains(rows.SelectMany(r => r.Words!), w => w.Hidden);
        Assert.Contains("system turns:", result.ChannelReport);
        Assert.Contains("redone by speaker turn", result.ChannelReport);
    }

    /// <summary>Like the faithful recognizer, but a long window comes back as text only, with no word times.</summary>
    private sealed class NoWordsForLongWindows : ITranscriptionProvider
    {
        private readonly MeetingFinalPassTests.ChannelProvider inner = new([]);

        public string ModelId => this.inner.ModelId;

        public TranscriptionProviderCapabilities Capabilities => this.inner.Capabilities;

        public EffectiveRuntime Runtime => this.inner.Runtime;

        public ValueTask<IReadOnlyList<RecognizedSegment>> RecognizeWindowAsync(ReadOnlyMemory<float> samples, string? language, CancellationToken cancellationToken) =>
            samples.Length > 9 * 16_000
                ? ValueTask.FromResult<IReadOnlyList<RecognizedSegment>>([new RecognizedSegment(TimeSpan.Zero, TimeSpan.FromSeconds(samples.Length / 16_000d), "some words", null, null, null, TimingProvenance.ApproximateChunk)])
                : this.inner.RecognizeWindowAsync(samples, language, cancellationToken);

        public ValueTask<IStreamingRecognitionSession> StartStreamingAsync(string? language, bool diarize, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task A_short_turn_between_two_long_ones_keeps_its_words_because_the_recognizer_saw_the_whole_window()
    {
        Directory.CreateDirectory(this.root);
        var path = Path.Combine(this.root, "stereo.wav");
        using (var writer = new WavFileWriter(path, 16_000, 2))
        {
            var samples = new float[10 * 16_000 * 2];
            for (var i = 0; i < 10 * 16_000; i++)
            {
                samples[(i * 2) + 1] = 0.1f * MathF.Sin(i * 0.07f);
            }

            writer.Write(samples);
        }

        var store = SqliteTranscriptionSessionStore.Create(new AppDataPaths(this.root));
        await store.InitializeAsync(CancellationToken.None);
        var doc = NewDocument() with { SourceType = TranscriptSourceType.Meeting, Duration = TimeSpan.FromSeconds(10) };
        doc = TranscriptDocumentReducer.Apply(doc, new SessionStarted(doc.SessionId, Run(1, "whisper-onnx/tiny.en")), Now);
        var host = new SessionDocumentHost(doc, store);
        var calls = new List<string>();

        var result = await new MeetingFinalPass(new ModelLeaseScheduler()).RunAsync(
            host, path, new MeetingFinalPassTests.ChannelProvider(calls), _ => Task.FromResult<(DiarizationOverlay?, string?)>((new DiarizationOverlay([new("speaker_1", 0.0, 5.0), new("speaker_2", 5.0, 6.4), new("speaker_3", 6.4, 10.0)]), null)), "en-US", this.root, null, default);

        await store.DisposeAsync();
        SqliteConnection.ClearAllPools();
        var rows = host.Document.ActiveSegments.Where(l => l.Id.StartsWith("fs", StringComparison.Ordinal)).OrderBy(l => l.Start).ToList();
        Assert.Equal(["speaker-1", "speaker-2", "speaker-3"], rows.Select(r => r.Speakers[0].SpeakerId));
        Assert.Equal(1, calls.Count(c => c == "system")); // one recognizer call saw all ten seconds
        Assert.Equal(3, rows[1].Words!.Count);
        Assert.All(rows[1].Words!, w => Assert.InRange(w.Start.TotalSeconds, 4.9, 6.6));
        Assert.Contains("system windows:", result.ChannelReport);
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
