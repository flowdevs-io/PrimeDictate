namespace PrimeDictate.Core.Diagnostics;

/// <summary>
/// A small, rotated log of faults the app itself hit (an exception ended audio capture, for example), so a stall can be
/// explained afterwards without asking anyone to copy a status line. Only the exception type, a shortened message and a
/// shortened stack trace are written; recognized text is never passed in. Written to
/// <c>%LocalAppData%\PrimeDictate\logs\app.log</c>, two files of at most 1 MB.
/// </summary>
public static class AppLog
{
    private const long MaxBytes = 1_000_000;
    private const int MaxMessage = 300;
    private const int MaxStack = 2_500;
    private static readonly object Gate = new();

    /// <summary>Where the log goes. Tests point it at a temporary folder.</summary>
    public static string Directory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PrimeDictate", "logs");

    /// <summary>The in-memory feed behind the Dictation activity view; every call below also lands here.</summary>
    public static ActivityFeed Feed { get; } = new();

    public static string FilePath => Path.Combine(Directory, "app.log");

    /// <summary>Records an exception with where it happened.</summary>
    public static void Fault(string source, Exception exception, Guid? sessionId = null)
    {
        Feed.Add(ActivityLevel.Error, source, $"{exception.GetType().FullName}: {Cut(exception.Message, MaxMessage)}", sessionId);
        Write($"[{source}] {exception.GetType().FullName}: {Cut(exception.Message, MaxMessage)}{Environment.NewLine}{Cut(exception.StackTrace ?? string.Empty, MaxStack)}");
    }

    /// <summary>Records something the app observed, such as a timeout. Never pass recognized text.</summary>
    public static void Event(string source, string message, ActivityLevel level = ActivityLevel.Info, Guid? sessionId = null)
    {
        Feed.Add(level, source, Cut(message, MaxMessage), sessionId);
        Write($"[{source}] {Cut(message, MaxMessage)}");
    }

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..max] + "…";

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
            // Diagnostics must never break recording.
        }
    }
}
