using PrimeDictate.Core.Settings;
using PrimeDictate.Core.Storage;

namespace PrimeDictate.Desktop;

/// <summary>
/// The transcription and meeting options (live text, speaker labels, quiet-audio boost, last source, default model), kept in
/// <c>transcription-settings.json</c> and shared by the main window and the Settings window so a change in one shows in the other.
/// Used on the UI thread only.
/// </summary>
public sealed class TranscriptionPrefsService
{
    private readonly TranscriptionPreferencesStore store;
    private bool readOnly;

    public TranscriptionPrefsService(AppDataPaths paths)
    {
        this.store = new TranscriptionPreferencesStore(paths.TranscriptionPreferencesPath);
        var legacy = Path.Combine(paths.Root, "meeting-live-text.txt");
        try
        {
            var load = this.store.Load(() => new TranscriptionPreferences { LiveTextMode = ReadLegacyLiveText(legacy) });
            this.Current = load.Preferences;
            this.readOnly = load.IsReadOnly;
            this.Warning = load.Warning;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            this.Current = new TranscriptionPreferences();
            this.readOnly = true;
            this.Warning = "Transcription settings could not be read: " + ex.Message;
        }
    }

    public TranscriptionPreferences Current { get; private set; }

    /// <summary>Set when the file could not be read or came from a newer version; nothing is written then.</summary>
    public string? Warning { get; }

    /// <summary>Raised after a change with the source that made it, so that source can skip refreshing itself.</summary>
    public event Action<object?>? Changed;

    public void Update(Func<TranscriptionPreferences, TranscriptionPreferences> change, object? origin = null)
    {
        var next = change(this.Current);
        if (next == this.Current)
        {
            return;
        }

        this.Current = next;
        if (!this.readOnly)
        {
            try
            {
                this.store.Save(next);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Core.Diagnostics.AppLog.Event("settings", $"Transcription settings were not saved: {ex.Message}");
            }
        }

        this.Changed?.Invoke(origin);
    }

    /// <summary>Earlier builds kept only the live-text choice, in <c>meeting-live-text.txt</c>; carry it over once.</summary>
    private static string ReadLegacyLiveText(string path)
    {
        try
        {
            return File.Exists(path) && File.ReadAllText(path).Trim() == "draft" ? LiveTextModes.Draft : LiveTextModes.Off;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return LiveTextModes.Off;
        }
    }
}
