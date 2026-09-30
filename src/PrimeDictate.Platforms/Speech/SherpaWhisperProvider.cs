using PrimeDictate.Core.Providers;
using PrimeDictate.Core.Transcripts;
using SherpaOnnx;

namespace PrimeDictate.Platforms.Speech;

/// <summary>
/// A sherpa-onnx offline model (Whisper, Parakeet or Moonshine) as a windowed provider. Each window (at most 30 s) yields one segment
/// whose timing covers the window, so provenance is <see cref="TimingProvenance.ApproximateChunk"/>.
/// The recognizer loads on first use and is serialized, because instances are not thread-safe.
/// </summary>
public abstract class SherpaOfflineProvider : ITranscriptionProvider
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private OfflineRecognizer? recognizer;

    protected SherpaOfflineProvider(string modelId, IReadOnlyList<string> languages)
    {
        this.ModelId = modelId;
        this.Capabilities = new TranscriptionProviderCapabilities(
            SupportsFiles: true,
            LiveMode: LiveRecognitionMode.BufferedWindows,
            Timing: TimingCapabilities.SegmentTimestamps,
            CombinedDiarization: false,
            MaxSpeakers: null,
            MaxWindow: TimeSpan.FromSeconds(30),
            Languages: languages,
            RequiredSampleRate: 16_000);
    }

    public string ModelId { get; }

    public TranscriptionProviderCapabilities Capabilities { get; }

    public EffectiveRuntime Runtime => new("sherpa-onnx", "1.13.0", this.ProviderName, this.ProviderName, null);

    /// <summary><c>cpu</c> or <c>cuda</c> for this model. Families whose files have no GPU-friendly variant stay on the CPU even when CUDA is active.</summary>
    protected virtual string ProviderName => OnnxRuntimeDevice.Provider;

    public async ValueTask<IReadOnlyList<RecognizedSegment>> RecognizeWindowAsync(ReadOnlyMemory<float> samples, string? language, CancellationToken cancellationToken)
    {
        if (samples.Length == 0)
        {
            return [];
        }

        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var copy = samples.ToArray();
            var text = await Task.Run(() => this.Decode(copy, language), cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text))
            {
                return [];
            }

            var duration = TimeSpan.FromSeconds(copy.Length / 16_000d);
            return [new RecognizedSegment(TimeSpan.Zero, duration, text, null, null, null, TimingProvenance.ApproximateChunk)];
        }
        finally
        {
            this.gate.Release();
        }
    }

    public ValueTask<IStreamingRecognitionSession> StartStreamingAsync(string? language, bool diarize, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This model runs as Buffered Live; native streaming is not available.");

    public async ValueTask DisposeAsync()
    {
        await this.gate.WaitAsync().ConfigureAwait(false);
        try
        {
            this.recognizer?.Dispose();
            this.recognizer = null;
        }
        finally
        {
            this.gate.Release();
        }
    }

    private string Decode(float[] samples, string? language)
    {
        var recognizer = this.recognizer ??= this.Load(language);
        using var stream = recognizer.CreateStream();
        stream.AcceptWaveform(16_000, samples);
        recognizer.Decode([stream]);
        try
        {
            return stream.Result.Text.Trim();
        }
        catch (NullReferenceException)
        {
            // The sherpa wrapper throws when the native result is null (no speech in the window).
            return string.Empty;
        }
    }

    /// <summary>Fills in the model files and type for this model family. Common settings are already applied. <c>ref</c> because the sherpa config types are structs: a by-value copy would silently drop every setting.</summary>
    protected abstract void Configure(ref OfflineRecognizerConfig config, string? language);

    private OfflineRecognizer Load(string? language)
    {
        var config = new OfflineRecognizerConfig();
        config.FeatConfig.SampleRate = 16_000;
        config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.Debug = 0;
        config.ModelConfig.Provider = this.ProviderName;
        config.ModelConfig.NumThreads = InferenceThreads.Default;
        this.Configure(ref config, language);
        return new OfflineRecognizer(config);
    }
}

