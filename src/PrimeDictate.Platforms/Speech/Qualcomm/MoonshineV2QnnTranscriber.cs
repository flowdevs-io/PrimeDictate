using System.Text;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using PrimeDictate.Core.Diagnostics;

namespace PrimeDictate.Platforms.Speech.Qualcomm;

/// <summary>Moonshine v2 (encoder plus merged decoder with key/value caches) on the QNN provider, with a managed greedy decoder. Ported from the WPF app.</summary>
internal sealed class MoonshineV2QnnTranscriber : IQnnTranscriber
{
    private const int SampleRate = 16_000;
    private const int MaxSamplesPerWindow = SampleRate * 8;
    private const double MaxDecoderTokensPerAudioSecond = 24d;
    private const int DecoderTokenAllowance = 32;
    private const int MaxDecoderTokens = 768;

    private readonly InferenceSession encoderSession;
    private readonly InferenceSession decoderSession;
    private readonly MoonshineTokenizer tokenizer;
    private readonly string modelDirectory;
    private readonly QnnRuntimeOptions runtimeOptions;

    private MoonshineV2QnnTranscriber(
        string modelDirectory,
        QnnRuntimeOptions runtimeOptions,
        InferenceSession encoderSession,
        InferenceSession decoderSession,
        MoonshineTokenizer tokenizer)
    {
        this.modelDirectory = modelDirectory;
        this.runtimeOptions = runtimeOptions;
        this.encoderSession = encoderSession;
        this.decoderSession = decoderSession;
        this.tokenizer = tokenizer;
    }

    public string DiagnosticsSummary =>
        $"activeRuntime=QnnHtp, version=v2, modelDirectory={this.modelDirectory}, runtimePlan=({QnnRuntimeSupport.DescribeRuntimePlan(QnnActiveRuntime.QnnHtp, this.runtimeOptions, contextFilePath: "<per-session>")})";

    /// <param name="stagePaths">Encoder then decoder (from <see cref="MoonshineQnnArtifacts"/>).</param>
    public static MoonshineV2QnnTranscriber Create(string modelDirectory, QnnRuntimeOptions runtimeOptions, IReadOnlyList<string> stagePaths, string tokensPath)
    {
        var tokenizer = MoonshineTokenizer.Load(tokensPath);
        return new MoonshineV2QnnTranscriber(
            modelDirectory,
            runtimeOptions,
            MoonshineOrt.CreateQnnSession(modelDirectory, runtimeOptions, "MoonshineV2Encoder", stagePaths[0]),
            MoonshineOrt.CreateQnnSession(modelDirectory, runtimeOptions, "MoonshineV2Decoder", stagePaths[1]),
            tokenizer);
    }

    public string Transcribe(float[] samples, CancellationToken cancellationToken)
    {
        if (samples.Length <= MaxSamplesPerWindow)
        {
            return this.TranscribeWindow(samples, cancellationToken);
        }

        var transcript = new StringBuilder();
        for (var offset = 0; offset < samples.Length; offset += MaxSamplesPerWindow)
        {
            var chunkLength = Math.Min(MaxSamplesPerWindow, samples.Length - offset);
            var chunk = new float[chunkLength];
            Array.Copy(samples, offset, chunk, 0, chunkLength);

            var chunkTranscript = this.TranscribeWindow(chunk, cancellationToken);
            if (string.IsNullOrWhiteSpace(chunkTranscript))
            {
                continue;
            }

            if (transcript.Length > 0)
            {
                transcript.Append(' ');
            }

            transcript.Append(chunkTranscript.Trim());
        }

        return transcript.ToString().Trim();
    }

    public void Dispose()
    {
        this.decoderSession.Dispose();
        this.encoderSession.Dispose();
    }

    private string TranscribeWindow(float[] samples, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var audioInputName = ResolveInputName(this.encoderSession, ["input_values", "audio", "input"])!;
        var audioMaskName = ResolveInputName(this.encoderSession, ["attention_mask", "mask"], required: false);

        var encoderInputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(audioInputName, new DenseTensor<float>(samples, [1, samples.Length]))
        };

        if (audioMaskName is not null)
        {
            encoderInputs.Add(NamedOnnxValue.CreateFromTensor(
                audioMaskName,
                new DenseTensor<long>(CreateFilledLongArray(samples.Length), [1, samples.Length])));
        }

        using var encoderOutputs = this.encoderSession.Run(encoderInputs);
        var encoderOut = MoonshineOrt.CopyFloatTensor(encoderOutputs[0]);

        var encoderMaskName = ResolveInputName(this.decoderSession, ["encoder_attention_mask", "encoder_mask"])!;
        var inputIdsName = ResolveInputName(this.decoderSession, ["input_ids", "token"])!;
        var encoderHiddenName = ResolveInputName(this.decoderSession, ["encoder_hidden_states", "encoder_out", "hidden"])!;
        var useCacheBranchName = ResolveInputName(this.decoderSession, ["use_cache_branch"], required: false);
        var pastInputNames = this.decoderSession.InputNames
            .Where(name => name.StartsWith("past_key_values.", StringComparison.Ordinal))
            .ToArray();

        var encoderFrames = encoderOut.Dimensions[1];
        var encoderMask = new DenseTensor<long>(CreateFilledLongArray(encoderFrames), [1, encoderFrames]);
        var stateByInputName = this.CreateInitialPastStates(pastInputNames);
        Dictionary<string, DenseTensor<float>>? encoderCacheByInputName = null;

