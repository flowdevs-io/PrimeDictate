namespace PrimeDictate.Platforms.Nemotron;

/// <summary>
/// A marker file in a session's media folder meaning "this meeting still needs its after-Stop pass". It is written when a two-pass
/// recording starts and removed when the pass ends, however it ends (done, failed, canceled, or skipped with a note). If the app
/// exits before the pass ran, the marker survives and the next launch finishes the transcript.
/// </summary>
public static class FinalPassPending
{
    public const string FileName = "final-pass-pending";

    public static void Mark(string mediaDirectory)
    {
        Directory.CreateDirectory(mediaDirectory);
        File.WriteAllText(Path.Combine(mediaDirectory, FileName), DateTimeOffset.UtcNow.ToString("O"));
    }

    public static void Clear(string mediaDirectory)
    {
        var path = Path.Combine(mediaDirectory, FileName);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public static bool IsPending(string mediaDirectory) => File.Exists(Path.Combine(mediaDirectory, FileName));
}
