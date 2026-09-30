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
public sealed record TranscriptionPreferences
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public string? AsrModelId { get; init; }

    public string RequestedBackend { get; init; } = "cpu";

    public string? Language { get; init; }

    public bool SpeakerDetection { get; init; }

    public string? DiarizerModelId { get; init; }

    [JsonConverter(typeof(JsonStringEnumConverter<AudioRetention>))]
    public AudioRetention AudioRetention { get; init; } = AudioRetention.KeepAudio;

    public string? InputDeviceId { get; init; }

    /// <summary>Copy imported files into app storage instead of referencing them in place.</summary>
    public bool CopyImportedMedia { get; init; }
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
