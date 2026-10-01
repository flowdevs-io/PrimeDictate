using System.Diagnostics.CodeAnalysis;

namespace PrimeDictate.Platforms.Speech.Qualcomm;

/// <summary>Where the Qualcomm AI Hub Whisper package comes from and where it installs. Same id, folder and URLs as the WPF catalog, so an installed package is shared.</summary>
public sealed record QualcommAihubWhisperOption(
    string Id,
    string DisplayName,
    string InstallDirectoryName,
    string RunnableArchiveFileName,
    string RawContextArchiveFileName,
    string Description,
    long ApproximateBytes,
    bool Recommended = false)
{
    public const string ReleaseBaseUri =
        "https://qaihub-public-assets.s3.us-west-2.amazonaws.com/qai-hub-models/models/whisper_small/releases/v0.52.0";

    public Uri DownloadUri => new($"{ReleaseBaseUri}/{this.RunnableArchiveFileName}");

    public Uri RawContextDownloadUri => new($"{ReleaseBaseUri}/{this.RawContextArchiveFileName}");
}

/// <summary>The files a runnable AI Hub package must hold: ONNX Runtime EPContext wrappers plus the QNN context binaries they point at.</summary>
public readonly record struct QualcommAihubWhisperArtifacts(
    string ModelDirectory,
    string EncoderOnnxPath,
    string DecoderOnnxPath,
    string EncoderContextPath,
    string DecoderContextPath,
    string TokenizerPath,
    string MetadataPath);

/// <summary>
/// The Qualcomm AI Hub Whisper Small package (<c>precompiled_qnn_onnx</c>, Snapdragon X Elite / X Plus) under
/// <c>{models}/qualcomm-aihub-whisper/{folder}/</c>. File-name rules live here and nowhere else.
/// </summary>
public static class QualcommAihubWhisperCatalog
{
    public const string TokenizerFileName = "multilingual.tiktoken";

    /// <summary>The managed-models subfolder, as in the WPF app.</summary>
    public const string SubFolder = "qualcomm-aihub-whisper";

    public const string TokenizerUri =
        "https://raw.githubusercontent.com/openai/whisper/839639a223b92ad61851baae9ad8a695ccb41ce5/whisper/assets/multilingual.tiktoken";

    public static IReadOnlyList<QualcommAihubWhisperOption> Options { get; } =
    [
        new(
            Id: "qaihub-whisper-small-snapdragon-x-elite",
            DisplayName: "Qualcomm AI Hub Whisper Small (Snapdragon X Elite)",
            InstallDirectoryName: "whisper_small-precompiled_qnn_onnx-float-qualcomm_snapdragon_x_elite",
            RunnableArchiveFileName: "whisper_small-precompiled_qnn_onnx-float-qualcomm_snapdragon_x_elite.zip",
            RawContextArchiveFileName: "whisper_small-qnn_context_binary-float-qualcomm_snapdragon_x_elite.zip",
            Description: "Qualcomm AI Hub Whisper Small compiled for Snapdragon X Elite / X Plus NPU through ONNX Runtime QNN. PrimeDictate installs the ONNX wrapper package because the raw context-binary ZIP is not directly runnable by ONNX Runtime.",
            ApproximateBytes: 520_029_222,
            Recommended: true)
    ];

