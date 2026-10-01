using PrimeDictate.Core.Diagnostics;

namespace PrimeDictate.Platforms.Speech.Qualcomm;

/// <summary>
/// Moonshine on the NPU with the WPF app's fallback rule: unless strict validation is on (<c>PRIMEDICTATE_QNN_STRICT=1</c>), a failure to
/// create the QNN sessions, or to run them, switches to the CPU for the rest of the session. Strict mode lets the error through, which is
/// what makes a run proof that the NPU did the work.
/// </summary>
internal sealed class MoonshineQnnTranscriber : IQnnTranscriber
{
    private readonly bool strict;
    private readonly Func<IQnnTranscriber> createCpu;
    private IQnnTranscriber? npu;
    private IQnnTranscriber? cpu;

    private MoonshineQnnTranscriber(IQnnTranscriber? npu, Func<IQnnTranscriber> createCpu, bool strict, string? fallbackReason)
    {
        this.npu = npu;
        this.createCpu = createCpu;
        this.strict = strict;
        this.FallbackReason = fallbackReason;
    }

    /// <summary>Why the CPU is in use, or null while the NPU is. Never contains recognized text.</summary>
    public string? FallbackReason { get; private set; }

    public string DiagnosticsSummary => this.npu is { } active
        ? active.DiagnosticsSummary
        : $"activeRuntime=Cpu, runtime=sherpa-onnx, provider=cpu, reason={this.FallbackReason}";

    public static IQnnTranscriber Create(string modelDirectory, InstalledSpeechModel model)
    {
        var options = QnnRuntimeSupport.GetRuntimeOptions(modelDirectory);
        return Create(
            () => CreateNpu(modelDirectory, options),
            () => new SherpaCpuTranscriber(model),
            options.StrictValidation);
    }

    internal static IQnnTranscriber Create(Func<IQnnTranscriber> createNpu, Func<IQnnTranscriber> createCpu, bool strict)
    {
        try
        {
            return new MoonshineQnnTranscriber(createNpu(), createCpu, strict, fallbackReason: null);
        }
        catch (Exception ex) when (!strict && ex is not OperationCanceledException)
        {
            AppLog.Event("qnn", $"Moonshine QNN session creation failed and will fall back to the CPU. Error: {ex.Message}");
            return new MoonshineQnnTranscriber(null, createCpu, strict, fallbackReason: "QNN session creation failed");
        }
    }

    public string Transcribe(float[] samples, CancellationToken cancellationToken)
    {
        if (this.npu is { } active)
        {
            try
            {
                return active.Transcribe(samples, cancellationToken);
            }
            catch (Exception ex) when (!this.strict && ex is not OperationCanceledException)
            {
                AppLog.Event("qnn", $"Moonshine QNN failed during inference and will fall back to the CPU. {active.DiagnosticsSummary}. Error: {ex.Message}");
                this.npu = null;
                active.Dispose();
                this.FallbackReason = "QNN inference failed";
            }
        }

        this.cpu ??= this.createCpu();
        return this.cpu.Transcribe(samples, cancellationToken);
    }

    public void Dispose()
    {
        this.npu?.Dispose();
        this.cpu?.Dispose();
    }

    internal static IQnnTranscriber CreateNpu(string modelDirectory, QnnRuntimeOptions options)
    {
        var files = SpeechModelLocator.ResolveMoonshine(modelDirectory)
            ?? throw new FileNotFoundException($"No valid Moonshine model (v1 or v2) found in {modelDirectory}");
        var stages = MoonshineQnnArtifacts.ResolveStages(modelDirectory, files)
            ?? throw new InvalidOperationException(files.MergedDecoder is not null
                ? "Moonshine v2 QNN requires qnn/encoder.qdq.onnx and qnn/decoder.qdq.onnx. The stock encoder_model.ort and decoder_model_merged.ort files are not QNN-ready."
                : "Moonshine v1 QNN requires prepared artifacts under the model folder's qnn subfolder.");
        return files.MergedDecoder is not null
            ? MoonshineV2QnnTranscriber.Create(modelDirectory, options, stages, files.Tokens)
            : MoonshineV1QnnTranscriber.Create(modelDirectory, options, stages, files.Tokens);
    }

    /// <summary>The CPU path: the same sherpa-onnx Moonshine provider dictation uses on any machine, driven synchronously (this runs on a worker thread).</summary>
    private sealed class SherpaCpuTranscriber(InstalledSpeechModel model) : IQnnTranscriber
    {
        private readonly SherpaMoonshineProvider provider = new(model);

        public string DiagnosticsSummary => "activeRuntime=Cpu, runtime=sherpa-onnx, provider=cpu";

        public string Transcribe(float[] samples, CancellationToken cancellationToken)
        {
            var segments = this.provider.RecognizeWindowAsync(samples, "en", cancellationToken).AsTask().GetAwaiter().GetResult();
            return string.Join(' ', segments.Select(s => s.Text.Trim())).Trim();
        }

        public void Dispose() => this.provider.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
