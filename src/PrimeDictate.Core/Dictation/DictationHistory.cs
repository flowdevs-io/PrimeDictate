using System.Text.Json;
using Microsoft.Data.Sqlite;
using PrimeDictate.Core.Storage;

namespace PrimeDictate.Core.Dictation;

public sealed record DictationHistoryEntry(
    Guid Id,
    Guid SessionId,
    DateTime TimestampUtc,
    string Transcript,
    DictationDeliveryStatus Status,
    string? TargetDisplayName,
    string? TargetAppName,
    string? TargetWindowTitle,
    string? Error,
    double AudioSeconds,
    bool EnterSent,
    string? OriginalTranscript,
    string? RewriteSystemPrompt)
{
    public static DictationHistoryEntry From(DictationCommit c) => new(
        Guid.NewGuid(), c.SessionId, c.TimestampUtc, c.Transcript, c.Status, c.TargetDisplayName, c.TargetAppName, c.TargetWindowTitle,
        c.Error, c.AudioDuration.TotalSeconds, c.EnterSent, c.OriginalTranscript, c.RewriteSystemPrompt);
}

/// <summary>
/// The committed-transcript log for dictation: every final transcript with where it went and whether it was typed.
/// Kept apart from transcription sessions. The WPF app's <c>history.json</c> is imported once, read-only.
/// </summary>
public sealed class DictationHistoryStore
{
    public const int MaxEntries = 1_000;

    private readonly string databasePath;
    private readonly string legacyPath;
    private readonly object sync = new();
    private bool initialized;

    public DictationHistoryStore(AppDataPaths paths)
    {
        this.databasePath = Path.Combine(paths.Root, "dictation-history.db");
        this.legacyPath = Path.Combine(paths.Root, "history.json");
        AppDataPaths.EnsurePrivateDirectory(paths.Root);
    }

    public void Add(DictationHistoryEntry entry)
    {
        lock (this.sync)
        {
            using var connection = this.Open();
            Insert(connection, entry);
            using var trim = connection.CreateCommand();
            trim.CommandText = "DELETE FROM entries WHERE id NOT IN (SELECT id FROM entries ORDER BY timestamp DESC LIMIT $max)";
            trim.Parameters.AddWithValue("$max", MaxEntries);
            trim.ExecuteNonQuery();
        }
    }

