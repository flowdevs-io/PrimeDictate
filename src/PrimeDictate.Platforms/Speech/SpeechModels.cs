using PrimeDictate.Core.Dictation;

namespace PrimeDictate.Platforms.Speech;

/// <summary>An installed speech model of any family, as dictation sees it.</summary>
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
    bool Recommended = false)
{
    public string SubFolder => SpeechModelLocator.SubFolder(this.Backend);

    public string ArchiveFileName => $"{this.InstallDirectoryName}.tar.bz2";

    public Uri DownloadUri => new($"https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/{this.ArchiveFileName}");

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
        new(LegacyBackend.Moonshine, "moonshine-tiny-v2-en", "Moonshine Tiny v2 (English)", "Very small and fast English model.", 83_886_080, "sherpa-onnx-moonshine-tiny-en-quantized-2026-02-27", true),
        new(LegacyBackend.Moonshine, "moonshine-base-en", "Moonshine Base (English) v1", "The original Moonshine model.", 250_807_309, "sherpa-onnx-moonshine-base-en-int8")
    ];

    public static string FormatSize(long bytes) => $"{bytes / (1024d * 1024d):N0} MB";

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
        _ => "whisper"
    };

    public static bool IsValid(ModelDownloadOption option, string directory) => option.Backend switch
    {
        LegacyBackend.Parakeet => IsParakeet(directory),
        LegacyBackend.Moonshine => ResolveMoonshine(directory) is not null,
        _ => WhisperOnnxModelLocator.TryResolve(directory, out _)
    };

    public static bool IsParakeet(string directory) =>
        new[] { "encoder.int8.onnx", "decoder.int8.onnx", "joiner.int8.onnx", "tokens.txt" }.All(f => File.Exists(Path.Combine(directory, f)));

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
            var dir = Path.Combine(modelsRoot, option.SubFolder, option.InstallDirectoryName);
            if (IsValid(option, dir))
            {
                found.Add(new InstalledSpeechModel(option.Backend, option.Id, option.DisplayName, Path.GetFullPath(dir), option.Id is "parakeet-tdt-0.6b-v2" || option.Id.EndsWith("-en", StringComparison.Ordinal)));
            }
        }

        return found;
    }

    public static string InstallPath(string modelsRoot, ModelDownloadOption option) =>
        Path.Combine(modelsRoot, option.SubFolder, option.InstallDirectoryName);

    private static string? Find(string directory, params string[] names) =>
        names.Select(n => Path.Combine(directory, n)).FirstOrDefault(File.Exists);
}
