using System.Diagnostics;
using PrimeDictate.Core.Diagnostics;
using PrimeDictate.Core.Dictation;
using PrimeDictate.Core.Providers;
using PrimeDictate.Core.Transcripts;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace PrimeDictate.Platforms.Speech;

/// <summary>
/// A loaded ggml model shared by every <see cref="WhisperNetProvider"/> that uses the same file. Dictation and the transcription workspace
/// each create their own processor from it, but the weights (and the GPU memory) are loaded once. <see cref="Gate"/> serializes the
/// processors, because whisper.cpp runs one inference at a time per context.
/// </summary>
public sealed class WhisperNetModel : IDisposable
{
    private readonly WhisperFactory factory;

    private WhisperNetModel(WhisperFactory factory, string? openVinoEncoder, string openVinoDevice)
    {
        this.factory = factory;
        this.OpenVinoEncoder = openVinoEncoder;
        this.OpenVinoDevice = openVinoDevice;
    }

    public SemaphoreSlim Gate { get; } = new(1, 1);

    public string? OpenVinoEncoder { get; }

    public string OpenVinoDevice { get; }

    internal static WhisperNetModel Load(string modelPath)
    {
        var preference = WhisperNetRuntime.Preference;
        var sidecar = SpeechModelLocator.WhisperNetOpenVinoEncoder(modelPath);
        // The OpenVINO encoder is used only when the NPU is what runs, as in the WPF app (it never ran OpenVINO on the CPU or GPU).
        var effective = WhisperNetRuntime.Resolve(preference, MachineSupport.Current, sidecar is not null);
        var openVino = effective == WhisperNetDevicePreference.Npu ? sidecar : null;
        if (effective == WhisperNetDevicePreference.Npu && sidecar is null)
        {
            AppLog.Event("whisper.net", $"OpenVINO encoder files were not found next to {Path.GetFileName(modelPath)}. Using the CPU runtime.");
        }

        WhisperNetRuntime.ApplyOnce(effective, openVino is not null);
        var clock = Stopwatch.StartNew();
        var factory = WhisperFactory.FromPath(modelPath);
        clock.Stop();
        var loaded = RuntimeOptions.LoadedLibrary;
        AppLog.Event("whisper.net", $"Loaded {Path.GetFileName(modelPath)} in {clock.ElapsedMilliseconds} ms; requested {preference}, running {effective}, native runtime {loaded?.ToString() ?? "unknown"}.");
        if (openVino is not null && loaded != RuntimeLibrary.OpenVino)
        {
            var message = $"The NPU (OpenVINO) was requested for Whisper.net, but it loaded {loaded?.ToString() ?? "no OpenVINO runtime"} instead, so transcription runs without the NPU.";
            AppLog.Event("whisper.net", message);
            WhisperNetRuntime.Notice?.Invoke(message);
        }

        if (WhisperNetRuntime.GpuWantedButNotLoaded(preference, loaded))
        {
            var message = $"GPU was requested for Whisper.net, but it loaded {loaded?.ToString() ?? "no GPU runtime"} instead. Check the NVIDIA driver (CUDA) or Vulkan support if GPU speed is expected.";
            AppLog.Event("whisper.net", message, ActivityLevel.Warning);
            WhisperNetRuntime.Notice?.Invoke(message);
        }

        return new WhisperNetModel(factory, openVino, WhisperNetRuntime.OpenVinoDevice);
    }

    public WhisperProcessor CreateProcessor(string? language)
    {
        var builder = this.factory.CreateBuilder();
        builder = language is null ? builder.WithLanguageDetection() : builder.WithLanguage(language);
        if (this.OpenVinoEncoder is { } encoder && RuntimeOptions.LoadedLibrary == RuntimeLibrary.OpenVino)
        {
            var cache = Path.Combine(Path.GetDirectoryName(encoder) ?? ".", ".openvino-cache", this.OpenVinoDevice.ToLowerInvariant());
            Directory.CreateDirectory(cache);
            builder = builder.WithOpenVinoEncoder(encoder, this.OpenVinoDevice, cache);
        }

        return builder.Build();
    }

    public void Dispose()
    {
        this.factory.Dispose();
        this.Gate.Dispose();
    }
}

/// <summary>
/// Whisper through Whisper.net (whisper.cpp, ggml models), on the GPU when the runtime allows. Loads on first use. Segments carry the
/// model's own timestamps. A processor is bound to one language, so it is rebuilt if the language changes.
/// </summary>
public sealed class WhisperNetProvider : ITranscriptionProvider
{
    private static readonly SharedResourceCache<WhisperNetModel> Models = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly InstalledSpeechModel model;
    private readonly WhisperNetDevicePreference preference = WhisperNetRuntime.Preference;
    private readonly Func<string, SharedResourceCache<WhisperNetModel>.Lease> acquire;
    private SharedResourceCache<WhisperNetModel>.Lease? lease;
    private WhisperProcessor? processor;
    private string? processorLanguage;
    private bool disposed;