    /// <summary>Newest first. <paramref name="search"/> matches the transcript, the original and the target app or window.</summary>
    public IReadOnlyList<DictationHistoryEntry> List(string? search = null, int limit = 200)
    {
        lock (this.sync)
        {
            using var connection = this.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, session_id, timestamp, transcript, status, target_display, target_app, target_title, error, audio_seconds, enter_sent, original, rewrite_prompt
                FROM entries
                WHERE $q IS NULL OR transcript LIKE $like ESCAPE '\' OR original LIKE $like ESCAPE '\'
                   OR target_app LIKE $like ESCAPE '\' OR target_title LIKE $like ESCAPE '\'
                ORDER BY timestamp DESC LIMIT $limit
                """;
            var query = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
            command.Parameters.AddWithValue("$q", (object?)query ?? DBNull.Value);
            command.Parameters.AddWithValue("$like", query is null ? DBNull.Value : "%" + query.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%");
            command.Parameters.AddWithValue("$limit", limit);
            var list = new List<DictationHistoryEntry>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new DictationHistoryEntry(
                    Guid.Parse(reader.GetString(0)),
                    Guid.Parse(reader.GetString(1)),
                    DateTime.Parse(reader.GetString(2), null, System.Globalization.DateTimeStyles.RoundtripKind),
                    reader.GetString(3),
                    Enum.TryParse<DictationDeliveryStatus>(reader.GetString(4), out var status) ? status : DictationDeliveryStatus.Injected,
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8),
                    reader.GetDouble(9),
                    reader.GetInt64(10) != 0,
                    reader.IsDBNull(11) ? null : reader.GetString(11),
                    reader.IsDBNull(12) ? null : reader.GetString(12)));
            }

            return list;
        }
    }

    public void Delete(Guid id) => this.Execute("DELETE FROM entries WHERE id = $id", ("$id", id.ToString()));

    public void Clear() => this.Execute("DELETE FROM entries");

    private void Execute(string sql, params (string Name, object Value)[] args)
    {
        lock (this.sync)
        {
            using var connection = this.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in args)
            {
                command.Parameters.AddWithValue(name, value);
            }

            command.ExecuteNonQuery();
        }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = this.databasePath, Pooling = false }.ToString());
        connection.Open();
        if (!this.initialized)
        {
            using (var create = connection.CreateCommand())
            {
                create.CommandText = """
                    CREATE TABLE IF NOT EXISTS entries (
                      id TEXT PRIMARY KEY, session_id TEXT NOT NULL, timestamp TEXT NOT NULL, transcript TEXT NOT NULL,
                      status TEXT NOT NULL, target_display TEXT, target_app TEXT, target_title TEXT, error TEXT,
                      audio_seconds REAL NOT NULL, enter_sent INTEGER NOT NULL, original TEXT, rewrite_prompt TEXT);
                    CREATE INDEX IF NOT EXISTS entries_timestamp ON entries(timestamp);
                    """;
                create.ExecuteNonQuery();
            }

            AppDataPaths.RestrictFile(this.databasePath);
            this.ImportLegacy(connection);
            this.initialized = true;
        }

        return connection;
    }

    /// <summary>One-time, read-only import of the WPF history. Runs once per database.</summary>
    private void ImportLegacy(SqliteConnection connection)
    {
        using (var version = connection.CreateCommand())
        {
            // user_version 1 marks the import as done, so clearing history does not bring the old entries back.
            version.CommandText = "PRAGMA user_version";
            if ((long)version.ExecuteScalar()! >= 1)
            {
                return;
            }
        }

        using (var mark = connection.CreateCommand())
        {
            mark.CommandText = "PRAGMA user_version = 1";
            mark.ExecuteNonQuery();
        }

        if (!File.Exists(this.legacyPath))
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(this.legacyPath));
            using var transaction = connection.BeginTransaction();
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                if (!e.TryGetProperty("Transcript", out var transcript) || transcript.GetString() is not { Length: > 0 } text)
                {
                    continue;
                }

                var status = Str(e, "DeliveryStatus") is { } s && Enum.TryParse<DictationDeliveryStatus>(s, out var parsed) ? parsed : DictationDeliveryStatus.Injected;
                Insert(connection, new DictationHistoryEntry(
                    Guid.TryParse(Str(e, "Id"), out var id) ? id : Guid.NewGuid(),
                    Guid.TryParse(Str(e, "ThreadId"), out var session) ? session : Guid.NewGuid(),
                    DateTime.TryParse(Str(e, "TimestampUtc"), null, System.Globalization.DateTimeStyles.RoundtripKind, out var ts) ? ts : DateTime.UtcNow,
                    text, status, Str(e, "TargetDisplayName"), Str(e, "TargetAppName"), Str(e, "TargetWindowTitle"), Str(e, "Error"),
                    e.TryGetProperty("AudioDurationSeconds", out var secs) && secs.TryGetDouble(out var d) ? d : 0,
                    e.TryGetProperty("SendEnterAfterCommit", out var enter) && enter.ValueKind == JsonValueKind.True,
                    Str(e, "OriginalTranscript"), Str(e, "OllamaSystemPrompt")), transaction);
            }

            transaction.Commit();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            // An unreadable legacy file is left alone; the new history simply starts empty.
        }

        static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    private static void Insert(SqliteConnection connection, DictationHistoryEntry e, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR REPLACE INTO entries VALUES ($id, $session, $ts, $text, $status, $display, $app, $title, $error, $secs, $enter, $orig, $prompt)
            """;
        command.Parameters.AddWithValue("$id", e.Id.ToString());
        command.Parameters.AddWithValue("$session", e.SessionId.ToString());
        command.Parameters.AddWithValue("$ts", e.TimestampUtc.ToString("O"));
        command.Parameters.AddWithValue("$text", e.Transcript);
        command.Parameters.AddWithValue("$status", e.Status.ToString());
        command.Parameters.AddWithValue("$display", (object?)e.TargetDisplayName ?? DBNull.Value);
        command.Parameters.AddWithValue("$app", (object?)e.TargetAppName ?? DBNull.Value);
        command.Parameters.AddWithValue("$title", (object?)e.TargetWindowTitle ?? DBNull.Value);
        command.Parameters.AddWithValue("$error", (object?)e.Error ?? DBNull.Value);
        command.Parameters.AddWithValue("$secs", e.AudioSeconds);
        command.Parameters.AddWithValue("$enter", e.EnterSent ? 1 : 0);
        command.Parameters.AddWithValue("$orig", (object?)e.OriginalTranscript ?? DBNull.Value);
        command.Parameters.AddWithValue("$prompt", (object?)e.RewriteSystemPrompt ?? DBNull.Value);
        command.ExecuteNonQuery();
    }
}
