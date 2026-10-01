using System.Text;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using PrimeDictate.Core.Diagnostics;

namespace PrimeDictate.Platforms.Speech.Qualcomm;

/// <summary>What a Qualcomm provider needs from a loaded model: text from float samples at 16 kHz. Lets the providers be tested without the NPU.</summary>
internal interface IQnnTranscriber : IDisposable
{
    string Transcribe(float[] samples, CancellationToken cancellationToken);

    /// <summary>Which runtime actually ran (the NPU, or the CPU after a non-strict fallback). Never contains recognized text.</summary>
    string DiagnosticsSummary { get; }

    /// <summary>Why the NPU is not in use (a non-strict fallback to the CPU), or null while it is.</summary>
    string? FallbackReason => null;
}

/// <summary>
/// Qualcomm AI Hub Whisper Small: a precompiled encoder and decoder (ONNX Runtime EPContext wrappers around QNN context binaries) run
/// through the QNN HTP provider with CPU fallback disabled, a managed log-mel front end, and a greedy decode loop with the English prompt.
/// Ported unchanged from the WPF app except that logging never includes token ids (they would spell the transcript).
/// </summary>
internal sealed class QualcommAihubWhisperTranscriber : IQnnTranscriber
{
    private const int SampleRate = 16_000;
    private const int ChunkSamples = SampleRate * 30;
    private const int MeanDecodeLength = 200;
    private const int DecoderLayers = 12;
    private const int DecoderHeads = 12;
    private const int HeadDim = 64;
    private const int EndOfTranscriptToken = 50257;
    private const int StartOfTranscriptToken = 50258;
    private const int LanguageEnglishToken = 50259;
    private const int TranscribeToken = 50359;
    private const int NoTimestampsToken = 50363;
    private const double DecoderTokensPerSecondBudget = 12d;
    private const int DecoderTokenAllowance = 16;
    private static readonly int[] PromptTokens =
    [
        StartOfTranscriptToken,
        LanguageEnglishToken,
        TranscribeToken,
        NoTimestampsToken
    ];
    private static readonly Float16 MaskNegative = (Float16)(-100f);
    private static readonly Float16 HalfZero = (Float16)0f;

    private readonly InferenceSession encoderSession;
    private readonly InferenceSession decoderSession;
    private readonly QualcommAihubWhisperFeatureExtractor featureExtractor = new();
    private readonly WhisperTiktokenDecoder tokenizer;
    private readonly QualcommAihubWhisperArtifacts artifacts;
    private readonly QnnRuntimeOptions runtimeOptions;

    private QualcommAihubWhisperTranscriber(
        QualcommAihubWhisperArtifacts artifacts,
        QnnRuntimeOptions runtimeOptions,
        InferenceSession encoderSession,
        InferenceSession decoderSession,
        WhisperTiktokenDecoder tokenizer)
    {
        this.artifacts = artifacts;
        this.runtimeOptions = runtimeOptions;
        this.encoderSession = encoderSession;
        this.decoderSession = decoderSession;
        this.tokenizer = tokenizer;
    }

    public string DiagnosticsSummary =>
        $"modelDirectory={this.artifacts.ModelDirectory}, runtimePlan=({QnnRuntimeSupport.DescribeRuntimePlan(QnnActiveRuntime.QnnHtp, this.runtimeOptions, contextFilePath: "<precompiled-ep-context>")})";

    public static QualcommAihubWhisperTranscriber Create(string modelDirectory, QnnRuntimeOptions runtimeOptions)
    {
        if (!QualcommAihubWhisperCatalog.TryResolveArtifacts(modelDirectory, out var artifacts))
        {
            throw new FileNotFoundException(
                "Qualcomm AI Hub Whisper model folder is incomplete. Expected encoder.onnx, decoder.onnx, context binaries, metadata.json, and multilingual.tiktoken.");
        }

        InferenceSession CreateSession(string sessionName, string modelPath)
        {
            using var options = QnnRuntimeSupport.CreateSessionOptions(
                QnnActiveRuntime.QnnHtp,
                runtimeOptions,
                sessionTag: $"PrimeDictate.{sessionName}",
                contextFilePath: null);
            return new InferenceSession(modelPath, options);
        }

        return new QualcommAihubWhisperTranscriber(
            artifacts.Value,
            runtimeOptions,
            CreateSession("QualcommAihubWhisperEncoder", artifacts.Value.EncoderOnnxPath),
            CreateSession("QualcommAihubWhisperDecoder", artifacts.Value.DecoderOnnxPath),
            WhisperTiktokenDecoder.Load(artifacts.Value.TokenizerPath));
    }

