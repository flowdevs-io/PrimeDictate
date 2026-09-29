using PrimeDictate.Core.Providers;
using PrimeDictate.Core.Transcripts;
using SherpaOnnx;

namespace PrimeDictate.Platforms.Speech;

/// <summary>
/// Whisper through sherpa-onnx as a windowed provider. Each window (at most 30 s) yields one segment
/// whose timing covers the window, so provenance is <see cref="TimingProvenance.ApproximateChunk"/>.
/// The recognizer loads on first use and is serialized, because instances are not thread-safe.
/// </summary>
public sealed class SherpaWhisperProvider : ITranscriptionProvider
{
    private readonly InstalledWhisperModel model;
    private readonly SemaphoreSlim gate = new(1, 1);
    private OfflineRecognizer? recognizer;

    public SherpaWhisperProvider(InstalledWhisperModel model)
    {
        this.model = model;
        this.Capabilities = new TranscriptionProviderCapabilities(
            SupportsFiles: true,
            LiveMode: LiveRecognitionMode.BufferedWindows,
            Timing: TimingCapabilities.SegmentTimestamps,
            CombinedDiarization: false,
            MaxSpeakers: null,
            MaxWindow: TimeSpan.FromSeconds(30),
            Languages: model.IsEnglishOnly ? ["en"] : ["auto"],
            RequiredSampleRate: 16_000);
        this.Runtime = new EffectiveRuntime("sherpa-onnx", "1.13.0", "cpu", "cpu", null);
    }

    public string ModelId => this.model.ModelId;

    public TranscriptionProviderCapabilities Capabilities { get; }

    public EffectiveRuntime Runtime { get; }

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
        throw new NotSupportedException("Whisper ONNX runs as Buffered Live; native streaming is not available.");

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

    private OfflineRecognizer Load(string? language)
    {
        var config = new OfflineRecognizerConfig();
        config.FeatConfig.SampleRate = 16_000;
        config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.Debug = 0;
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.NumThreads = InferenceThreads.Default;
        config.ModelConfig.Tokens = this.model.Tokens;
        config.ModelConfig.Whisper.Encoder = this.model.Encoder;
        config.ModelConfig.Whisper.Decoder = this.model.Decoder;
        // English-only models must be told "en"; multilingual models detect the language when it is empty.
        config.ModelConfig.Whisper.Language = this.model.IsEnglishOnly ? "en" : (language is null or "auto" ? string.Empty : language);
        config.ModelConfig.Whisper.Task = "transcribe";
        return new OfflineRecognizer(config);
    }
}

/// <summary>sherpa-onnx runs on a single thread unless told otherwise, which is far too slow for live recognition.</summary>
internal static class InferenceThreads
{
    /// <summary>Half the logical cores, at least 2 and at most 8, leaving room for capture and the UI.</summary>
    public static int Default { get; } = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
}
