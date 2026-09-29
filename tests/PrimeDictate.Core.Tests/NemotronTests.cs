using System.Net;
using System.Text;
using PrimeDictate.Core.Pipeline;
using PrimeDictate.Core.Providers;
using PrimeDictate.Core.Transcripts;
using PrimeDictate.Platforms.Nemotron;

namespace PrimeDictate.Core.Tests;

public sealed class NemotronTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pd-nemo-tests", Guid.NewGuid().ToString("N"));

    public NemotronTests() => Directory.CreateDirectory(this.root);

    public void Dispose()
    {
        if (Directory.Exists(this.root))
        {
            Directory.Delete(this.root, recursive: true);
        }
    }

    private static string SamplePath(string name)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "PrimeDictate.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return Path.Combine(dir!, "docs", "architecture", "nemotron-samples", name);
    }

    [Fact]
    public void Real_diarized_response_becomes_speaker_turns_with_word_timings()
    {
        var json = File.ReadAllText(SamplePath("file-verbose-json-diarization.json"));
        var segments = NemotronResponseParser.ParseVerboseJson(json, TimeSpan.FromSeconds(12), withSpeakers: true);

        Assert.True(segments.Count >= 2);
        Assert.All(segments, s => Assert.StartsWith("speaker-", s.SpeakerLabel));
        Assert.Contains(segments, s => s.SpeakerLabel == "speaker-1");
        Assert.Contains(segments, s => s.SpeakerLabel == "speaker-2");
        Assert.All(segments, s => Assert.Equal(TimingProvenance.Model, s.Provenance));
        Assert.All(segments, s => Assert.True(s.Words!.Count > 0 && s.Start <= s.End));
        // No word is lost or reordered by grouping.
        var joined = string.Join(' ', segments.Select(s => s.Text));
        Assert.StartsWith("From a plain text file", joined);
        Assert.Equal(segments.Sum(s => s.Words!.Count), segments.SelectMany(s => s.Words!).Count());
    }

    [Fact]
    public void Without_speakers_the_same_words_stay_one_segment()
    {
        var json = File.ReadAllText(SamplePath("file-verbose-json-diarization.json"));
        var segments = NemotronResponseParser.ParseVerboseJson(json, TimeSpan.FromSeconds(12), withSpeakers: false);
        var single = Assert.Single(segments);
        Assert.Null(single.SpeakerLabel);
    }

    [Fact]
    public void Response_without_words_falls_back_to_the_text_and_empty_text_gives_nothing()
    {
        var one = NemotronResponseParser.ParseVerboseJson("""{"text":"hello there"}""", TimeSpan.FromSeconds(3), false);
        Assert.Equal("hello there", Assert.Single(one).Text);
        Assert.Empty(NemotronResponseParser.ParseVerboseJson("""{"text":"  ","words":[]}""", TimeSpan.FromSeconds(3), false));
    }

    [Fact]
    public void Worker_arguments_are_loopback_cpu_and_local_paths_only()
    {
        var files = new NemotronModelFiles("/m/asr.gguf", "/m/diar.gguf");
        var args = NemotronWorker.BuildArguments(18080, files);

        Assert.Equal("serve", args[0]);
        Assert.Contains("--no-ui", args);
        Assert.Equal("127.0.0.1", args[args.ToList().IndexOf("--host") + 1]);
        Assert.Equal("cpu", args[args.ToList().IndexOf("--device") + 1]);
        Assert.Equal("/m/asr.gguf", args[args.ToList().IndexOf("--asr-model") + 1]);
        Assert.Equal("/m/diar.gguf", args[args.ToList().IndexOf("--diar-model") + 1]);
        Assert.DoesNotContain(args, a => a.Contains("api", StringComparison.OrdinalIgnoreCase) && a.Contains("key", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("--diar-model", NemotronWorker.BuildArguments(1, new NemotronModelFiles("/m/asr.gguf", null)));
    }

    [Fact]
    public void Only_pinned_model_files_with_the_pinned_size_are_accepted()
    {
        var wrongName = Path.Combine(this.root, "other.gguf");
        File.WriteAllText(wrongName, "x");
        Assert.Equal("model-unrecognized", Assert.Throws<NemotronException>(() => NemotronModelFiles.Verify(wrongName, null)).Code);

        var pinnedName = Path.Combine(this.root, NemotronPins.Multilingual.FileName);
        File.WriteAllText(pinnedName, "too small");
        Assert.Equal("model-corrupt", Assert.Throws<NemotronException>(() => NemotronModelFiles.Verify(pinnedName, null)).Code);

        Assert.Equal("model-missing", Assert.Throws<NemotronException>(() => NemotronModelFiles.Verify(Path.Combine(this.root, "none.gguf"), null)).Code);

        using (var fs = new FileStream(pinnedName, FileMode.Create))
        {
            fs.SetLength(NemotronPins.Multilingual.Bytes);
        }

        Assert.Equal(pinnedName, NemotronModelFiles.Verify(pinnedName, null).AsrPath);
    }

    private sealed class FakeEndpoint(HttpClient client, bool hasDiarizer) : INemotronEndpoint
    {
        public HttpClient Client { get; } = client;

        public bool HasDiarizer { get; } = hasDiarizer;
    }

    private static async Task<(string Body, string Response)> ServeOnceAsync(HttpListener listener, string responseJson, int status = 200)
    {
        var context = await listener.GetContextAsync();
        using var reader = new StreamReader(context.Request.InputStream, Encoding.Latin1);
        var body = await reader.ReadToEndAsync();
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        var bytes = Encoding.UTF8.GetBytes(responseJson);
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
        return (body, responseJson);
    }

    private static (HttpListener Listener, HttpClient Client) StartFake()
    {
        var port = new Random().Next(20000, 60000);
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        return (listener, new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") });
    }

    [Fact]
    public async Task Provider_requests_diarization_only_when_the_diarizer_is_loaded()
    {
        var sample = File.ReadAllText(SamplePath("file-verbose-json-diarization.json"));
        var audio = new float[16_000 * 2];

        var (withListener, withClient) = StartFake();
        using (withListener)
        {
            var provider = new NemotronProvider(new FakeEndpoint(withClient, hasDiarizer: true), "nemotron:test");
            var serve = ServeOnceAsync(withListener, sample);
            var segments = await provider.RecognizeWindowAsync(audio, "en", default);
            var (request, _) = await serve;
            Assert.Contains("name=diarization", request);
            Assert.Contains("verbose_json", request);
            Assert.Contains("name=language", request);
            Assert.True(provider.Capabilities.CombinedDiarization);
            Assert.Contains(segments, s => s.SpeakerLabel == "speaker-2");
        }

        var (withoutListener, withoutClient) = StartFake();
        using (withoutListener)
        {
            var provider = new NemotronProvider(new FakeEndpoint(withoutClient, hasDiarizer: false), "nemotron:test");
            var serve = ServeOnceAsync(withoutListener, sample);
            var segments = await provider.RecognizeWindowAsync(audio, null, default);
            var (request, _) = await serve;
            Assert.DoesNotContain("name=diarization", request);
            Assert.False(provider.Capabilities.CombinedDiarization);
            Assert.All(segments, s => Assert.Null(s.SpeakerLabel));
        }
    }

    [Fact]
    public async Task Provider_reports_worker_errors_with_the_workers_message_and_refuses_oversized_windows()
    {
        var (listener, client) = StartFake();
        using (listener)
        {
            var provider = new NemotronProvider(new FakeEndpoint(client, true), "nemotron:test");
            var serve = ServeOnceAsync(listener, """{"error":{"message":"speaker diarization requested but no diarizer model is loaded"}}""", 400);
            var ex = await Assert.ThrowsAsync<NemotronException>(async () => await provider.RecognizeWindowAsync(new float[16_000], null, default));
            await serve;
            Assert.Contains("no diarizer", ex.Message);
        }

        var offline = new NemotronProvider(new FakeEndpoint(new HttpClient(), true), "nemotron:test");
        await Assert.ThrowsAsync<ArgumentException>(async () => await offline.RecognizeWindowAsync(new float[NemotronProvider.MaxWindowSamples + 1], null, default));
    }

    [Fact]
    public void Speaker_labels_from_a_provider_register_speakers_and_keep_renames_on_the_mapping_only()
    {
        var doc = TestData.NewDocument();
        var host = new SessionDocumentHost(doc, new NullStore());
        var recognized = new[]
        {
            new RecognizedSegment(TimeSpan.Zero, TimeSpan.FromSeconds(2), "hi", null, null, "speaker-1", TimingProvenance.Model),
            new RecognizedSegment(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), "hello", null, null, "speaker-2", TimingProvenance.Model)
        };
        var segments = SegmentMapper.Map(recognized, "f0", 0, TimeSpan.FromSeconds(4), 1, 1, SegmentState.Final).ToList();
        host.EnsureSpeakers(segments);
        host.EnsureSpeakers(segments);
        Assert.Equal(["Speaker 1", "Speaker 2"], host.Document.Speakers.Select(s => s.Name));

        host.RenameSpeaker("speaker-2", "Dana");
        Assert.Equal("Dana", host.Document.Speakers.Single(s => s.Id == "speaker-2").Name);
        Assert.Equal("speaker-2", segments[1].Speakers[0].SpeakerId);
    }

    private sealed class NullStore : PrimeDictate.Core.Sessions.ITranscriptionSessionStore
    {
        public ValueTask InitializeAsync(CancellationToken c) => ValueTask.CompletedTask;

        public ValueTask SaveCheckpointAsync(TranscriptDocument d, CancellationToken c) => ValueTask.CompletedTask;

        public ValueTask<TranscriptDocument?> LoadAsync(Guid id, CancellationToken c) => ValueTask.FromResult<TranscriptDocument?>(null);

        public ValueTask<IReadOnlyList<PrimeDictate.Core.Sessions.TranscriptSessionSummary>> ListAsync(int s, int t, CancellationToken c) => ValueTask.FromResult<IReadOnlyList<PrimeDictate.Core.Sessions.TranscriptSessionSummary>>([]);

        public ValueTask<IReadOnlyList<PrimeDictate.Core.Sessions.TranscriptSessionSummary>> MarkInterruptedSessionsAsync(CancellationToken c) => ValueTask.FromResult<IReadOnlyList<PrimeDictate.Core.Sessions.TranscriptSessionSummary>>([]);

        public ValueTask<IReadOnlyList<string>> DeleteOwnedAudioAsync(Guid id, CancellationToken c) => ValueTask.FromResult<IReadOnlyList<string>>([]);

        public ValueTask<PrimeDictate.Core.Sessions.SessionDeletionResult> DeleteAsync(Guid id, CancellationToken c) => ValueTask.FromResult(new PrimeDictate.Core.Sessions.SessionDeletionResult(false, [], []));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
