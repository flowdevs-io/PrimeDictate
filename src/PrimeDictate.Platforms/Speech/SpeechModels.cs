using PrimeDictate.Core.Dictation;

namespace PrimeDictate.Platforms.Speech;

/// <summary>An installed speech model of any family, as dictation sees it.</summary>
/// <remarks><see cref="Directory"/> is the model folder, except for Whisper.net where it is the path of the ggml <c>.bin</c> file.</remarks>
public sealed record InstalledSpeechModel(LegacyBackend Backend, string Id, string DisplayName, string Directory, bool IsEnglishOnly)
{
    /// <summary>Stable id such as <c>whisper-onnx:base.en</c> or <c>parakeet-onnx:parakeet-tdt-0.6b-v3</c>.</summary>
    public string ModelId => $"{SpeechModelLocator.Prefix(this.Backend)}:{this.Id}";
}

public sealed record MoonshineFiles(string Tokens, string Encoder, string? MergedDecoder, string? Preprocessor, string? UncachedDecoder, string? CachedDecoder);

/// <summary>A model that can be downloaded: same ids, folders and GitHub archives as the WPF catalogs, so both apps share installs.</summary>
public sealed record ModelDownloadOption(
    LegacyBackend Backend,
    string Id,
    string DisplayName,
    string Description,
    long ApproximateBytes,
    string InstallDirectoryName,
    bool Recommended = false,
    string? FileName = null)
{
    /// <summary>True for a model that is one file (Whisper.net ggml <c>.bin</c>) rather than an archive that unpacks to a folder.</summary>
    public bool IsSingleFile => this.FileName is not null;

    public string SubFolder => SpeechModelLocator.SubFolder(this.Backend);

    public string ArchiveFileName => $"{this.InstallDirectoryName}.tar.bz2";

    public Uri DownloadUri => this.FileName is { } file
        ? new($"https://huggingface.co/ggerganov/whisper.cpp/resolve/main/{file}")
        : new($"https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/{this.ArchiveFileName}");

    public string ModelId => $"{SpeechModelLocator.Prefix(this.Backend)}:{this.Id}";
}

public static class SpeechModelCatalog
{
    public static IReadOnlyList<ModelDownloadOption> Options { get; } =
    [
        Whisper("tiny.en", "Tiny English", "The lightest English model. Fast, good for slower laptops and idle wake listening.", 118_071_777, true),
        Whisper("base.en", "Base English", "A good English balance for everyday dictation on CPU.", 208_576_005, true),
        Whisper("distil-small.en", "Distil Small English", "A faster distilled English model for longer sessions.", 453_710_017),
        Whisper("small.en", "Small English", "Higher English accuracy, larger download, more compute.", 635_693_775),
        Whisper("tiny", "Tiny Multilingual", "The lightest model for non-English dictation.", 116_204_861),
        Whisper("base", "Base Multilingual", "A balanced multilingual model.", 207_557_382),
        Whisper("small", "Small Multilingual", "Higher multilingual accuracy, more compute.", 639_387_718),
        new(LegacyBackend.Parakeet, "parakeet-tdt-0.6b-v3", "Parakeet TDT 0.6B v3", "NVIDIA Parakeet, fast and accurate local transcription; multilingual.", 710L * 1024 * 1024, "sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8", true),
        new(LegacyBackend.Parakeet, "parakeet-tdt-0.6b-v2", "Parakeet TDT 0.6B v2", "The earlier English Parakeet release.", 690L * 1024 * 1024, "sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8"),
        new(LegacyBackend.Parakeet, "parakeet-tdt-0.6b-v2-fp16", "Parakeet TDT 0.6B v2 (fp16, for the GPU)", "English Parakeet in half precision. Made for CUDA; large and slower on the CPU.", 1_120_982_957, "sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-fp16"),
        new(LegacyBackend.Moonshine, "moonshine-tiny-v2-en", "Moonshine Tiny v2 (English)", "Very small and fast English model.", 83_886_080, "sherpa-onnx-moonshine-tiny-en-quantized-2026-02-27", true),
        new(LegacyBackend.Moonshine, "moonshine-base-en", "Moonshine Base (English) v1", "The original Moonshine model.", 250_807_309, "sherpa-onnx-moonshine-base-en-int8"),
        GgmlWhisper("large-v3-turbo", "Large V3 Turbo (GGML, Whisper.net)", "The fastest Whisper V3 option. Runs on the GPU (CUDA or Vulkan) when available.", 1_618_426_976),
        GgmlWhisper("large-v3", "Large V3 (GGML, Whisper.net)", "The highest-accuracy Whisper V3 model. Runs on the GPU when available.", 4_277_163_902, recommended: true),
        GgmlWhisper("base.en", "Base English (GGML, Whisper.net)", "Standard English GGML model.", 147_964_352),
        GgmlWhisper("tiny.en", "Tiny English (GGML, Whisper.net)", "Very small English GGML model.", 77_720_256)
    ];