/// <summary>Whisper through sherpa-onnx.</summary>
public sealed class SherpaWhisperProvider(InstalledWhisperModel model)
    : SherpaOfflineProvider(model.ModelId, model.IsEnglishOnly ? ["en"] : ["auto"])
{
    protected override string ProviderName => OnnxRuntimeDevice.IsCuda && model.HasFullPrecision ? "cuda" : "cpu";

    protected override void Configure(ref OfflineRecognizerConfig config, string? language)
    {
        var full = this.ProviderName == "cuda";
        config.ModelConfig.Tokens = model.Tokens;
        config.ModelConfig.Whisper.Encoder = full ? model.EncoderFullPrecision! : model.Encoder;
        config.ModelConfig.Whisper.Decoder = full ? model.DecoderFullPrecision! : model.Decoder;
        // English-only models must be told "en"; multilingual models detect the language when it is empty.
        config.ModelConfig.Whisper.Language = model.IsEnglishOnly ? "en" : (language is null or "auto" ? string.Empty : language.Split('-')[0].ToLowerInvariant());
        config.ModelConfig.Whisper.Task = "transcribe";
    }
}

/// <summary>NVIDIA Parakeet TDT (NeMo transducer) through sherpa-onnx. v3 is multilingual and detects the language itself.</summary>
public sealed class SherpaParakeetProvider(InstalledSpeechModel model)
    : SherpaOfflineProvider(model.ModelId, model.IsEnglishOnly ? ["en"] : ["auto"])
{
    protected override string ProviderName =>
        OnnxRuntimeDevice.IsCuda && SpeechModelLocator.ParakeetPrecision(model.Directory, preferHalf: true) == "fp16" ? "cuda" : "cpu";

    protected override void Configure(ref OfflineRecognizerConfig config, string? language)
    {
        var precision = SpeechModelLocator.ParakeetPrecision(model.Directory, this.ProviderName == "cuda")
            ?? throw new FileNotFoundException($"The Parakeet model folder is incomplete: {model.Directory}");
        config.ModelConfig.Tokens = Path.Combine(model.Directory, "tokens.txt");
        config.ModelConfig.Transducer.Encoder = Path.Combine(model.Directory, $"encoder.{precision}.onnx");
        config.ModelConfig.Transducer.Decoder = Path.Combine(model.Directory, $"decoder.{precision}.onnx");
        config.ModelConfig.Transducer.Joiner = Path.Combine(model.Directory, $"joiner.{precision}.onnx");
        config.ModelConfig.ModelType = "nemo_transducer";
        config.DecodingMethod = "greedy_search";
        config.MaxActivePaths = 4;
    }
}

/// <summary>Moonshine v1 (four ONNX stages) and v2 (encoder plus merged decoder) through sherpa-onnx, CPU only.</summary>
public sealed class SherpaMoonshineProvider(InstalledSpeechModel model)
    : SherpaOfflineProvider(model.ModelId, ["en"])
{
    // Moonshine ships int8 and ORT-format files only, so it stays on the CPU (it is small and fast there).
    protected override string ProviderName => "cpu";

    protected override void Configure(ref OfflineRecognizerConfig config, string? language)
    {
        var files = SpeechModelLocator.ResolveMoonshine(model.Directory)
            ?? throw new FileNotFoundException($"The Moonshine model folder is incomplete: {model.Directory}");
        config.ModelConfig.Tokens = files.Tokens;
        config.ModelConfig.Moonshine.Encoder = files.Encoder;
        if (files.MergedDecoder is not null)
        {
            config.ModelConfig.Moonshine.MergedDecoder = files.MergedDecoder;
        }
        else
        {
            config.ModelConfig.Moonshine.Preprocessor = files.Preprocessor!;
            config.ModelConfig.Moonshine.UncachedDecoder = files.UncachedDecoder!;
            config.ModelConfig.Moonshine.CachedDecoder = files.CachedDecoder!;
        }
    }
}

/// <summary>sherpa-onnx runs on a single thread unless told otherwise, which is far too slow for live recognition.</summary>
internal static class InferenceThreads
{
    /// <summary>Half the logical cores, at least 2 and at most 8, leaving room for capture and the UI.</summary>
    public static int Default { get; } = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
}
