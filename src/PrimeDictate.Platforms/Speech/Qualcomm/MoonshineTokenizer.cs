using System.Text;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace PrimeDictate.Platforms.Speech.Qualcomm;

/// <summary>Shared pieces of the two managed Moonshine QNN transcribers.</summary>
internal static class MoonshineOrt
{
    /// <summary>One QNN session. The context cache (an EPContext model written once, so later loads skip compilation) lives in <c>.qnn-cache</c> inside the model folder.</summary>
    public static InferenceSession CreateQnnSession(string modelDirectory, QnnRuntimeOptions runtimeOptions, string sessionName, string modelPath)
    {
        var contextDirectory = Path.Combine(modelDirectory, ".qnn-cache");
        Directory.CreateDirectory(contextDirectory);
        var contextPath = Path.Combine(contextDirectory, $"{sessionName}_ctx.onnx");

        using var options = QnnRuntimeSupport.CreateSessionOptions(
            QnnActiveRuntime.QnnHtp,
            runtimeOptions,
            sessionTag: $"PrimeDictate.{sessionName}",
            contextFilePath: contextPath);
        return new InferenceSession(modelPath, options);
    }

    public static DenseTensor<float> CopyFloatTensor(DisposableNamedOnnxValue value)
    {
        var tensor = value.AsTensor<float>();
        return new DenseTensor<float>(tensor.ToArray(), tensor.Dimensions.ToArray());
    }
}

/// <summary>Moonshine <c>tokens.txt</c>: id to piece, with <c>&lt;s&gt;</c> and <c>&lt;/s&gt;</c> marking start and end. Some files store the pieces base64 encoded.</summary>
internal sealed class MoonshineTokenizer
{
    private readonly Dictionary<int, string> idToToken;

    private MoonshineTokenizer(Dictionary<int, string> idToToken, int sosTokenId, int eosTokenId)
    {
        this.idToToken = idToToken;
        this.SosTokenId = sosTokenId;
        this.EosTokenId = eosTokenId;
    }

    public int SosTokenId { get; }

    public int EosTokenId { get; }

    public static MoonshineTokenizer Load(string tokensPath)
    {
        var idToToken = new Dictionary<int, string>();
        int? sos = null;
        int? eos = null;
        var decodeBase64Tokens = ShouldDecodeBase64Tokens(tokensPath);

        foreach (var line in File.ReadLines(tokensPath))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var trimmedLine = line.TrimEnd();
            var separatorIndex = trimmedLine.LastIndexOfAny(['\t', ' ']);
            if (separatorIndex <= 0 ||
                !int.TryParse(trimmedLine[(separatorIndex + 1)..].Trim(), out var tokenId))
            {
                continue;
            }

            var tokenText = trimmedLine[..separatorIndex];
            var token = decodeBase64Tokens ? DecodeBase64TokenText(tokenText) : tokenText;
            idToToken[tokenId] = token;
            if (string.Equals(token, "<s>", StringComparison.Ordinal))
            {
                sos = tokenId;
            }
            else if (string.Equals(token, "</s>", StringComparison.Ordinal))
            {
                eos = tokenId;
            }
        }

        if (sos is null || eos is null)
        {
            throw new InvalidOperationException("Moonshine tokens.txt is missing the <s> or </s> token required for greedy decoding.");
        }

        return new MoonshineTokenizer(idToToken, sos.Value, eos.Value);
    }

    private static bool ShouldDecodeBase64Tokens(string tokensPath)
    {
        foreach (var line in File.ReadLines(tokensPath))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var trimmedLine = line.TrimEnd();
            var separatorIndex = trimmedLine.LastIndexOfAny(['\t', ' ']);
            if (separatorIndex <= 0)
            {
                continue;
            }

            var token = DecodeBase64TokenText(trimmedLine[..separatorIndex]);
            return string.Equals(token, "<unk>", StringComparison.Ordinal) ||
                string.Equals(token, "<s>", StringComparison.Ordinal) ||
                string.Equals(token, "</s>", StringComparison.Ordinal);
        }

        return false;
    }

    private static string DecodeBase64TokenText(string token)
    {
        try
        {
            var bytes = Convert.FromBase64String(token);
            var decoded = Encoding.UTF8.GetString(bytes);
            return string.IsNullOrEmpty(decoded) ? token : decoded;
        }
        catch (FormatException)
        {
            return token;
        }
        catch (ArgumentException)
        {
            return token;
        }
    }

    public string Decode(IEnumerable<int> tokenIds)
    {
        var pieces = new List<string>();
        foreach (var tokenId in tokenIds)
        {
            if (this.idToToken.TryGetValue(tokenId, out var token))
            {
                if (string.Equals(token, "<s>", StringComparison.Ordinal) ||
                    string.Equals(token, "</s>", StringComparison.Ordinal))
                {
                    continue;
                }

                pieces.Add(token);
            }
        }

        return string.Concat(pieces).Replace("▁", " ", StringComparison.Ordinal).Trim();
    }
}
