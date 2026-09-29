namespace PrimeDictate.Platforms.Nemotron;

/// <summary>A GGUF the app has pinned: only these exact files are ever handed to the worker.</summary>
public sealed record PinnedNemotronModel(string Id, string FileName, long Bytes, string Sha256Prefix, bool IsDiarizer);

public static class NemotronPins
{
    /// <summary>Runtime commit the app was validated against.</summary>
    public const string RuntimeCommit = "0f706e43cf1fbc031bad1423e05460d3acaeaa1c";

    public static readonly PinnedNemotronModel Multilingual = new("nemotron-3.5-asr-streaming-0.6b", "nemotron-3.5-asr-streaming-0.6b.q8_0.gguf", 741_548_352, "a5c435f2", false);

    public static readonly PinnedNemotronModel EnglishOnly = new("nemotron-speech-streaming-en-0.6b", "nemotron-speech-streaming-en-0.6b.q8_0.gguf", 699_872_960, "d9a01898", false);

    public static readonly PinnedNemotronModel Diarizer = new("nemotron-3-diarization", "Nemotron-3-Diarization.q8_0.gguf", 107_012_128, "08456d9e", true);

    public static IReadOnlyList<PinnedNemotronModel> All { get; } = [Multilingual, EnglishOnly, Diarizer];
}

/// <summary>Verified local model paths for one worker process. The worker never sees a model name, so it can never start a download.</summary>
public sealed record NemotronModelFiles(string AsrPath, string? DiarizerPath)
{
    public static NemotronModelFiles Verify(string asrPath, string? diarizerPath)
    {
        CheckFile(asrPath, NemotronPins.Multilingual, NemotronPins.EnglishOnly);
        if (diarizerPath is not null)
        {
            CheckFile(diarizerPath, NemotronPins.Diarizer);
        }

        return new NemotronModelFiles(Path.GetFullPath(asrPath), diarizerPath is null ? null : Path.GetFullPath(diarizerPath));
    }

    /// <summary>Finds pinned files in a folder; null when the speech model is missing. The diarizer is optional.</summary>
    public static NemotronModelFiles? TryFind(string directory, PinnedNemotronModel asr)
    {
        var asrPath = Path.Combine(directory, asr.FileName);
        var diarPath = Path.Combine(directory, NemotronPins.Diarizer.FileName);
        try
        {
            return Verify(asrPath, File.Exists(diarPath) ? diarPath : null);
        }
        catch (NemotronException)
        {
            return null;
        }
    }

    private static void CheckFile(string path, params PinnedNemotronModel[] allowed)
    {
        if (!File.Exists(path))
        {
            throw new NemotronException("model-missing", $"Model file not found: {Path.GetFileName(path)}");
        }

        var pin = allowed.FirstOrDefault(p => string.Equals(p.FileName, Path.GetFileName(path), StringComparison.Ordinal))
            ?? throw new NemotronException("model-unrecognized", $"{Path.GetFileName(path)} is not a model this version of PrimeDictate has verified.");
        var length = new FileInfo(path).Length;
        if (length != pin.Bytes)
        {
            throw new NemotronException("model-corrupt", $"{pin.FileName} is {length:N0} bytes; expected {pin.Bytes:N0}. Download it again.");
        }
    }
}

public sealed class NemotronException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>Stable code for the UI: model-missing, model-unrecognized, model-corrupt, worker-missing, worker-exited, worker-timeout, worker-http.</summary>
    public string Code { get; } = code;
}