    public static string FormatSize(long bytes) => bytes >= 1024L * 1024 * 1024 ? $"{bytes / (1024d * 1024d * 1024d):N1} GB" : $"{bytes / (1024d * 1024d):N0} MB";

    /// <summary>
    /// A Whisper.net ggml model: one <c>ggml-{id}.bin</c> under <c>models/whisper.net/</c>, downloaded from the whisper.cpp repository on Hugging Face.
    /// Same ids, file names and sizes as the WPF catalog, so both apps share the installed file.
    /// </summary>
    private static ModelDownloadOption GgmlWhisper(string id, string name, string description, long bytes, bool recommended = false) =>
        new(LegacyBackend.WhisperNet, id, name, description, bytes, $"ggml-{id}.bin", recommended, FileName: $"ggml-{id}.bin");

    private static ModelDownloadOption Whisper(string id, string name, string description, long bytes, bool recommended = false) =>
        new(LegacyBackend.Whisper, id, name, description, bytes, $"sherpa-onnx-whisper-{id}", recommended);
}

/// <summary>Finds and validates installed models under <c>{models}/{whisper|parakeet|moonshine}/</c>. File-name rules live here and nowhere else.</summary>
public static class SpeechModelLocator
{
    public static string Prefix(LegacyBackend backend) => backend.ModelIdPrefix();

    public static string SubFolder(LegacyBackend backend) => backend switch
    {
        LegacyBackend.Parakeet => "parakeet",
        LegacyBackend.Moonshine => "moonshine",
        LegacyBackend.WhisperNet => "whisper.net",
        _ => "whisper"
    };

    /// <summary>
    /// A ggml file is valid when it exists and is at least half the catalog size. The sizes are approximate (the published file
    /// can differ by a few MB), so this only rejects an empty or cut-off file, not a slightly different build.
    /// </summary>
    public static bool IsValidGgml(ModelDownloadOption option, string path)
    {
        var info = new FileInfo(path);
        return info.Exists && info.Length > 0 && info.Length >= option.ApproximateBytes / 2;
    }

    /// <summary>For a directory-based model, the folder; for a single-file model, the file path.</summary>
    public static bool IsValid(ModelDownloadOption option, string directory) => option.Backend switch
    {
        LegacyBackend.WhisperNet => IsValidGgml(option, directory),
        LegacyBackend.Parakeet => IsParakeet(directory),
        LegacyBackend.Moonshine => ResolveMoonshine(directory) is not null,
        _ => WhisperOnnxModelLocator.TryResolve(directory, out _)
    };

    public static bool IsParakeet(string directory) => ParakeetPrecision(directory, preferHalf: false) is not null;

    /// <summary>
    /// <c>int8</c> or <c>fp16</c>, whichever complete file set the folder has. int8 is preferred on the CPU and fp16 on CUDA
    /// (which has no int8 kernels for most operators); the other set is used when it is the only one. Null when neither is complete.
    /// </summary>
    public static string? ParakeetPrecision(string directory, bool preferHalf)
    {
        static bool Complete(string dir, string precision) =>
            new[] { $"encoder.{precision}.onnx", $"decoder.{precision}.onnx", $"joiner.{precision}.onnx", "tokens.txt" }.All(f => File.Exists(Path.Combine(dir, f)));
        var order = preferHalf ? new[] { "fp16", "int8" } : ["int8", "fp16"];
        return order.FirstOrDefault(p => Complete(directory, p));
    }

