namespace PrimeDictate.Core.Storage;

/// <summary>
/// Per-user application data locations. On Windows this is the same
/// <c>%LocalAppData%\PrimeDictate</c> root existing installs use for settings, history, and
/// models. .NET maps LocalApplicationData to <c>~/Library/Application Support</c> on macOS and
/// <c>$XDG_DATA_HOME</c> (default <c>~/.local/share</c>) on Linux.
/// </summary>
public sealed class AppDataPaths
{
    public AppDataPaths(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        this.Root = Path.GetFullPath(root);
    }

    public static AppDataPaths Default { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify),
        "PrimeDictate"));

    public string Root { get; }

    public string ModelsDirectory => Path.Combine(this.Root, "models");

    public string TranscriptionDirectory => Path.Combine(this.Root, "transcription");

    public string SessionDatabasePath => Path.Combine(this.TranscriptionDirectory, "sessions.db");

    public string SessionMediaDirectory => Path.Combine(this.TranscriptionDirectory, "media");

    public string SessionTempDirectory => Path.Combine(this.TranscriptionDirectory, "tmp");

    public string TranscriptionPreferencesPath => Path.Combine(this.Root, "transcription-settings.json");

    /// <summary>Creates a directory readable only by the current user where the OS supports it.</summary>
    public static void EnsurePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            // LocalAppData is already per-user; inherit its ACL.
            Directory.CreateDirectory(path);
            return;
        }

        Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public static void RestrictFile(string path)
    {
        if (!OperatingSystem.IsWindows() && File.Exists(path))
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
