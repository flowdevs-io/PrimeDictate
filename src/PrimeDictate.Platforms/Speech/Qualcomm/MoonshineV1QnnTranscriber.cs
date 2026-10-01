using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace PrimeDictate.Platforms.Speech.Qualcomm;

/// <summary>Moonshine v1 (preprocess, encode, uncached and cached decode) on the QNN provider, with a managed greedy decoder. Ported from the WPF app.</summary>
internal sealed class MoonshineV1QnnTranscriber : IQnnTranscriber
{
    private readonly InferenceSession preprocessSession;
    private readonly InferenceSession encodeSession;
    private readonly InferenceSession uncachedDecodeSession;
    private readonly InferenceSession cachedDecodeSession;
    private readonly MoonshineTokenizer tokenizer;
    private readonly string modelDirectory;
    private readonly QnnRuntimeOptions runtimeOptions;

    private MoonshineV1QnnTranscriber(
        string modelDirectory,
        QnnRuntimeOptions runtimeOptions,
        InferenceSession preprocessSession,
        InferenceSession encodeSession,
        InferenceSession uncachedDecodeSession,
        InferenceSession cachedDecodeSession,
        MoonshineTokenizer tokenizer)
    {
        this.modelDirectory = modelDirectory;
        this.runtimeOptions = runtimeOptions;
        this.preprocessSession = preprocessSession;
        this.encodeSession = encodeSession;
        this.uncachedDecodeSession = uncachedDecodeSession;
        this.cachedDecodeSession = cachedDecodeSession;
        this.tokenizer = tokenizer;
    }

    public string DiagnosticsSummary =>
        $"activeRuntime=QnnHtp, version=v1, modelDirectory={this.modelDirectory}, runtimePlan=({QnnRuntimeSupport.DescribeRuntimePlan(QnnActiveRuntime.QnnHtp, this.runtimeOptions, contextFilePath: "<per-session>")})";

    /// <param name="stagePaths">preprocess, encode, uncached decode, cached decode, in that order (from <see cref="MoonshineQnnArtifacts"/>).</param>
    public static MoonshineV1QnnTranscriber Create(string modelDirectory, QnnRuntimeOptions runtimeOptions, IReadOnlyList<string> stagePaths, string tokensPath)
    {
        var tokenizer = MoonshineTokenizer.Load(tokensPath);

        InferenceSession CreateSession(string sessionName, string modelPath) =>
            MoonshineOrt.CreateQnnSession(modelDirectory, runtimeOptions, sessionName, modelPath);

        return new MoonshineV1QnnTranscriber(
            modelDirectory,
            runtimeOptions,
            CreateSession("MoonshineV1Preprocess", stagePaths[0]),
            CreateSession("MoonshineV1Encode", stagePaths[1]),
            CreateSession("MoonshineV1UncachedDecode", stagePaths[2]),
            CreateSession("MoonshineV1CachedDecode", stagePaths[3]),
            tokenizer);
    }

    public string Transcribe(float[] samples, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var audioTensor = new DenseTensor<float>(samples, [1, samples.Length]);
        var features = RunSingleOutput(
            this.preprocessSession,
            NamedOnnxValue.CreateFromTensor(this.preprocessSession.InputNames[0], audioTensor));
        var featuresLength = features.Dimensions[1];
        var featuresLengthTensor = new DenseTensor<int>(new[] { featuresLength }, [1]);
        var encoderOut = RunSingleOutput(
            this.encodeSession,
            NamedOnnxValue.CreateFromTensor(this.encodeSession.InputNames[0], features),
            NamedOnnxValue.CreateFromTensor(this.encodeSession.InputNames[1], featuresLengthTensor));

        var tokens = new List<int>();
        var seqLen = 1;
        var currentToken = this.tokenizer.SosTokenId;

        var decodeResult = RunDecoder(this.uncachedDecodeSession, currentToken, seqLen, encoderOut, states: null);
        var maxLen = Math.Max(1, (int)Math.Ceiling(encoderOut.Dimensions[1] * 384d / 16_000d * 6d));

        for (var i = 0; i < maxLen; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var nextToken = ArgMax(decodeResult.Logits);
            if (nextToken == this.tokenizer.EosTokenId)
            {
                break;
            }

            tokens.Add(nextToken);
            seqLen += 1;
            decodeResult = RunDecoder(this.cachedDecodeSession, nextToken, seqLen, encoderOut, decodeResult.States);
        }

        return this.tokenizer.Decode(tokens);
    }

    public void Dispose()
    {
        this.cachedDecodeSession.Dispose();
        this.uncachedDecodeSession.Dispose();
        this.encodeSession.Dispose();
        this.preprocessSession.Dispose();
    }

    private static DecoderStepResult RunDecoder(
        InferenceSession session,
        int token,
        int tokenLength,
        DenseTensor<float> encoderOut,
        IReadOnlyList<DenseTensor<float>>? states)
    {
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(session.InputNames[0], new DenseTensor<int>(new[] { token }, [1, 1])),
            NamedOnnxValue.CreateFromTensor(session.InputNames[1], encoderOut),
            NamedOnnxValue.CreateFromTensor(session.InputNames[2], new DenseTensor<int>(new[] { tokenLength }, [1]))
        };

        if (states is not null)
        {
            for (var i = 3; i < session.InputNames.Count && (i - 3) < states.Count; i++)
            {
                inputs.Add(NamedOnnxValue.CreateFromTensor(session.InputNames[i], states[i - 3]));
            }
        }

        using var outputs = session.Run(inputs, session.OutputNames);
        var logits = MoonshineOrt.CopyFloatTensor(outputs[0]);
        var nextStates = new List<DenseTensor<float>>(Math.Max(0, outputs.Count - 1));
        for (var i = 1; i < outputs.Count; i++)
        {
            nextStates.Add(MoonshineOrt.CopyFloatTensor(outputs[i]));
        }

        return new DecoderStepResult(logits, nextStates);
    }

    private static DenseTensor<float> RunSingleOutput(InferenceSession session, params NamedOnnxValue[] inputs)
    {
        using var outputs = session.Run(inputs, [session.OutputNames[0]]);
        return MoonshineOrt.CopyFloatTensor(outputs[0]);
    }

    private static int ArgMax(DenseTensor<float> logits)
    {
        var span = logits.Buffer.Span;
        if (span.IsEmpty)
        {
            return 0;
        }

        var bestIndex = 0;
        var bestValue = span[0];
        for (var i = 1; i < span.Length; i++)
        {
            if (span[i] > bestValue)
            {
                bestValue = span[i];
                bestIndex = i;
            }
        }

        return bestIndex;
    }

    private sealed record DecoderStepResult(DenseTensor<float> Logits, IReadOnlyList<DenseTensor<float>> States);
}