    /// <summary>Accepts the v2 layout (encoder + merged decoder) first, then the v1 four-stage layout, as the WPF catalog does.</summary>
    public static MoonshineFiles? ResolveMoonshine(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return null;
        }

        var tokens = Find(directory, "tokens.txt", "tokenizer.json", "vocab.txt");
        var v2Encoder = Find(directory, "encoder_model.ort", "encoder_model_int8.ort", "encoder_model.onnx", "encoder_model_int8.onnx", "encoder.int8.onnx", "encoder.onnx", "encode.int8.onnx", "encode.onnx");
        var v2Decoder = Find(directory, "decoder_model_merged.ort", "decoder_model_merged_int8.ort", "decoder_model_merged.onnx", "decoder_model_merged_int8.onnx", "decoder.int8.onnx", "decoder.onnx", "merged_decoder.onnx", "decode.int8.onnx", "decode.onnx");
        if (tokens is not null && v2Encoder is not null && v2Decoder is not null)
        {
            return new MoonshineFiles(tokens, v2Encoder, v2Decoder, null, null, null);
        }

        var pre = Find(directory, "preprocess.onnx", "preprocess.qdq.onnx");
        var enc = Find(directory, "encode.int8.onnx", "encode.onnx", "encoder.int8.onnx");
        var unc = Find(directory, "uncached_decode.int8.onnx", "uncached_decode.onnx");
        var cac = Find(directory, "cached_decode.int8.onnx", "cached_decode.onnx");
        return tokens is not null && pre is not null && enc is not null && unc is not null && cac is not null
            ? new MoonshineFiles(tokens, enc, null, pre, unc, cac)
            : null;
    }

    /// <summary>Every installed model of every family that is in the download catalog, plus any Whisper folder that validates.</summary>
    public static IReadOnlyList<InstalledSpeechModel> Discover(string modelsRoot)
    {
        var found = new List<InstalledSpeechModel>();
        foreach (var w in WhisperOnnxModelLocator.Discover(modelsRoot))
        {
            found.Add(new InstalledSpeechModel(LegacyBackend.Whisper, w.Id, w.DisplayName, w.Directory, w.IsEnglishOnly));
        }

        foreach (var option in SpeechModelCatalog.Options.Where(o => o.Backend != LegacyBackend.Whisper))
        {
            var dir = InstallPath(modelsRoot, option);
            if (IsValid(option, dir))
            {
                var english = option.Backend == LegacyBackend.WhisperNet
                    ? option.Id.EndsWith(".en", StringComparison.Ordinal)
                    : option.Id is "parakeet-tdt-0.6b-v2" or "parakeet-tdt-0.6b-v2-fp16" || option.Id.EndsWith("-en", StringComparison.Ordinal);
                found.Add(new InstalledSpeechModel(option.Backend, option.Id, option.DisplayName, Path.GetFullPath(dir), english));
            }
        }

        return found;
    }

    /// <summary>Where the model lives: its folder, or for a Whisper.net model the <c>.bin</c> file itself.</summary>
    public static string InstallPath(string modelsRoot, ModelDownloadOption option) =>
        Path.Combine(modelsRoot, option.SubFolder, option.InstallDirectoryName);

    /// <summary>The catalog option for a Whisper.net model id such as <c>large-v3-turbo</c>, or null.</summary>
    public static ModelDownloadOption? FindWhisperNet(string? id) =>
        SpeechModelCatalog.Options.FirstOrDefault(o => o.Backend == LegacyBackend.WhisperNet && string.Equals(o.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>OpenVINO encoder files that sit next to a ggml model (the WPF app downloads these for the Intel NPU). Null when either is missing.</summary>
    public static string? WhisperNetOpenVinoEncoder(string modelPath)
    {
        var directory = Path.GetDirectoryName(modelPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        var stem = Path.GetFileNameWithoutExtension(modelPath);
        var xml = Path.Combine(directory, $"{stem}-encoder-openvino.xml");
        return File.Exists(xml) && File.Exists(Path.Combine(directory, $"{stem}-encoder-openvino.bin")) ? xml : null;
    }

    private static string? Find(string directory, params string[] names) =>
        names.Select(n => Path.Combine(directory, n)).FirstOrDefault(File.Exists);
}