        var tokens = new List<int>();
        var currentToken = this.tokenizer.SosTokenId;
        var maxLen = Math.Clamp(
            (int)Math.Ceiling(samples.Length / (double)SampleRate * MaxDecoderTokensPerAudioSecond) + DecoderTokenAllowance,
            DecoderTokenAllowance,
            MaxDecoderTokens);
        var reachedDecoderLimit = true;

        for (var i = 0; i < maxLen; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var decoderInputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor(encoderMaskName, encoderMask),
                NamedOnnxValue.CreateFromTensor(inputIdsName, new DenseTensor<long>(new[] { (long)currentToken }, [1, 1])),
                NamedOnnxValue.CreateFromTensor(encoderHiddenName, encoderOut)
            };

            foreach (var pastInputName in pastInputNames)
            {
                decoderInputs.Add(NamedOnnxValue.CreateFromTensor(pastInputName, stateByInputName[pastInputName]));
            }

            if (useCacheBranchName is not null)
            {
                decoderInputs.Add(NamedOnnxValue.CreateFromTensor(useCacheBranchName, new DenseTensor<bool>(new[] { i > 0 }, [1])));
            }

            using var outputs = this.decoderSession.Run(decoderInputs);
            var logits = MoonshineOrt.CopyFloatTensor(outputs[0]);
            var nextToken = ArgMaxLastDimension(logits);

            if (nextToken == this.tokenizer.EosTokenId)
            {
                reachedDecoderLimit = false;
                break;
            }

            tokens.Add(nextToken);
            currentToken = nextToken;

            var nextStateByInputName = new Dictionary<string, DenseTensor<float>>(StringComparer.Ordinal);
            for (var outputIndex = 1; outputIndex < outputs.Count; outputIndex++)
            {
                var stateInputName = ToPastInputName(outputs[outputIndex].Name);
                if (stateInputName is not null && pastInputNames.Contains(stateInputName))
                {
                    nextStateByInputName[stateInputName] = MoonshineOrt.CopyFloatTensor(outputs[outputIndex]);
                }
            }

            if (encoderCacheByInputName is null)
            {
                encoderCacheByInputName = nextStateByInputName
                    .Where(item => item.Key.Contains(".encoder.", StringComparison.Ordinal))
                    .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
            }
            else
            {
                foreach (var item in encoderCacheByInputName)
                {
                    nextStateByInputName[item.Key] = item.Value;
                }
            }

            foreach (var pastInputName in pastInputNames)
            {
                if (!nextStateByInputName.ContainsKey(pastInputName) &&
                    stateByInputName.TryGetValue(pastInputName, out var previousState))
                {
                    nextStateByInputName[pastInputName] = previousState;
                }
            }

            stateByInputName = nextStateByInputName;
        }

        if (reachedDecoderLimit)
        {
            AppLog.Event("qnn", $"Moonshine v2 decoder reached its token budget ({maxLen:N0} tokens for {samples.Length / (double)SampleRate:N2}s audio).");
        }

        return this.tokenizer.Decode(tokens);
    }

    private static long[] CreateFilledLongArray(int length)
    {
        var values = new long[length];
        Array.Fill(values, 1L);
        return values;
    }

    private Dictionary<string, DenseTensor<float>> CreateInitialPastStates(IEnumerable<string> inputNames)
    {
        var states = new Dictionary<string, DenseTensor<float>>(StringComparer.Ordinal);
        foreach (var inputName in inputNames)
        {
            var numHeads = this.GetInputDimensionOrDefault(inputName, 1, 8);
            var headDim = this.GetInputDimensionOrDefault(inputName, 3, 36);
            states[inputName] = new DenseTensor<float>(new float[0], [1, numHeads, 0, headDim]);
        }

        return states;
    }

    private int GetInputDimensionOrDefault(string inputName, int dimensionIndex, int fallback)
    {
        if (this.decoderSession.InputMetadata.TryGetValue(inputName, out var metadata) &&
            metadata.Dimensions.Length > dimensionIndex &&
            metadata.Dimensions[dimensionIndex] > 0)
        {
            return metadata.Dimensions[dimensionIndex];
        }

        return fallback;
    }

    private static string? ResolveInputName(InferenceSession session, IReadOnlyList<string> candidates, bool required = true)
    {
        foreach (var candidate in candidates)
        {
            var exactMatch = session.InputNames.FirstOrDefault(name => string.Equals(name, candidate, StringComparison.Ordinal));
            if (exactMatch is not null)
            {
                return exactMatch;
            }
        }

        foreach (var candidate in candidates)
        {
            var containsMatch = session.InputNames.FirstOrDefault(name => name.Contains(candidate, StringComparison.Ordinal));
            if (containsMatch is not null)
            {
                return containsMatch;
            }
        }

        if (!required)
        {
            return null;
        }

        throw new InvalidOperationException(
            $"Moonshine v2 model input mismatch. Expected one of [{string.Join(", ", candidates)}], but model inputs are [{string.Join(", ", session.InputNames)}].");
    }

    private static string? ToPastInputName(string outputName)
    {
        const string prefix = "present.";
        return outputName.StartsWith(prefix, StringComparison.Ordinal)
            ? "past_key_values." + outputName[prefix.Length..]
            : null;
    }

    private static int ArgMaxLastDimension(DenseTensor<float> logits)
    {
        var span = logits.Buffer.Span;
        if (span.IsEmpty)
        {
            return 0;
        }

        var vocabularySize = logits.Dimensions[^1];
        var start = Math.Max(0, span.Length - vocabularySize);
        var bestIndex = 0;
        var bestValue = span[start];
        for (var i = 1; i < vocabularySize; i++)
        {
            var candidate = span[start + i];
            if (candidate > bestValue)
            {
                bestValue = candidate;
                bestIndex = i;
            }
        }

        return bestIndex;
    }
}