    public WhisperNetProvider(InstalledSpeechModel model)
    {
        this.model = model;
        this.acquire = path => Models.Acquire(path, () => WhisperNetModel.Load(path));
        this.ModelId = model.ModelId;
        this.Capabilities = new TranscriptionProviderCapabilities(
            SupportsFiles: true,
            LiveMode: LiveRecognitionMode.BufferedWindows,
            Timing: TimingCapabilities.SegmentTimestamps,
            CombinedDiarization: false,
            MaxSpeakers: null,
            MaxWindow: TimeSpan.FromSeconds(30),
            Languages: model.IsEnglishOnly ? ["en"] : ["auto"],
            RequiredSampleRate: 16_000);
    }

    /// <summary>The loaded-model cache shared by all providers in the process. Exposed so the app can check that nothing stays loaded after exit.</summary>
    public static SharedResourceCache<WhisperNetModel> SharedModels => Models;

    public string ModelId { get; }

    public TranscriptionProviderCapabilities Capabilities { get; }

    /// <summary>What ran, read after the first recognition. A GPU request that ended on the CPU says so.</summary>
    public EffectiveRuntime Runtime
    {
        get
        {
            var loaded = RuntimeOptions.LoadedLibrary;
            var effective = loaded?.ToString().ToLowerInvariant() ?? "whisper.net";
            var wantedGpu = this.preference == WhisperNetDevicePreference.Gpu;
            var fellBack = loaded is not null && WhisperNetRuntime.GpuWantedButNotLoaded(this.preference, loaded);
            var requested = wantedGpu && !fellBack ? effective : wantedGpu ? "cuda" : this.preference.ToString().ToLowerInvariant();
            return new EffectiveRuntime("whisper.net", "1.9.0", requested, effective, fellBack ? "The GPU runtime could not be loaded." : null);
        }
    }

    public ValueTask<IStreamingRecognitionSession> StartStreamingAsync(string? language, bool diarize, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This model runs as Buffered Live; native streaming is not available.");

    /// <summary>
    /// English-only models always get "en". Otherwise an explicit language wins ("en-US" becomes "en"), "auto" asks Whisper to detect it,
    /// and no language means "en", as in the WPF app.
    /// </summary>
    public static string? ResolveLanguage(string? language, bool englishOnly)
    {
        if (englishOnly || string.IsNullOrWhiteSpace(language))
        {
            return "en";
        }

        return string.Equals(language.Trim(), "auto", StringComparison.OrdinalIgnoreCase)
            ? null
            : language.Split('-')[0].Trim().ToLowerInvariant();
    }

    public async ValueTask<IReadOnlyList<RecognizedSegment>> RecognizeWindowAsync(ReadOnlyMemory<float> samples, string? language, CancellationToken cancellationToken)
    {
        if (samples.Length == 0)
        {
            return [];
        }

        var resolved = ResolveLanguage(language, this.model.IsEnglishOnly);
        var copy = samples.ToArray();
        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(this.disposed, this);
            return await Task.Run(() => this.RunAsync(copy, resolved, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            this.gate.Release();
        }
    }

    private async Task<IReadOnlyList<RecognizedSegment>> RunAsync(float[] samples, string? language, CancellationToken cancellationToken)
    {
        this.lease ??= this.acquire(this.model.Directory);
        var shared = this.lease.Value;
        // One inference at a time per loaded model, across every provider that shares it.
        await shared.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (this.processor is null || this.processorLanguage != language)
            {
                if (this.processor is not null)
                {
                    await this.processor.DisposeAsync().ConfigureAwait(false);
                    this.processor = null;
                }

                this.processor = shared.CreateProcessor(language);
                this.processorLanguage = language;
            }

            var segments = new List<RecognizedSegment>();
            await foreach (var segment in this.processor.ProcessAsync(samples, cancellationToken).ConfigureAwait(false))
            {
                var text = segment.Text.Trim();
                // Whisper prints this marker for silence; it is not speech.
                if (text.Length == 0 || string.Equals(text, "[BLANK_AUDIO]", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Whisper.net reports token probabilities, not a calibrated confidence score.
                segments.Add(new RecognizedSegment(segment.Start, segment.End, text, null, null, null, TimingProvenance.Model));
            }

            return segments;
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
            if (this.processor is not null)
            {
                // The processor shares the native context, so it is released under the model's gate.
                var shared = this.lease?.Value;
                if (shared is not null)
                {
                    await shared.Gate.WaitAsync().ConfigureAwait(false);
                }

                try
                {
                    await this.processor.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    shared?.Gate.Release();
                }

                this.processor = null;
            }

            this.lease?.Dispose();
            this.lease = null;
        }
        finally
        {
            this.gate.Release();
        }
    }
}