    public static bool TryGetById(string? id, [NotNullWhen(true)] out QualcommAihubWhisperOption? option)
    {
        option = Options.FirstOrDefault(candidate => string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase));
        return option is not null;
    }

    public static string InstallPath(string modelsRoot, QualcommAihubWhisperOption option) =>
        Path.Combine(modelsRoot, SubFolder, option.InstallDirectoryName);

    /// <summary>The catalog option whose folder this path is (the folder name decides), or null for a custom folder.</summary>
    public static QualcommAihubWhisperOption? TryGetByPath(string? modelPath)
    {
        if (!TryResolveDirectory(modelPath, out var resolved))
        {
            return null;
        }

        var directoryName = Path.GetFileName(resolved.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return Options.FirstOrDefault(candidate => string.Equals(candidate.InstallDirectoryName, directoryName, StringComparison.OrdinalIgnoreCase));
    }

    public static bool TryResolveDirectory(string? path, [NotNullWhen(true)] out string? resolvedPath)
    {
        if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
        {
            var candidate = Path.GetFullPath(path);
            if (IsValidModelDirectory(candidate))
            {
                resolvedPath = candidate;
                return true;
            }
        }

        resolvedPath = null;
        return false;
    }

    public static bool TryResolveInstalledPath(string modelsRoot, QualcommAihubWhisperOption option, [NotNullWhen(true)] out string? installedPath)
    {
        var candidate = InstallPath(modelsRoot, option);
        if (IsValidModelDirectory(candidate))
        {
            installedPath = Path.GetFullPath(candidate);
            return true;
        }

        installedPath = null;
        return false;
    }

    public static bool TryResolveArtifacts(string? directoryPath, [NotNullWhen(true)] out QualcommAihubWhisperArtifacts? artifacts)
    {
        artifacts = null;
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
        {
            return false;
        }

        var directory = Path.GetFullPath(directoryPath);
        var found = new QualcommAihubWhisperArtifacts(
            directory,
            Path.Combine(directory, "encoder.onnx"),
            Path.Combine(directory, "decoder.onnx"),
            Path.Combine(directory, "encoder_qairt_context.bin"),
            Path.Combine(directory, "decoder_qairt_context.bin"),
            Path.Combine(directory, TokenizerFileName),
            Path.Combine(directory, "metadata.json"));

        if (File.Exists(found.EncoderOnnxPath) &&
            File.Exists(found.DecoderOnnxPath) &&
            File.Exists(found.EncoderContextPath) &&
            File.Exists(found.DecoderContextPath) &&
            File.Exists(found.TokenizerPath) &&
            File.Exists(found.MetadataPath))
        {
            artifacts = found;
            return true;
        }

        return false;
    }

    public static bool IsValidModelDirectory(string? directoryPath) => TryResolveArtifacts(directoryPath, out _);

    /// <summary>The raw <c>qnn_context_binary</c> package (encoder.bin and decoder.bin only). ONNX Runtime cannot run it without the ONNX wrappers.</summary>
    public static bool IsRawContextOnlyDirectory(string? directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
        {
            return false;
        }

        return File.Exists(Path.Combine(directoryPath, "encoder.bin")) &&
            File.Exists(Path.Combine(directoryPath, "decoder.bin")) &&
            File.Exists(Path.Combine(directoryPath, "metadata.json")) &&
            !File.Exists(Path.Combine(directoryPath, "encoder.onnx")) &&
            !File.Exists(Path.Combine(directoryPath, "decoder.onnx"));
    }

    public static IReadOnlyList<string> RequiredFiles { get; } =
    [
        "encoder.onnx",
        "decoder.onnx",
        "encoder_qairt_context.bin",
        "decoder_qairt_context.bin",
        TokenizerFileName,
        "metadata.json"
    ];

    /// <summary>
    /// The folder inside an unpacked archive that holds the runnable package. The archive does not contain the tokenizer (it is
    /// downloaded separately), so this checks for the files the archive does carry.
    /// </summary>
    public static string? FindRunnablePackageDirectory(string rootPath)
    {
        if (HasRunnablePackageFiles(rootPath))
        {
            return rootPath;
        }

        return Directory.EnumerateDirectories(rootPath, "*", SearchOption.AllDirectories).FirstOrDefault(HasRunnablePackageFiles);
    }

    private static bool HasRunnablePackageFiles(string directoryPath) =>
        File.Exists(Path.Combine(directoryPath, "encoder.onnx")) &&
        File.Exists(Path.Combine(directoryPath, "decoder.onnx")) &&
        File.Exists(Path.Combine(directoryPath, "encoder_qairt_context.bin")) &&
        File.Exists(Path.Combine(directoryPath, "decoder_qairt_context.bin")) &&
        File.Exists(Path.Combine(directoryPath, "metadata.json"));
}
