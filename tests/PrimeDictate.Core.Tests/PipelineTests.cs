using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using PrimeDictate.Core.Audio;
using PrimeDictate.Core.Coordination;
using PrimeDictate.Core.Export;
using PrimeDictate.Core.Pipeline;
using PrimeDictate.Core.Providers;
using PrimeDictate.Core.Sessions;
using PrimeDictate.Core.Storage;
using PrimeDictate.Core.Transcripts;
using static PrimeDictate.Core.Tests.TestData;

namespace PrimeDictate.Core.Tests;

public sealed class PipelineTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pd-pipeline-tests", Guid.NewGuid().ToString("N"));

    public PipelineTests() => Directory.CreateDirectory(this.root);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(this.root))
        {
            Directory.Delete(this.root, recursive: true);
        }
    }

    private static float[] Tone(double seconds, float amplitude = 0.3f) =>
        Enumerable.Range(0, (int)(seconds * 16_000)).Select(i => amplitude * MathF.Sin(i * 0.05f)).ToArray();

    private static float[] Silence(double seconds) => new float[(int)(seconds * 16_000)];

    private static float[] Join(params float[][] parts) => parts.SelectMany(p => p).ToArray();

    [Fact]
    public void Chunker_keeps_every_sample_in_order_and_cuts_in_silence()
    {
        var audio = Join(Tone(25), Silence(0.6), Tone(25), Silence(0.6), Tone(10));
        var chunker = new SpeechChunker();
        var chunks = chunker.Add(audio).Concat(chunker.Flush()).ToList();

        Assert.True(chunks.Count >= 2);
        long expectedStart = 0;
        foreach (var chunk in chunks)
        {
            Assert.Equal(expectedStart, chunk.StartSample);
            Assert.True(chunk.Duration <= TimeSpan.FromSeconds(28.1));
            expectedStart += chunk.Samples.Length;
        }

        Assert.Equal(audio.Length, expectedStart);
        Assert.Equal(audio, chunks.SelectMany(c => c.Samples).ToArray());
        // No overlap means repeated speech is never dropped or duplicated by the chunker.
        // Cuts land inside the silent gaps (25.0-25.6 s and 50.6-51.2 s), never mid-tone.
        Assert.InRange(chunks[0].Duration.TotalSeconds, 25.0, 25.6);
        Assert.InRange((chunks[1].Start + chunks[1].Duration).TotalSeconds, 50.6, 51.2);
    }

    [Fact]
    public void Chunker_marks_silent_audio_as_no_speech()
    {
        var chunker = new SpeechChunker();
        var chunks = chunker.Add(Silence(5)).Concat(chunker.Flush()).ToList();
        Assert.All(chunks, c => Assert.False(c.ContainsSpeech));
    }

    [Fact]
    public void Utterance_detector_ends_an_utterance_on_silence_but_keeps_running()
    {
        var detector = new UtteranceDetector();
        var events = detector.Add(Join(Silence(0.5), Tone(2), Silence(1.2), Tone(2), Silence(1.2))).ToList();
        var ended = events.Where(e => e.Kind == UtteranceEventKind.Ended).ToList();
        Assert.Equal(2, ended.Count);
        Assert.NotEqual(ended[0].UtteranceIndex, ended[1].UtteranceIndex);
        Assert.True(ended[1].StartSample > ended[0].StartSample);
    }

    [Fact]
    public void Utterance_detector_flush_finalizes_the_open_utterance()
    {
        var detector = new UtteranceDetector();
        var events = detector.Add(Tone(2)).ToList();
        Assert.DoesNotContain(events, e => e.Kind == UtteranceEventKind.Ended);
        Assert.Contains(detector.Flush(), e => e.Kind == UtteranceEventKind.Ended);
    }

    [Fact]
    public async Task Wav_writer_output_decodes_to_the_same_samples()
    {
        var path = Path.Combine(this.root, "a.wav");
        var audio = Tone(1.5);
        using (var writer = new WavFileWriter(path))
        {
            writer.Write(audio);
        }

        var decoder = new WavAudioDecoder();
        var probe = await decoder.ProbeAsync(path, default);
        Assert.Equal(16_000, probe.AudioStreams[0].SampleRate);
        var decoded = new List<float>();
        await foreach (var frame in decoder.DecodeAsync(path, 0, default))
        {
            decoded.AddRange(frame.Samples.ToArray());
        }

        Assert.Equal(audio.Length, decoded.Count);
        Assert.All(audio.Zip(decoded), p => Assert.InRange(Math.Abs(p.First - p.Second), 0, 1f / 16_000));
    }

    [Fact]
    public async Task Wav_decoder_rejects_non_wav_data_with_a_typed_error()
    {
        var path = Path.Combine(this.root, "bad.wav");
        await File.WriteAllBytesAsync(path, "not audio at all"u8.ToArray());
        var ex = await Assert.ThrowsAsync<MediaDecodeException>(async () => await new WavAudioDecoder().ProbeAsync(path, default));
        Assert.False(string.IsNullOrEmpty(ex.ErrorCode));
    }

    [Fact]
    public void Timecodes_support_hours_over_99_and_the_right_separator()
    {
        var time = TimeSpan.FromHours(100) + TimeSpan.FromMilliseconds(3_723_004);
        Assert.Equal("101:02:03,004", TranscriptExporter.FormatTimecode(time, srt: true));
        Assert.Equal("101:02:03.004", TranscriptExporter.FormatTimecode(time, srt: false));
    }

    [Fact]
    public void Exports_use_edited_text_by_default_and_raw_on_request_without_altering_words()
    {
        var doc = NewDocument();
        doc = TranscriptDocumentReducer.Apply(doc, new SessionStarted(doc.SessionId, Run()), Now);
        doc = TranscriptDocumentReducer.Apply(doc, new SegmentFinalized(doc.SessionId, Segment("s1", "thank you", 0, 2)), Now);
        doc = TranscriptDocumentReducer.EditSegment(doc, "s1", "Thank you, potato farmer.", Now);

        var edited = TranscriptExporter.Export(doc, new ExportOptions(ExportFormat.Text, ExportTextSource.Edited, false));
        var raw = TranscriptExporter.Export(doc, new ExportOptions(ExportFormat.Text, ExportTextSource.Raw, false));
        Assert.Contains("Thank you, potato farmer.", edited);
        Assert.Contains("thank you", raw);
        Assert.DoesNotContain("potato", raw);
    }

    [Fact]
    public void Subtitle_cues_cannot_be_broken_by_transcript_text()
    {
        var doc = NewDocument();
        doc = TranscriptDocumentReducer.Apply(doc, new SessionStarted(doc.SessionId, Run()), Now);
        doc = TranscriptDocumentReducer.Apply(doc, new SegmentFinalized(doc.SessionId, Segment("s1", "line one\n\nnext --> cue <b>&", 0, 2)), Now);
        var vtt = TranscriptExporter.Export(doc, new ExportOptions(ExportFormat.WebVtt));
        var body = vtt[(vtt.IndexOf("-->", StringComparison.Ordinal) + 3)..];
        Assert.DoesNotContain("-->", body);
        Assert.DoesNotContain("\n\n", body.TrimEnd('\n'));
        Assert.DoesNotContain("<b>", vtt);
    }

    private sealed class FakeDecoder(float[] audio) : IAudioDecoder
    {
        public ValueTask<MediaProbeResult> ProbeAsync(string path, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new MediaProbeResult("wav", TimeSpan.FromSeconds(audio.Length / 16_000d), [new MediaStreamInfo(0, "pcm", 16_000, 1, null, null)]));

        public async IAsyncEnumerable<AudioFrame> DecodeAsync(string path, int streamIndex, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            long offset = 0;
            var seq = 0;
            for (var i = 0; i < audio.Length; i += 8000)
            {
                var part = audio.AsMemory(i, Math.Min(8000, audio.Length - i));
                yield return AudioFrame.CopyFrom(part.Span, new AudioFormat(16_000, 1, AudioSampleFormat.Float32), seq++, offset);
                offset += part.Length;
                await Task.Yield();
            }
        }
    }

    private sealed class FakeProvider(Func<int, string> textFor) : ITranscriptionProvider
    {
        private int calls;

        public string ModelId => "fake:model";

        public TranscriptionProviderCapabilities Capabilities { get; } = new(true, LiveRecognitionMode.BufferedWindows, TimingCapabilities.SegmentTimestamps, false, null, TimeSpan.FromSeconds(30), ["en"], 16_000);

        public EffectiveRuntime Runtime { get; } = new("fake", "1", "cpu", "cpu", null);

        public ValueTask<IReadOnlyList<RecognizedSegment>> RecognizeWindowAsync(ReadOnlyMemory<float> samples, string? language, CancellationToken cancellationToken)
        {
            var n = Interlocked.Increment(ref this.calls);
            IReadOnlyList<RecognizedSegment> result = [new RecognizedSegment(TimeSpan.Zero, TimeSpan.FromSeconds(samples.Length / 16_000d), textFor(n), null, null, null, TimingProvenance.ApproximateChunk)];
            return ValueTask.FromResult(result);
        }

        public ValueTask<IStreamingRecognitionSession> StartStreamingAsync(string? language, bool diarize, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static TranscriptionSessionOptions Options(AudioRetention retention) =>
        new("fake:model", null, "cpu", "en", null, retention, DownmixMode.Average, null, null, null);

    [Fact]
    public async Task File_run_produces_ordered_segments_with_literal_text_and_survives_reload()
    {
        var paths = new AppDataPaths(this.root);
        await using var store = SqliteTranscriptionSessionStore.Create(paths);
        await store.InitializeAsync(default);
        var original = Path.Combine(this.root, "original.wav");
        await File.WriteAllTextAsync(original, "placeholder");
        var audio = Join(Tone(20), Silence(0.8), Tone(20), Silence(0.8), Tone(5));
        var runner = new FileTranscriptionRunner(new ModelLeaseScheduler(), store);
        var decoder = new FakeDecoder(audio);
        var request = new FileJobRequest(original, "Call", Options(AudioRetention.KeepAudio), 0, true, string.Empty);
        var host = await runner.CreateSessionAsync(request, decoder, default);
        request = request with { SessionMediaDirectory = store.GetSessionMediaDirectory(host.Document.SessionId) };
        // Whisper's well-known silence hallucinations are ordinary text to the pipeline: nothing is filtered or rewritten.
        await runner.RunAsync(host, request, decoder, new FakeProvider(n => n == 1 ? "thank you" : $"potato farmer {n}"), null, default);

        var loaded = (await store.LoadAsync(host.Document.SessionId, default))!;
        Assert.Equal(TranscriptSessionStatus.Completed, loaded.Status);
        var texts = loaded.ActiveSegments.Select(s => s.RawText).ToList();
        Assert.Equal("thank you", texts[0]);
        Assert.Contains("potato farmer 2", texts);
        Assert.True(loaded.ActiveSegments.Zip(loaded.ActiveSegments.Skip(1)).All(p => p.First.Start <= p.Second.Start));
        Assert.Contains(loaded.Audio, a => a.Kind == AudioReferenceKind.Owned && File.Exists(a.Path));
    }

    [Fact]
    public async Task Rerun_adds_a_new_result_version_and_keeps_edits_on_the_old_one()
    {
        var paths = new AppDataPaths(this.root);
        await using var store = SqliteTranscriptionSessionStore.Create(paths);
        await store.InitializeAsync(default);
        var original = Path.Combine(this.root, "original.wav");
        await File.WriteAllTextAsync(original, "x");
        var runner = new FileTranscriptionRunner(new ModelLeaseScheduler(), store);
        var decoder = new FakeDecoder(Tone(5));
        var request = new FileJobRequest(original, "Call", Options(AudioRetention.KeepAudio), 0, false, this.root);
        var host = await runner.CreateSessionAsync(request, decoder, default);
        await runner.RunAsync(host, request, decoder, new FakeProvider(_ => "first"), null, default);
        var firstId = host.Document.ActiveSegments.Single().Id;
        host.Edit(firstId, "my edit");

        await runner.RunAsync(host, request, decoder, new FakeProvider(_ => "second"), null, default);

        Assert.Equal(2, host.Document.Runs.Count);
        Assert.Equal("second", host.Document.ActiveSegments.Single().RawText);
        var old = host.Document.Segments.Single(s => s.Id == firstId && s.ResultVersion == 1);
        Assert.Equal("my edit", old.EditedText);
        Assert.Equal("first", old.RawText);
    }

    [Fact]
    public async Task Transcript_only_retention_deletes_owned_audio_but_never_the_original()
    {
        var paths = new AppDataPaths(this.root);
        await using var store = SqliteTranscriptionSessionStore.Create(paths);
        await store.InitializeAsync(default);
        var original = Path.Combine(this.root, "keep-me.wav");
        await File.WriteAllTextAsync(original, "x");
        var runner = new FileTranscriptionRunner(new ModelLeaseScheduler(), store);
        var decoder = new FakeDecoder(Tone(5));
        var request = new FileJobRequest(original, "Call", Options(AudioRetention.TranscriptOnly), 0, true, string.Empty);
        var host = await runner.CreateSessionAsync(request, decoder, default);
        request = request with { SessionMediaDirectory = store.GetSessionMediaDirectory(host.Document.SessionId) };
        await runner.RunAsync(host, request, decoder, new FakeProvider(_ => "hello"), null, default);
        Assert.DoesNotContain(host.Document.Audio, a => a.Kind == AudioReferenceKind.Owned);

        var result = await store.DeleteAsync(host.Document.SessionId, default);
        Assert.True(result.Existed);
        Assert.True(File.Exists(original));
        Assert.Contains(original, result.KeptExternalFiles);
    }

    [Fact]
    public async Task Cancellation_marks_the_session_canceled_and_keeps_finished_chunks()
    {
        var paths = new AppDataPaths(this.root);
        await using var store = SqliteTranscriptionSessionStore.Create(paths);
        await store.InitializeAsync(default);
        var original = Path.Combine(this.root, "o.wav");
        await File.WriteAllTextAsync(original, "x");
        var runner = new FileTranscriptionRunner(new ModelLeaseScheduler(), store);
        var audio = Join(Tone(20), Silence(0.8), Tone(20), Silence(0.8), Tone(20));
        var decoder = new FakeDecoder(audio);
        var request = new FileJobRequest(original, "Call", Options(AudioRetention.KeepAudio), 0, false, this.root);
        var host = await runner.CreateSessionAsync(request, decoder, default);
        using var cts = new CancellationTokenSource();
        var provider = new FakeProvider(n =>
        {
            if (n == 2)
            {
                cts.Cancel();
            }

            return $"chunk {n}";
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(host, request, decoder, provider, null, cts.Token));
        Assert.Equal(TranscriptSessionStatus.Canceled, host.Document.Status);
        Assert.Contains(host.Document.ActiveSegments, s => s.RawText == "chunk 1");
    }

    [Fact]
    public void Core_never_references_text_injection_shell_or_logging_of_transcript_text()
    {
        var core = typeof(SessionDocumentHost).Assembly;
        var referenced = core.GetReferencedAssemblies().Select(a => a.Name).ToList();
        Assert.DoesNotContain(referenced, n => n!.Contains("SharpHook", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(referenced, n => n!.Contains("Logging", StringComparison.OrdinalIgnoreCase));
        var usesProcess = core.GetTypes().SelectMany(t => t.GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly))
            .Any(m => m.ReturnType == typeof(System.Diagnostics.Process));
        Assert.False(usesProcess);
    }
}
