using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using PrimeDictate.Core.Sessions;
using PrimeDictate.Core.Storage;

namespace PrimeDictate.Core.Settings;

/// <summary>
/// Transcription-mode defaults. Stored apart from dictation's <c>settings.json</c>, which is
/// never rewritten by transcription mode.
/// </summary>
/// <remarks>
/// Earlier files also had <c>RequestedBackend</c>, <c>SpeakerDetection</c>, <c>DiarizerModelId</c>, <c>InputDeviceId</c> and <c>CopyImportedMedia</c>.
/// Nothing read them (the run records what it actually used, speaker labels are <see cref="SpeakerLabelsAfterStop"/>, imports always
/// keep their media in the session folder, and recording uses the system default microphone), so they were removed; an old file that
/// still has them loads fine.
/// </remarks>
public sealed record TranscriptionPreferences
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public string? AsrModelId { get; init; }

    public string? Language { get; init; }

    [JsonConverter(typeof(JsonStringEnumConverter<AudioRetention>))]
    public AudioRetention AudioRetention { get; init; } = AudioRetention.KeepAudio;

    /// <summary>Meetings: <c>off</c> records only and transcribes after Stop; <c>draft</c> shows a fast model's text while people talk.</summary>
    public string LiveTextMode { get; init; } = LiveTextModes.Off;

    /// <summary>Meetings: after Stop, re-read the recording, label the speakers and mark who talked over whom.</summary>
    public bool SpeakerLabelsAfterStop { get; init; } = true;

    /// <summary>Raise quiet audio before it is transcribed. The saved recording is not changed.</summary>
    public bool BoostQuietAudio { get; init; } = true;

    /// <summary>The recording source last used: <c>microphone</c>, <c>system</c> or <c>meeting</c> (both). Null until one was used.</summary>
    public string? LastSource { get; init; }
}

/// <summary>
/// First-run transcription defaults taken from the dictation settings (the WPF <c>TranscriptionPreferencesSeed</c>): the speech model
/// dictation uses, when there is one, so transcription starts on the model the user already picked.
/// </summary>
public static class TranscriptionPreferencesSeed
{
    public static TranscriptionPreferences From(string? dictationModelId, string liveTextMode) => new()
    {
        AsrModelId = string.IsNullOrWhiteSpace(dictationModelId) ? null : dictationModelId,
        LiveTextMode = liveTextMode
    };
}

public static class LiveTextModes
{
    public const string Off = "off";
    public const string Draft = "draft";
}

public static class RecordingSources
{
    public const string Microphone = "microphone";
    public const string System = "system";
    public const string Meeting = "meeting";
}

public sealed record PreferencesLoadResult(TranscriptionPreferences Preferences, bool IsReadOnly, string? Warning, string? BackupPath);

/// <summary>
/// Loads and saves <see cref="TranscriptionPreferences"/> with versioned, idempotent migrations.
/// A backup is written before any migration rewrites the file. A file from a newer app version
/// is left untouched and loaded read-only.
/// </summary>
public sealed class TranscriptionPreferencesStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string path;
    private readonly IReadOnlyDictionary<int, Func<JsonObject, JsonObject>> migrations;

    public TranscriptionPreferencesStore(string path)
        : this(path, new Dictionary<int, Func<JsonObject, JsonObject>>())
    {
    }

    /// <param name="migrations">Keyed by source version; each upgrades by exactly one version.</param>
    internal TranscriptionPreferencesStore(string path, IReadOnlyDictionary<int, Func<JsonObject, JsonObject>> migrations)
    {
        this.path = path;
        this.migrations = migrations;
    }

    public string FilePath => this.path;

    /// <param name="seed">Creates first-run defaults, typically copied from the dictation model choice.</param>
    public PreferencesLoadResult Load(Func<TranscriptionPreferences> seed, int targetVersion = TranscriptionPreferences.CurrentSchemaVersion)
    {
        if (!File.Exists(this.path))
        {
            return new PreferencesLoadResult(seed() with { SchemaVersion = targetVersion }, IsReadOnly: false, Warning: null, BackupPath: null);
        }

        JsonObject root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(this.path)) as JsonObject
                ?? throw new JsonException("Root is not an object.");
        }
        catch (JsonException)
        {
            var corruptBackup = this.Backup("corrupt");
            return new PreferencesLoadResult(
                seed() with { SchemaVersion = targetVersion },
                IsReadOnly: false,
                Warning: "Transcription settings could not be read and were reset. The unreadable file was kept.",
                BackupPath: corruptBackup);
        }

        var version = root["SchemaVersion"]?.GetValue<int>() ?? 0;
        if (version > targetVersion)
        {
            var newer = root.Deserialize<TranscriptionPreferences>(JsonOptions) ?? seed();
            return new PreferencesLoadResult(
                newer,
                IsReadOnly: true,
                Warning: $"Transcription settings were saved by a newer PrimeDictate (schema {version}). They will not be changed.",
                BackupPath: null);
        }

        string? backupPath = null;
        if (version < targetVersion)
        {
            backupPath = this.Backup($"v{version}");
            while (version < targetVersion)
            {
                if (!this.migrations.TryGetValue(version, out var migrate))
                {
                    throw new InvalidOperationException($"No migration from transcription settings schema {version}.");
                }

                root = migrate(root);
                version++;
                root["SchemaVersion"] = version;
            }

            this.WriteAtomic(root.ToJsonString(JsonOptions));
        }

        var preferences = root.Deserialize<TranscriptionPreferences>(JsonOptions) ?? seed();
        return new PreferencesLoadResult(preferences, IsReadOnly: false, Warning: null, BackupPath: backupPath);
    }

    public void Save(TranscriptionPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        this.WriteAtomic(JsonSerializer.Serialize(preferences, JsonOptions));
    }

    private string Backup(string label)
    {
        var backup = $"{this.path}.{label}.{DateTime.UtcNow:yyyyMMddHHmmss}.bak";
        File.Copy(this.path, backup, overwrite: true);
        return backup;
    }

    private void WriteAtomic(string json)
    {
        var directory = Path.GetDirectoryName(this.path) ?? throw new InvalidOperationException("Settings path has no directory.");
        AppDataPaths.EnsurePrivateDirectory(directory);
        var temp = this.path + ".tmp";
        File.WriteAllText(temp, json);
        AppDataPaths.RestrictFile(temp);
        File.Move(temp, this.path, overwrite: true);
    }
}
