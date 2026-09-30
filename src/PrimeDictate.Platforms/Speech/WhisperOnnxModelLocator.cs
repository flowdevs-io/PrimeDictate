namespace PrimeDictate.Platforms.Speech;

/// <summary>An installed sherpa-onnx Whisper model folder.</summary>
public sealed record InstalledWhisperModel(string Id, string DisplayName, string Directory, string Encoder, string Decoder, string Tokens, bool IsEnglishOnly, string? EncoderFullPrecision = null, string? DecoderFullPrecision = null)
{
    /// <summary>The unquantized pair when the folder has it. CUDA has no kernels for most int8 operators, so on the GPU the full-precision files are used.</summary>
    public bool HasFullPrecision => this.EncoderFullPrecision is not null && this.DecoderFullPrecision is not null;

    /// <summary>Stable model id used in transcripts, for example <c>whisper-onnx:base.en</c>.</summary>
    public string ModelId => $"whisper-onnx:{this.Id}";
}

/// <summary>
/// Finds Whisper ONNX models in the layout the dictation app already uses:
/// <c>{models}/whisper/sherpa-onnx-whisper-{id}/</c> holding <c>*-encoder[.int8].onnx</c>,
/// <c>*-decoder[.int8].onnx</c> and <c>*-tokens.txt</c>. Mirrors the WPF catalog's file rules so both
/// modes see the same installed models.
/// </summary>
public static class WhisperOnnxModelLocator
{
    private const string FolderPrefix = "sherpa-onnx-whisper-";

    public static string DefaultModelsRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PrimeDictate",
        "models");

    public static IReadOnlyList<InstalledWhisperModel> Discover(string? modelsRoot = null, IEnumerable<string>? extraRoots = null)
    {
        var roots = new List<string> { Path.Combine(modelsRoot ?? DefaultModelsRoot, "whisper") };
        if (extraRoots is not null)
        {
            roots.AddRange(extraRoots);
        }

        var found = new Dictionary<string, InstalledWhisperModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var dir in Directory.EnumerateDirectories(root, FolderPrefix + "*").Order(StringComparer.OrdinalIgnoreCase))
            {
                if (TryResolve(dir, out var model))
                {
                    found.TryAdd(model.Id, model);
                }
            }
        }

        return [.. found.Values.OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase)];
    }

    public static bool TryResolve(string directory, out InstalledWhisperModel model)
    {
        model = null!;
        if (!Directory.Exists(directory))
        {
            return false;
        }

        var encoderFull = Single(directory, "*-encoder.onnx");
        var decoderFull = Single(directory, "*-decoder.onnx");
        var encoder = Single(directory, "*-encoder.int8.onnx") ?? encoderFull;
        var decoder = Single(directory, "*-decoder.int8.onnx") ?? decoderFull;
        var tokens = Single(directory, "*-tokens.txt");
        if (encoder is null || decoder is null || tokens is null)
        {
            return false;
        }

        var name = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var id = name.StartsWith(FolderPrefix, StringComparison.OrdinalIgnoreCase) ? name[FolderPrefix.Length..] : name;
        model = new InstalledWhisperModel(id, $"Whisper {id} (ONNX)", Path.GetFullPath(directory), encoder, decoder, tokens, id.EndsWith(".en", StringComparison.OrdinalIgnoreCase), encoderFull, decoderFull);
        return true;
    }

    private static string? Single(string directory, string pattern)
    {
        var matches = Directory.GetFiles(directory, pattern);
        return matches.Length == 1 ? matches[0] : null;
    }
}
