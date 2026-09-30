using System.Text.RegularExpressions;

namespace PrimeDictate.Platforms.Nemotron;

/// <summary>
/// A small, rotated log of what the speech worker and the diarizer say about themselves (device, model load, errors),
/// so a stall or crash can be explained afterwards. Transcripts stay out of it: only stderr lines that start with one of
/// the worker's own bracketed prefixes are kept, lines that look like they carry recognized text are dropped, and every
/// line is cut short. Written to <c>%LocalAppData%\PrimeDictate\logs\nemo-worker.log</c>; two files of at most 1 MB.
/// </summary>
public static class NemotronLog
{
    private const long MaxBytes = 1_000_000;
    private const int MaxLine = 240;
    private static readonly object Gate = new();

    private static readonly Regex Prefixed = new(@"^\[[a-z][a-z0-9_.-]{0,24}\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Words that mark a line as possibly containing what was said, in the JSON the worker speaks or in plain logs.</summary>
    private static readonly Regex LooksLikeSpeech = new("transcript|\"text\"|\"delta\"|\"words\"|text=|delta=|partial|hypothesis|utterance", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Where the log goes. Tests point it at a temporary folder.</summary>
    public static string Directory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PrimeDictate", "logs");

    public static string FilePath => Path.Combine(Directory, "nemo-worker.log");

    /// <returns>The line to store, or null when it must not be written.</returns>
    public static string? Filter(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        var trimmed = line.Trim();
        if (!Prefixed.IsMatch(trimmed) || LooksLikeSpeech.IsMatch(trimmed))
        {
            return null;
        }

        return trimmed.Length <= MaxLine ? trimmed : trimmed[..MaxLine] + "…";
    }

    /// <summary>Records a line the worker printed, if it passes <see cref="Filter"/>.</summary>
    public static void WorkerLine(string source, string? line)
    {
        if (Filter(line) is { } kept)
        {
            Write($"{source} {kept}");
        }
    }

    /// <summary>Records something the app itself observed, such as a process starting or exiting. Never contains transcript text.</summary>
    public static void Event(string source, string message) => Write($"{source} [app] {message}");

    private static void Write(string text)
    {
        try
        {
            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(Directory);
                var path = FilePath;
                if (File.Exists(path) && new FileInfo(path).Length >= MaxBytes)
                {
                    File.Move(path, path + ".1", overwrite: true);
                }

                File.AppendAllText(path, $"{DateTimeOffset.UtcNow:yyyy-MM-dd'T'HH:mm:ss.fff'Z'} {text}{Environment.NewLine}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Diagnostics must never break dictation.
        }
    }
}