    public string Transcribe(float[] samples, CancellationToken cancellationToken)
    {
        if (samples.Length <= ChunkSamples)
        {
            return this.TranscribeChunk(samples, cancellationToken);
        }

        var transcript = new StringBuilder();
        for (var offset = 0; offset < samples.Length; offset += ChunkSamples)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunkLength = Math.Min(ChunkSamples, samples.Length - offset);
            var chunk = new float[chunkLength];
            Array.Copy(samples, offset, chunk, 0, chunkLength);
            var chunkText = this.TranscribeChunk(chunk, cancellationToken);
            if (string.IsNullOrWhiteSpace(chunkText))
            {
                continue;
            }

            if (transcript.Length > 0)
            {
                transcript.Append(' ');
            }

            transcript.Append(chunkText.Trim());
        }

        return transcript.ToString().Trim();
    }

    public void Dispose()
    {
        this.decoderSession.Dispose();
        this.encoderSession.Dispose();
    }

    private string TranscribeChunk(float[] samples, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var inputFeatures = this.featureExtractor.Extract(samples, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        using var encoderOutputs = this.encoderSession.Run(
            [NamedOnnxValue.CreateFromTensor("input_features", inputFeatures)]);
        var crossCacheByName = CopyFloat16Outputs(encoderOutputs);
        cancellationToken.ThrowIfCancellationRequested();

        var attentionMaskValues = CreateFilledArray<Float16>(MeanDecodeLength, MaskNegative);
        var attentionMask = new DenseTensor<Float16>(attentionMaskValues, [1, 1, 1, MeanDecodeLength]);
        var selfCacheByName = CreateInitialSelfCache();
        var outputTokens = new List<int>();
        var currentToken = PromptTokens[0];
        var position = 0;
        var maxDecodeSteps = GetMaxDecodeSteps(samples.Length);
        var reachedBudget = true;

        for (var n = 0; n < maxDecodeSteps; n++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attentionMaskValues[MeanDecodeLength - n - 1] = HalfZero;
            var inputs = new List<NamedOnnxValue>(2 + DecoderLayers * 4 + 1)
            {
                NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<int>(new[] { currentToken }, new[] { 1, 1 })),
                NamedOnnxValue.CreateFromTensor("attention_mask", attentionMask)
            };

            for (var layer = 0; layer < DecoderLayers; layer++)
            {
                inputs.Add(NamedOnnxValue.CreateFromTensor($"k_cache_self_{layer}_in", selfCacheByName[$"k_cache_self_{layer}_in"]));
                inputs.Add(NamedOnnxValue.CreateFromTensor($"v_cache_self_{layer}_in", selfCacheByName[$"v_cache_self_{layer}_in"]));
            }

            for (var layer = 0; layer < DecoderLayers; layer++)
            {
                inputs.Add(NamedOnnxValue.CreateFromTensor($"k_cache_cross_{layer}", crossCacheByName[$"k_cache_cross_{layer}"]));
                inputs.Add(NamedOnnxValue.CreateFromTensor($"v_cache_cross_{layer}", crossCacheByName[$"v_cache_cross_{layer}"]));
            }

            inputs.Add(NamedOnnxValue.CreateFromTensor("position_ids", new DenseTensor<int>(new[] { position }, new[] { 1 })));

            using var decoderOutputs = this.decoderSession.Run(inputs);
            var logits = CopyFloat16Tensor(decoderOutputs.First(output => string.Equals(output.Name, "logits", StringComparison.Ordinal)));
            var nextToken = ArgMax(logits);

            selfCacheByName = CopySelfCacheOutputs(decoderOutputs);
            if (n < PromptTokens.Length - 1)
            {
                currentToken = PromptTokens[n + 1];
                position++;
                continue;
            }

            if (nextToken == EndOfTranscriptToken)
            {
                reachedBudget = false;
                break;
            }

            outputTokens.Add(nextToken);
            currentToken = nextToken;
            position++;
        }

        var decoded = this.tokenizer.Decode(outputTokens);
        if (string.IsNullOrWhiteSpace(decoded) && outputTokens.Count > 0)
        {
            AppLog.Event("qnn", $"Qualcomm AI Hub Whisper decoded no text from {outputTokens.Count} generated tokens.");
        }
        else if (reachedBudget)
        {
            AppLog.Event("qnn", $"Qualcomm AI Hub Whisper reached decode budget ({maxDecodeSteps} steps, {outputTokens.Count} generated tokens).");
        }

        return decoded;
    }

    private static int GetMaxDecodeSteps(int sampleCount)
    {
        var audioSeconds = Math.Max(0.5d, sampleCount / (double)SampleRate);
        var generatedTokenBudget = (int)Math.Ceiling(audioSeconds * DecoderTokensPerSecondBudget) + DecoderTokenAllowance;
        return Math.Clamp(
            PromptTokens.Length + generatedTokenBudget,
            PromptTokens.Length + 8,
            MeanDecodeLength - 1);
    }

    private static Dictionary<string, DenseTensor<Float16>> CreateInitialSelfCache()
    {
        var caches = new Dictionary<string, DenseTensor<Float16>>(StringComparer.Ordinal);
        for (var layer = 0; layer < DecoderLayers; layer++)
        {
            caches[$"k_cache_self_{layer}_in"] = new DenseTensor<Float16>(
                new Float16[DecoderHeads * HeadDim * (MeanDecodeLength - 1)],
                [DecoderHeads, 1, HeadDim, MeanDecodeLength - 1]);
            caches[$"v_cache_self_{layer}_in"] = new DenseTensor<Float16>(
                new Float16[DecoderHeads * (MeanDecodeLength - 1) * HeadDim],
                [DecoderHeads, 1, MeanDecodeLength - 1, HeadDim]);
        }

        return caches;
    }

    private static Dictionary<string, DenseTensor<Float16>> CopyFloat16Outputs(IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs)
    {
        var tensors = new Dictionary<string, DenseTensor<Float16>>(StringComparer.Ordinal);
        foreach (var output in outputs)
        {
            tensors[output.Name] = CopyFloat16Tensor(output);
        }

        return tensors;
    }

    private static Dictionary<string, DenseTensor<Float16>> CopySelfCacheOutputs(IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs)
    {
        var tensors = new Dictionary<string, DenseTensor<Float16>>(StringComparer.Ordinal);
        foreach (var output in outputs)
        {
            if (!output.Name.Contains("_cache_self_", StringComparison.Ordinal))
            {
                continue;
            }

            var inputName = output.Name.Replace("_out", "_in", StringComparison.Ordinal);
            tensors[inputName] = CopyFloat16Tensor(output);
        }

        return tensors;
    }

    private static DenseTensor<Float16> CopyFloat16Tensor(DisposableNamedOnnxValue value)
    {
        var tensor = value.AsTensor<Float16>();
        return new DenseTensor<Float16>(tensor.ToArray(), tensor.Dimensions.ToArray());
    }

    private static int ArgMax(DenseTensor<Float16> logits)
    {
        var span = logits.Buffer.Span;
        if (span.IsEmpty)
        {
            return EndOfTranscriptToken;
        }

        var bestIndex = 0;
        var bestValue = span[0].ToFloat();
        for (var i = 1; i < span.Length; i++)
        {
            var candidate = span[i].ToFloat();
            if (candidate > bestValue)
            {
                bestValue = candidate;
                bestIndex = i;
            }
        }

        return bestIndex;
    }

    private static T[] CreateFilledArray<T>(int length, T value)
    {
        var values = new T[length];
        Array.Fill(values, value);
        return values;
    }
}
