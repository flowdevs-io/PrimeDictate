using PrimeDictate.Core.Dictation;
using PrimeDictate.Core.Providers;
using PrimeDictate.Core.Transcripts;
using PrimeDictate.Platforms.Speech;
using PrimeDictate.Platforms.Speech.Qualcomm;

namespace PrimeDictate.Core.Tests;

/// <summary>The Qualcomm providers and their parts, with a fake in place of the NPU session.</summary>
public sealed class QualcommProviderTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pd-qnnp-" + Guid.NewGuid().ToString("N"));

    public QualcommProviderTests() => Directory.CreateDirectory(this.root);

    public void Dispose() => Directory.Delete(this.root, recursive: true);

    private static readonly InstalledSpeechModel Aihub = new(LegacyBackend.QualcommQnn, "qaihub-whisper-small-snapdragon-x-elite", "AI Hub", @"C:\models\aihub", true);

    private sealed class FakeTranscriber(string text = "hello there") : IQnnTranscriber
    {
        public int Calls { get; private set; }

        public int DisposeCount { get; private set; }

        public int LastLength { get; private set; }

        public string? Fallback { get; set; }

        public string DiagnosticsSummary => "fake";

        string? IQnnTranscriber.FallbackReason => this.Fallback;

        public string Transcribe(float[] samples, CancellationToken cancellationToken)
        {
            this.Calls++;
            this.LastLength = samples.Length;
            return text;
        }

        public void Dispose() => this.DisposeCount++;
    }

    // --- provider behaviour ---

    [Fact]
    public async Task A_window_becomes_one_segment_covering_it()
    {
        var fake = new FakeTranscriber();
        await using var provider = new QualcommAihubWhisperProvider(Aihub, QualcommCatalogTests.Arm64WithNatives, _ => fake, new SharedResourceCache<SharedQnnModel>());

        var segments = await provider.RecognizeWindowAsync(new float[32_000], "de", CancellationToken.None);

        var segment = Assert.Single(segments);
        Assert.Equal("hello there", segment.Text);
        Assert.Equal(TimeSpan.Zero, segment.Start);
        Assert.Equal(TimeSpan.FromSeconds(2), segment.End);
        Assert.Equal(TimingProvenance.ApproximateChunk, segment.Provenance);
        Assert.Equal(32_000, fake.LastLength);
        Assert.Equal("qualcomm-qnn:qaihub-whisper-small-snapdragon-x-elite", provider.ModelId);
        Assert.Equal(16_000, provider.Capabilities.RequiredSampleRate);
        Assert.Equal(LiveRecognitionMode.BufferedWindows, provider.Capabilities.LiveMode);
        Assert.Equal(["en"], provider.Capabilities.Languages);
        Assert.Equal("qnn-htp", provider.Runtime.EffectiveBackend);
        Assert.Null(provider.Runtime.FallbackReason);
    }

    [Fact]
    public async Task Silence_or_empty_audio_makes_no_segment_and_loads_nothing()
    {
        var created = 0;
        await using var provider = new QualcommAihubWhisperProvider(Aihub, QualcommCatalogTests.Arm64WithNatives, _ => { created++; return new FakeTranscriber(" "); }, new SharedResourceCache<SharedQnnModel>());

        Assert.Empty(await provider.RecognizeWindowAsync(ReadOnlyMemory<float>.Empty, null, CancellationToken.None));
        Assert.Equal(0, created);
        Assert.Empty(await provider.RecognizeWindowAsync(new float[16_000], null, CancellationToken.None));
        Assert.Equal(1, created);
    }

    [Fact]
    public async Task A_machine_without_the_npu_fails_with_the_reason_and_never_creates_a_session()
    {
        var created = 0;
        var x64 = QnnRuntimeSupport.Evaluate(true, System.Runtime.InteropServices.Architecture.X64, _ => null);
        await using var provider = new QualcommAihubWhisperProvider(Aihub, x64, _ => { created++; return new FakeTranscriber(); }, new SharedResourceCache<SharedQnnModel>());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await provider.RecognizeWindowAsync(new float[16_000], null, CancellationToken.None));
        Assert.Contains("native Windows ARM64 process", error.Message);
        Assert.Equal(0, created);
    }

    [Fact]
    public async Task Streaming_is_not_offered()
    {
        await using var provider = new QualcommAihubWhisperProvider(Aihub, QualcommCatalogTests.Arm64WithNatives, _ => new FakeTranscriber(), new SharedResourceCache<SharedQnnModel>());
        await Assert.ThrowsAsync<NotSupportedException>(async () => await provider.StartStreamingAsync(null, false, CancellationToken.None));
    }

    [Fact]
    public async Task Two_providers_on_one_package_share_one_loaded_model_and_the_last_release_unloads_it()
    {
        var cache = new SharedResourceCache<SharedQnnModel>();
        var created = new List<FakeTranscriber>();
        FakeTranscriber Create(string _) { var f = new FakeTranscriber(); created.Add(f); return f; }
        var a = new QualcommAihubWhisperProvider(Aihub, QualcommCatalogTests.Arm64WithNatives, Create, cache);
        var b = new QualcommAihubWhisperProvider(Aihub, QualcommCatalogTests.Arm64WithNatives, Create, cache);

        await a.RecognizeWindowAsync(new float[16_000], null, CancellationToken.None);
        await b.RecognizeWindowAsync(new float[16_000], null, CancellationToken.None);
        Assert.Single(created);
        Assert.Equal(2, created[0].Calls);
        Assert.Equal(1, cache.Count);

        await a.DisposeAsync();
        Assert.Equal(0, created[0].DisposeCount);
        await b.DisposeAsync();
        await b.DisposeAsync();
        Assert.Equal(1, created[0].DisposeCount);
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public async Task A_disposed_provider_refuses_work()
    {
        var provider = new QualcommAihubWhisperProvider(Aihub, QualcommCatalogTests.Arm64WithNatives, _ => new FakeTranscriber(), new SharedResourceCache<SharedQnnModel>());
        await provider.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await provider.RecognizeWindowAsync(new float[16_000], null, CancellationToken.None));
    }

    [Fact]
    public async Task A_cpu_fallback_of_the_moonshine_path_is_reported_in_the_runtime()
    {
        var model = new InstalledSpeechModel(LegacyBackend.QualcommQnn, "moonshine-tiny-v2-en", "Moonshine", @"C:\models\moon", true);
        var fake = new FakeTranscriber { Fallback = "QNN session creation failed" };
        await using var provider = new MoonshineQnnProvider(model, QualcommCatalogTests.Arm64WithNatives, _ => fake, new SharedResourceCache<SharedQnnModel>());

        await provider.RecognizeWindowAsync(new float[16_000], null, CancellationToken.None);

        Assert.Equal("cpu", provider.Runtime.EffectiveBackend);
        Assert.Equal("QNN session creation failed", provider.Runtime.FallbackReason);
    }

    [Fact]
    public void The_provider_factory_picks_the_engine_the_wpf_app_chose()
    {
        Assert.IsType<QualcommAihubWhisperProvider>(SpeechProviders.Create(Aihub));
        var moonshine = new InstalledSpeechModel(LegacyBackend.QualcommQnn, "moonshine-tiny-v2-en", "Moonshine", @"C:\models\moon", true);
        Assert.IsType<MoonshineQnnProvider>(SpeechProviders.Create(moonshine));
    }

    // --- the Moonshine fallback rule ---

    private sealed class Boom : IQnnTranscriber
    {
        public bool Disposed { get; private set; }

        public string DiagnosticsSummary => "npu";

        public string Transcribe(float[] samples, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("npu failed");

        public void Dispose() => this.Disposed = true;
    }

    [Fact]
    public void Moonshine_falls_back_to_the_cpu_when_the_npu_session_cannot_be_created_unless_strict()
    {
        var cpu = new FakeTranscriber("from cpu");
        using var relaxed = MoonshineQnnTranscriber.Create(() => throw new InvalidOperationException("no qnn"), () => cpu, strict: false);
        Assert.Equal("from cpu", relaxed.Transcribe(new float[100], CancellationToken.None));
        Assert.Equal("QNN session creation failed", relaxed.FallbackReason);

        Assert.Throws<InvalidOperationException>(() => MoonshineQnnTranscriber.Create(() => throw new InvalidOperationException("no qnn"), () => cpu, strict: true));
    }

    [Fact]
    public void Moonshine_falls_back_to_the_cpu_after_a_failed_inference_unless_strict()
    {
        var npu = new Boom();
        var cpu = new FakeTranscriber("from cpu");
        using var relaxed = MoonshineQnnTranscriber.Create(() => npu, () => cpu, strict: false);
        Assert.Null(relaxed.FallbackReason);
        Assert.Equal("from cpu", relaxed.Transcribe(new float[100], CancellationToken.None));
        Assert.True(npu.Disposed);
        Assert.Equal("QNN inference failed", relaxed.FallbackReason);
        Assert.Equal("from cpu", relaxed.Transcribe(new float[100], CancellationToken.None));
        Assert.Equal(2, cpu.Calls);

        using var strict = MoonshineQnnTranscriber.Create(() => new Boom(), () => cpu, strict: true);
        Assert.Throws<InvalidOperationException>(() => strict.Transcribe(new float[100], CancellationToken.None));
        Assert.Equal(2, cpu.Calls);
    }

    [Fact]
    public void Moonshine_on_the_npu_uses_the_npu_when_it_works()
    {
        var npu = new FakeTranscriber("from npu");
        var cpu = new FakeTranscriber("from cpu");
        using var transcriber = MoonshineQnnTranscriber.Create(() => npu, () => cpu, strict: false);
        Assert.Equal("from npu", transcriber.Transcribe(new float[100], CancellationToken.None));
        Assert.Equal(0, cpu.Calls);
        Assert.Equal("fake", transcriber.DiagnosticsSummary);
    }

    // --- prepared Moonshine QNN files ---

    private string MoonshineFolder(params string[] files)
    {
        var dir = Path.Combine(this.root, "moonshine-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        foreach (var file in files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(dir, file))!);
            File.WriteAllText(Path.Combine(dir, file), "x");
        }

        return dir;
    }

    [Fact]
    public void Moonshine_v2_needs_a_qnn_encoder_and_decoder()
    {
        var stock = new[] { "tokens.txt", "encoder_model.ort", "decoder_model_merged.ort" };
        Assert.False(MoonshineQnnArtifacts.IsPrepared(this.MoonshineFolder(stock)));
        Assert.False(MoonshineQnnArtifacts.IsPrepared(this.MoonshineFolder([.. stock, "qnn/encoder.qdq.onnx"])));

        var ready = this.MoonshineFolder([.. stock, "qnn/encoder.qdq.onnx", "qnn/decoder.qnn.onnx"]);
        Assert.True(MoonshineQnnArtifacts.IsPrepared(ready));
        var files = SpeechModelLocator.ResolveMoonshine(ready)!;
        Assert.Equal(
            [Path.Combine(ready, "qnn", "encoder.qdq.onnx"), Path.Combine(ready, "qnn", "decoder.qnn.onnx")],
            MoonshineQnnArtifacts.ResolveStages(ready, files));

        // The name derived from the stock file works too.
        var derived = this.MoonshineFolder([.. stock, "qnn/encoder_model.qdq.onnx", "qnn/decoder_model_merged.qdq.onnx"]);
        Assert.True(MoonshineQnnArtifacts.IsPrepared(derived));
    }

    [Fact]
    public void Moonshine_v1_needs_all_four_qnn_stages_by_either_naming()
    {
        var stock = new[] { "tokens.txt", "preprocess.onnx", "encode.int8.onnx", "uncached_decode.int8.onnx", "cached_decode.int8.onnx" };
        Assert.False(MoonshineQnnArtifacts.IsPrepared(this.MoonshineFolder([.. stock, "qnn/preprocess.qdq.onnx"])));

        var plain = this.MoonshineFolder([.. stock, "qnn/preprocess.qdq.onnx", "qnn/encode.qdq.onnx", "qnn/uncached_decode.qdq.onnx", "qnn/cached_decode.qnn.onnx"]);
        Assert.True(MoonshineQnnArtifacts.IsPrepared(plain));
        var stages = MoonshineQnnArtifacts.ResolveStages(plain, SpeechModelLocator.ResolveMoonshine(plain)!)!;
        Assert.Equal(4, stages.Count);
        Assert.EndsWith("preprocess.qdq.onnx", stages[0]);
        Assert.EndsWith("cached_decode.qnn.onnx", stages[3]);

        var derived = this.MoonshineFolder([.. stock, "qnn/preprocess.qdq.onnx", "qnn/encode.int8.qdq.onnx", "qnn/uncached_decode.int8.qdq.onnx", "qnn/cached_decode.int8.qdq.onnx"]);
        Assert.True(MoonshineQnnArtifacts.IsPrepared(derived));
    }

    // --- parts that need no NPU ---

    [Fact]
    public void The_whisper_front_end_produces_the_shape_the_encoder_expects_and_silence_is_flat()
    {
        var tensor = new QualcommAihubWhisperFeatureExtractor().Extract(new float[8_000], CancellationToken.None);
        Assert.Equal([1, 80, 3000], tensor.Dimensions.ToArray());
    }

    [Fact]
    public void The_whisper_front_end_stops_when_cancelled()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => new QualcommAihubWhisperFeatureExtractor().Extract(new float[16_000], cancelled.Token));
    }

    [Fact]
    public void The_tiktoken_table_decodes_ids_and_skips_special_tokens()
    {
        var path = Path.Combine(this.root, "multilingual.tiktoken");
        File.WriteAllLines(path, [$"{Convert.ToBase64String("Hel"u8.ToArray())} 0", $"{Convert.ToBase64String("lo"u8.ToArray())} 1", $"{Convert.ToBase64String(" there"u8.ToArray())} 2", string.Empty]);
        var decoder = WhisperTiktokenDecoder.Load(path);
        Assert.Equal("Hello there", decoder.Decode([50258, 0, 1, 2, 50257]));
        Assert.Equal(string.Empty, decoder.Decode([50257]));

        File.WriteAllText(path, string.Empty);
        Assert.Throws<InvalidOperationException>(() => WhisperTiktokenDecoder.Load(path));
    }

    [Fact]
    public void The_moonshine_tokens_file_decodes_pieces()
    {
        var path = Path.Combine(this.root, "tokens.txt");
        File.WriteAllLines(path, ["<s> 1", "</s> 2", "\u2581hello 3", "\u2581world 4"]);
        var tokenizer = MoonshineTokenizer.Load(path);
        Assert.Equal(1, tokenizer.SosTokenId);
        Assert.Equal(2, tokenizer.EosTokenId);
        Assert.Equal("hello world", tokenizer.Decode([1, 3, 4, 2]));

        File.WriteAllLines(path, ["a 1"]);
        Assert.Throws<InvalidOperationException>(() => MoonshineTokenizer.Load(path));
    }

    // --- the --qnn-* developer commands ---

    [Theory]
    [InlineData("--qnn-aihub-whisper-transcribe", new[] { "m", "a.wav" }, "m", "a.wav", null)]
    [InlineData("--qnn-aihub-whisper-transcribe", new[] { "m", "a.wav", "out.json" }, "m", "a.wav", "out.json")]
    [InlineData("--qnn-whisper-smoke", new[] { "m", "Npu" }, "m", "Npu", null)]
    [InlineData("--qnn-smoke", new[] { "m", "Cpu", "out.json" }, "m", "Cpu", "out.json")]
    [InlineData("--qnn-proof", new[] { "m" }, "m", "true", null)]
    [InlineData("--qnn-whisper-proof", new[] { "m", "false", "out.json" }, "m", "false", "out.json")]
    public void The_validation_commands_take_the_same_arguments_as_the_wpf_app(string name, string[] rest, string model, string argument, string? output)
    {
        Assert.True(QnnValidationCommand.TryParse([name, .. rest], out var command, out var usage));
        Assert.Null(usage);
        Assert.Equal(new QnnValidationCommand(name, model, argument, output), command);
    }

    [Theory]
    [InlineData("--qnn-aihub-whisper-transcribe", "m")]
    [InlineData("--qnn-smoke", "m")]
    [InlineData("--qnn-whisper-smoke")]
    [InlineData("--qnn-proof")]
    public void A_validation_command_with_missing_arguments_explains_its_usage(params string[] args)
    {
        Assert.True(QnnValidationCommand.TryParse(args, out var command, out var usage));
        Assert.Null(command);
        Assert.StartsWith("Usage: " + args[0], usage);
    }

    [Fact]
    public void Other_arguments_are_not_validation_commands()
    {
        Assert.False(QnnValidationCommand.TryParse([], out _, out _));
        Assert.False(QnnValidationCommand.TryParse(["--background"], out _, out _));
        Assert.False(QnnValidationCommand.TryParse(["--quit"], out _, out _));
        Assert.Equal(-1, QnnValidation.Run(["--show"], TextWriter.Null, TextWriter.Null));

        var error = new StringWriter();
        Assert.Equal(1, QnnValidation.Run(["--qnn-smoke"], TextWriter.Null, error));
        Assert.Contains("Usage: --qnn-smoke", error.ToString());
    }
}
