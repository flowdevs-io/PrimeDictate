using PrimeDictate.Core.Diagnostics;
using PrimeDictate.Core.Providers;
using PrimeDictate.Core.Transcripts;

namespace PrimeDictate.Platforms.Speech.Qualcomm;

/// <summary>A loaded Qualcomm model shared by every provider that uses the same folder, with one inference at a time (the NPU session holds the model in memory).</summary>
internal sealed class SharedQnnModel(IQnnTranscriber transcriber) : IDisposable
{
    public IQnnTranscriber Transcriber { get; } = transcriber;

    public SemaphoreSlim Gate { get; } = new(1, 1);

    public void Dispose()
    {
        this.Transcriber.Dispose();
        this.Gate.Dispose();
    }
}

/// <summary>
/// Common behaviour of the Qualcomm NPU providers: nothing is loaded until the first window, a machine that cannot run QNN fails with the
/// reason (the model is not offered there, so this is a backstop), and dictation and the transcription workspace share one loaded copy.
/// Each window yields one segment covering it, so provenance is <see cref="TimingProvenance.ApproximateChunk"/>.
/// </summary>
public abstract class QnnProviderBase : ITranscriptionProvider
{
    private static readonly SharedResourceCache<SharedQnnModel> DefaultCache = new();

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string directory;
    private readonly string cacheKey;
    private readonly QnnAvailability availability;
    private readonly SharedResourceCache<SharedQnnModel> cache;
    private readonly Func<string, IQnnTranscriber> create;
    private SharedResourceCache<SharedQnnModel>.Lease? lease;
    private bool disposed;

    internal QnnProviderBase(
        string modelId,
        string directory,
        string kind,
        QnnAvailability? availability,
        Func<string, IQnnTranscriber> create,
        SharedResourceCache<SharedQnnModel>? cache)
    {
        this.ModelId = modelId;
        this.directory = directory;
        this.cacheKey = $"{kind}|{directory}";
        this.availability = availability ?? QnnRuntimeSupport.GetAvailability();
        this.create = create;
        this.cache = cache ?? DefaultCache;
        this.Capabilities = new TranscriptionProviderCapabilities(
            SupportsFiles: true,
            LiveMode: LiveRecognitionMode.BufferedWindows,
            Timing: TimingCapabilities.SegmentTimestamps,
            CombinedDiarization: false,
            MaxSpeakers: null,
            MaxWindow: TimeSpan.FromSeconds(30),
            Languages: ["en"],
            RequiredSampleRate: 16_000);
    }

    /// <summary>The loaded-model cache shared by all Qualcomm providers in the process. Exposed so the app can check that nothing stays loaded after exit.</summary>
    internal static SharedResourceCache<SharedQnnModel> SharedModels => DefaultCache;

    public string ModelId { get; }

    public TranscriptionProviderCapabilities Capabilities { get; }

    /// <summary>What ran, read after the first recognition. The Moonshine path may have fallen back to the CPU, and then says so.</summary>
    public EffectiveRuntime Runtime
    {
        get
        {
            var fallback = this.lease?.Value.Transcriber.FallbackReason;
            return new EffectiveRuntime("onnxruntime-qnn", QnnRuntimeSupport.OnnxRuntimeVersion, "qnn-htp", fallback is null ? "qnn-htp" : "cpu", fallback);
        }
    }

    public ValueTask<IStreamingRecognitionSession> StartStreamingAsync(string? language, bool diarize, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This model runs as Buffered Live; native streaming is not available.");

    // These models are English-only, so the requested language does not change what runs.
    public async ValueTask<IReadOnlyList<RecognizedSegment>> RecognizeWindowAsync(ReadOnlyMemory<float> samples, string? language, CancellationToken cancellationToken)
    {
        if (samples.Length == 0)
        {
            return [];
        }

        if (!this.availability.SupportsQnnHtp)
        {
            throw new InvalidOperationException(this.availability.Summary);
        }

        var copy = samples.ToArray();
        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(this.disposed, this);
            var text = await Task.Run(() => this.Run(copy, cancellationToken), cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text))
            {
                return [];
            }

            return [new RecognizedSegment(TimeSpan.Zero, TimeSpan.FromSeconds(copy.Length / 16_000d), text.Trim(), null, null, null, TimingProvenance.ApproximateChunk)];
        }
        finally
        {
            this.gate.Release();
        }
    }

    private string Run(float[] samples, CancellationToken cancellationToken)
    {
        if (this.lease is null)
        {
            this.lease = this.cache.Acquire(this.cacheKey, () =>
            {
                var transcriber = this.create(this.directory);
                AppLog.Event("qnn", $"Loaded {this.GetType().Name} from {this.directory}: {transcriber.DiagnosticsSummary}");
                return new SharedQnnModel(transcriber);
            });
        }

        var shared = this.lease.Value;
        shared.Gate.Wait(cancellationToken);
        try
        {
            return shared.Transcriber.Transcribe(samples, cancellationToken);
        }
        finally
        {
            shared.Gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await this.gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (this.disposed)
            {
                return;
            }

            this.disposed = true;
            this.lease?.Dispose();
            this.lease = null;
        }
        finally
        {
            this.gate.Release();
        }
    }
}

/// <summary>Qualcomm AI Hub Whisper Small on the NPU (strict: CPU fallback is disabled, as in the WPF app).</summary>
public sealed class QualcommAihubWhisperProvider : QnnProviderBase
{
    public QualcommAihubWhisperProvider(InstalledSpeechModel model)
        : this(model, null, null, null)
    {
    }

    internal QualcommAihubWhisperProvider(
        InstalledSpeechModel model,
        QnnAvailability? availability,
        Func<string, IQnnTranscriber>? create,
        SharedResourceCache<SharedQnnModel>? cache)
        : base(model.ModelId, model.Directory, "aihub-whisper", availability, create ?? CreateTranscriber, cache)
    {
    }

    private static IQnnTranscriber CreateTranscriber(string modelDirectory)
    {
        var options = QnnRuntimeSupport.GetRuntimeOptions(modelDirectory, strictValidationOverride: true) with { EnableContextCache = false };
        return QualcommAihubWhisperTranscriber.Create(modelDirectory, options);
    }
}

/// <summary>
/// Moonshine on the NPU through ONNX Runtime QNN, from the prepared <c>qnn</c> subfolder of an installed Moonshine model. Unless
/// <c>PRIMEDICTATE_QNN_STRICT=1</c>, a failure to create the NPU sessions or to run them falls back to the CPU (sherpa-onnx), as in the WPF app.
/// </summary>
public sealed class MoonshineQnnProvider : QnnProviderBase
{
    public MoonshineQnnProvider(InstalledSpeechModel model)
        : this(model, null, null, null)
    {
    }

    internal MoonshineQnnProvider(
        InstalledSpeechModel model,
        QnnAvailability? availability,
        Func<string, IQnnTranscriber>? create,
        SharedResourceCache<SharedQnnModel>? cache)
        : base(model.ModelId, model.Directory, "moonshine", availability, create ?? (directory => MoonshineQnnTranscriber.Create(directory, model)), cache)
    {
    }
}
