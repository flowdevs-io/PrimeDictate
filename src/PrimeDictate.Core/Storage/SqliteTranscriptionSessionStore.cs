using System.Text.Json;
using Microsoft.Data.Sqlite;
using PrimeDictate.Core.Sessions;
using PrimeDictate.Core.Transcripts;

namespace PrimeDictate.Core.Storage;

/// <summary>
/// SQLite metadata store. Media stays on disk under the session media directory; the database
/// holds only paths. Transcript text never goes to logs from here.
/// </summary>
public sealed class SqliteTranscriptionSessionStore : ITranscriptionSessionStore
{
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General);

    private readonly string databasePath;
    private readonly string mediaRoot;
    private readonly SemaphoreSlim gate = new(1, 1);
    private SqliteConnection? connection;

    public SqliteTranscriptionSessionStore(string databasePath, string mediaRoot)
    {
        this.databasePath = Path.GetFullPath(databasePath);
        this.mediaRoot = Path.GetFullPath(mediaRoot);
    }

    public static SqliteTranscriptionSessionStore Create(AppDataPaths paths) =>
        new(paths.SessionDatabasePath, paths.SessionMediaDirectory);

    public async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (this.connection is not null)
            {
                return;
            }

            AppDataPaths.EnsurePrivateDirectory(Path.GetDirectoryName(this.databasePath)!);
            AppDataPaths.EnsurePrivateDirectory(this.mediaRoot);
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = this.databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            };
            var conn = new SqliteConnection(builder.ToString());
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            AppDataPaths.RestrictFile(this.databasePath);
            await ExecuteAsync(conn, null, "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL;", cancellationToken).ConfigureAwait(false);

            var version = await ScalarLongAsync(conn, "PRAGMA user_version;", cancellationToken).ConfigureAwait(false);
            if (version > CurrentSchemaVersion)
            {
                await conn.DisposeAsync().ConfigureAwait(false);
                throw new InvalidOperationException(
                    $"The transcription database was created by a newer PrimeDictate (schema {version}). It was not modified.");
            }

            if (version < 1)
            {
                await using var tx = conn.BeginTransaction();
                await ExecuteAsync(conn, tx, SchemaV1, cancellationToken).ConfigureAwait(false);
                await ExecuteAsync(conn, tx, "PRAGMA user_version = 1;", cancellationToken).ConfigureAwait(false);
                await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            this.connection = conn;
        }
        finally
        {
            this.gate.Release();
        }
    }

    public async ValueTask SaveCheckpointAsync(TranscriptDocument document, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var conn = this.RequireConnection();
            await using var tx = conn.BeginTransaction();

            await using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO sessions (id, schema_version, title, source_type, status, created_at, updated_at,
                        duration_ticks, language, active_result_version, media_json)
                    VALUES ($id, $schema, $title, $source, $status, $created, $updated, $duration, $language, $active, $media)
                    ON CONFLICT(id) DO UPDATE SET
                        schema_version = excluded.schema_version, title = excluded.title, status = excluded.status,
                        updated_at = excluded.updated_at, duration_ticks = excluded.duration_ticks,
                        language = excluded.language, active_result_version = excluded.active_result_version,
                        media_json = excluded.media_json;
                    """;
                cmd.Parameters.AddWithValue("$id", document.SessionId.ToString("D"));
                cmd.Parameters.AddWithValue("$schema", document.SchemaVersion);
                cmd.Parameters.AddWithValue("$title", document.Title);
                cmd.Parameters.AddWithValue("$source", (int)document.SourceType);
                cmd.Parameters.AddWithValue("$status", (int)document.Status);
                cmd.Parameters.AddWithValue("$created", document.CreatedAt.ToString("O"));
                cmd.Parameters.AddWithValue("$updated", document.UpdatedAt.ToString("O"));
                cmd.Parameters.AddWithValue("$duration", (object?)document.Duration?.Ticks ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$language", (object?)document.Language ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$active", document.ActiveResultVersion);
                cmd.Parameters.AddWithValue("$media", document.Media is null ? DBNull.Value : JsonSerializer.Serialize(document.Media, JsonOptions));
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            foreach (var run in document.Runs)
            {
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO runs (session_id, result_version, run_json) VALUES ($id, $version, $json)
                    ON CONFLICT(session_id, result_version) DO UPDATE SET run_json = excluded.run_json;
                    """;
                cmd.Parameters.AddWithValue("$id", document.SessionId.ToString("D"));
                cmd.Parameters.AddWithValue("$version", run.ResultVersion);
                cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(run, JsonOptions));
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            foreach (var speaker in document.Speakers)
            {
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO speakers (session_id, id, default_label, display_name) VALUES ($sid, $id, $label, $name)
                    ON CONFLICT(session_id, id) DO UPDATE SET default_label = excluded.default_label, display_name = excluded.display_name;
                    """;
                cmd.Parameters.AddWithValue("$sid", document.SessionId.ToString("D"));
                cmd.Parameters.AddWithValue("$id", speaker.Id);
                cmd.Parameters.AddWithValue("$label", speaker.DefaultLabel);
                cmd.Parameters.AddWithValue("$name", (object?)speaker.DisplayName ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var clear = conn.CreateCommand())
            {
                clear.Transaction = tx;
                clear.CommandText = "DELETE FROM audio_refs WHERE session_id = $id;";
                clear.Parameters.AddWithValue("$id", document.SessionId.ToString("D"));
                await clear.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            foreach (var audio in document.Audio)
            {
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO audio_refs (session_id, kind, path, sha256) VALUES ($id, $kind, $path, $sha);";
                cmd.Parameters.AddWithValue("$id", document.SessionId.ToString("D"));
                cmd.Parameters.AddWithValue("$kind", (int)audio.Kind);
                cmd.Parameters.AddWithValue("$path", audio.Path);
                cmd.Parameters.AddWithValue("$sha", (object?)audio.Sha256 ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            foreach (var segment in document.Segments.Where(s => s.State == SegmentState.Final))
            {
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO segments (session_id, result_version, id, start_ticks, end_ticks, raw_text, edited_text,
                        revision, confidence, provenance, words_json, speakers_json)
                    VALUES ($sid, $version, $id, $start, $end, $raw, $edited, $revision, $confidence, $provenance, $words, $speakers)
                    ON CONFLICT(session_id, result_version, id) DO UPDATE SET
                        start_ticks = CASE WHEN excluded.revision > segments.revision THEN excluded.start_ticks ELSE segments.start_ticks END,
                        end_ticks = CASE WHEN excluded.revision > segments.revision THEN excluded.end_ticks ELSE segments.end_ticks END,
                        raw_text = CASE WHEN excluded.revision > segments.revision THEN excluded.raw_text ELSE segments.raw_text END,
                        confidence = CASE WHEN excluded.revision > segments.revision THEN excluded.confidence ELSE segments.confidence END,
                        provenance = CASE WHEN excluded.revision > segments.revision THEN excluded.provenance ELSE segments.provenance END,
                        words_json = CASE WHEN excluded.revision > segments.revision THEN excluded.words_json ELSE segments.words_json END,
                        speakers_json = CASE WHEN excluded.revision > segments.revision THEN excluded.speakers_json ELSE segments.speakers_json END,
                        revision = MAX(excluded.revision, segments.revision),
                        edited_text = excluded.edited_text;
                    """;
                cmd.Parameters.AddWithValue("$sid", document.SessionId.ToString("D"));
                cmd.Parameters.AddWithValue("$version", segment.ResultVersion);
                cmd.Parameters.AddWithValue("$id", segment.Id);
                cmd.Parameters.AddWithValue("$start", segment.Start.Ticks);
                cmd.Parameters.AddWithValue("$end", segment.End.Ticks);
                cmd.Parameters.AddWithValue("$raw", segment.RawText);
                cmd.Parameters.AddWithValue("$edited", (object?)segment.EditedText ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$revision", segment.Revision);
                cmd.Parameters.AddWithValue("$confidence", (object?)segment.Confidence ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$provenance", (int)segment.TimingProvenance);
                cmd.Parameters.AddWithValue("$words", segment.Words is null ? DBNull.Value : JsonSerializer.Serialize(segment.Words, JsonOptions));
                cmd.Parameters.AddWithValue("$speakers", JsonSerializer.Serialize(segment.Speakers, JsonOptions));
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            this.gate.Release();
        }
    }

    public async ValueTask<TranscriptDocument?> LoadAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var conn = this.RequireConnection();
            var id = sessionId.ToString("D");
            TranscriptDocument? document = null;
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT schema_version, title, source_type, status, created_at, updated_at, duration_ticks, language,
                        active_result_version, media_json
                    FROM sessions WHERE id = $id;
                    """;
                cmd.Parameters.AddWithValue("$id", id);
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return null;
                }

                document = new TranscriptDocument
                {
                    SessionId = sessionId,
                    SchemaVersion = reader.GetInt32(0),
                    Title = reader.GetString(1),
                    SourceType = (TranscriptSourceType)reader.GetInt32(2),
                    Status = (TranscriptSessionStatus)reader.GetInt32(3),
                    CreatedAt = DateTimeOffset.Parse(reader.GetString(4), System.Globalization.CultureInfo.InvariantCulture),
                    UpdatedAt = DateTimeOffset.Parse(reader.GetString(5), System.Globalization.CultureInfo.InvariantCulture),
                    Duration = reader.IsDBNull(6) ? null : TimeSpan.FromTicks(reader.GetInt64(6)),
                    Language = reader.IsDBNull(7) ? null : reader.GetString(7),
                    ActiveResultVersion = reader.GetInt32(8),
                    Media = reader.IsDBNull(9) ? null : JsonSerializer.Deserialize<MediaMetadata>(reader.GetString(9), JsonOptions)
                };
            }

            var runs = new List<RecognitionRunInfo>();
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT run_json FROM runs WHERE session_id = $id ORDER BY result_version;";
                cmd.Parameters.AddWithValue("$id", id);
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    runs.Add(JsonSerializer.Deserialize<RecognitionRunInfo>(reader.GetString(0), JsonOptions)!);
                }
            }

            var speakers = new List<TranscriptSpeaker>();
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT id, default_label, display_name FROM speakers WHERE session_id = $id ORDER BY rowid;";
                cmd.Parameters.AddWithValue("$id", id);
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    speakers.Add(new TranscriptSpeaker(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
                }
            }

            var audio = await this.ReadAudioRefsAsync(conn, id, cancellationToken).ConfigureAwait(false);

            var segments = new List<TranscriptSegment>();
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT result_version, id, start_ticks, end_ticks, raw_text, edited_text, revision, confidence,
                        provenance, words_json, speakers_json
                    FROM segments WHERE session_id = $id ORDER BY result_version, start_ticks, id;
                    """;
                cmd.Parameters.AddWithValue("$id", id);
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    segments.Add(new TranscriptSegment
                    {
                        ResultVersion = reader.GetInt32(0),
                        Id = reader.GetString(1),
                        Start = TimeSpan.FromTicks(reader.GetInt64(2)),
                        End = TimeSpan.FromTicks(reader.GetInt64(3)),
                        RawText = reader.GetString(4),
                        EditedText = reader.IsDBNull(5) ? null : reader.GetString(5),
                        Revision = reader.GetInt64(6),
                        Confidence = reader.IsDBNull(7) ? null : reader.GetDouble(7),
                        TimingProvenance = (TimingProvenance)reader.GetInt32(8),
                        Words = reader.IsDBNull(9) ? null : JsonSerializer.Deserialize<List<WordTiming>>(reader.GetString(9), JsonOptions),
                        Speakers = JsonSerializer.Deserialize<List<SpeakerAttribution>>(reader.GetString(10), JsonOptions) ?? [],
                        State = SegmentState.Final
                    });
                }
            }

            return document with { Runs = runs, Speakers = speakers, Audio = audio, Segments = segments };
        }
        finally
        {
            this.gate.Release();
        }
    }

    public async ValueTask<IReadOnlyList<TranscriptSessionSummary>> ListAsync(int skip, int take, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(take);
        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await this.QuerySummariesAsync(
                this.RequireConnection(),
                null,
                "ORDER BY s.updated_at DESC LIMIT $take OFFSET $skip",
                p =>
                {
                    p.AddWithValue("$take", take);
                    p.AddWithValue("$skip", skip);
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            this.gate.Release();
        }
    }

    public async ValueTask<IReadOnlyList<TranscriptSessionSummary>> MarkInterruptedSessionsAsync(CancellationToken cancellationToken)
    {
        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var conn = this.RequireConnection();
            await using var tx = conn.BeginTransaction();
            var inFlight = new[] { TranscriptSessionStatus.Running, TranscriptSessionStatus.Paused, TranscriptSessionStatus.Finalizing }
                .Select(s => ((int)s).ToString(System.Globalization.CultureInfo.InvariantCulture));
            var inList = string.Join(",", inFlight);
            await ExecuteAsync(
                conn,
                tx,
                $"UPDATE sessions SET status = {(int)TranscriptSessionStatus.Interrupted} WHERE status IN ({inList});",
                cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return await this.QuerySummariesAsync(
                conn,
                $"WHERE s.status = {(int)TranscriptSessionStatus.Interrupted}",
                "ORDER BY s.updated_at DESC",
                null,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            this.gate.Release();
        }
    }

    public async ValueTask<IReadOnlyList<string>> DeleteOwnedAudioAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var conn = this.RequireConnection();
            var id = sessionId.ToString("D");
            var refs = await this.ReadAudioRefsAsync(conn, id, cancellationToken).ConfigureAwait(false);
            var deleted = this.DeleteOwnedFiles(refs, []);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"DELETE FROM audio_refs WHERE session_id = $id AND kind = {(int)AudioReferenceKind.Owned};";
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return deleted;
        }
        finally
        {
            this.gate.Release();
        }
    }

    public async ValueTask<SessionDeletionResult> DeleteAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var conn = this.RequireConnection();
            var id = sessionId.ToString("D");
            var refs = await this.ReadAudioRefsAsync(conn, id, cancellationToken).ConfigureAwait(false);

            int removed;
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM sessions WHERE id = $id;";
                cmd.Parameters.AddWithValue("$id", id);
                removed = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            // The row is already removed; a locked file must be reported, not crash the caller.
            var failed = new List<string>();
            var deleted = this.DeleteOwnedFiles(refs, failed);
            var sessionMedia = this.GetSessionMediaDirectory(sessionId);
            if (Directory.Exists(sessionMedia))
            {
                try
                {
                    Directory.Delete(sessionMedia, recursive: true);
                    deleted.Add(sessionMedia);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failed.Add(sessionMedia);
                }
            }

            var kept = refs.Where(r => r.Kind == AudioReferenceKind.ExternalReference).Select(r => r.Path).ToList();
            return new SessionDeletionResult(removed > 0, deleted, kept) { FailedFiles = failed };
        }
        finally
        {
            this.gate.Release();
        }
    }

    /// <summary>Directory for audio and caches owned by one session.</summary>
    public string GetSessionMediaDirectory(Guid sessionId) => Path.Combine(this.mediaRoot, sessionId.ToString("N"));

    public async ValueTask DisposeAsync()
    {
        await this.gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (this.connection is not null)
            {
                await this.connection.DisposeAsync().ConfigureAwait(false);
                this.connection = null;
            }
        }
        finally
        {
            this.gate.Release();
        }
    }

    private List<string> DeleteOwnedFiles(IEnumerable<AudioReference> refs, List<string> failed)
    {
        var deleted = new List<string>();
        foreach (var audio in refs.Where(r => r.Kind == AudioReferenceKind.Owned))
        {
            var full = Path.GetFullPath(audio.Path);
            // Owned audio must live under the media root; anything else is refused, not deleted.
            if (!IsUnder(full, this.mediaRoot))
            {
                continue;
            }

            try
            {
                if (File.Exists(full))
                {
                    File.Delete(full);
                    deleted.Add(full);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add(full);
            }
        }

        return deleted;
    }

    private static bool IsUnder(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    private async Task<List<AudioReference>> ReadAudioRefsAsync(SqliteConnection conn, string id, CancellationToken cancellationToken)
    {
        var audio = new List<AudioReference>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT kind, path, sha256 FROM audio_refs WHERE session_id = $id ORDER BY rowid;";
        cmd.Parameters.AddWithValue("$id", id);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            audio.Add(new AudioReference((AudioReferenceKind)reader.GetInt32(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
        }

        return audio;
    }

    private async Task<IReadOnlyList<TranscriptSessionSummary>> QuerySummariesAsync(
        SqliteConnection conn,
        string? where,
        string tail,
        Action<SqliteParameterCollection>? bind,
        CancellationToken cancellationToken)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT s.id, s.title, s.source_type, s.status, s.created_at, s.updated_at, s.duration_ticks,
                (SELECT COUNT(*) FROM segments g WHERE g.session_id = s.id AND g.result_version = s.active_result_version)
            FROM sessions s {where} {tail};
            """;
        bind?.Invoke(cmd.Parameters);
        var result = new List<TranscriptSessionSummary>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new TranscriptSessionSummary(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                (TranscriptSourceType)reader.GetInt32(2),
                (TranscriptSessionStatus)reader.GetInt32(3),
                DateTimeOffset.Parse(reader.GetString(4), System.Globalization.CultureInfo.InvariantCulture),
                DateTimeOffset.Parse(reader.GetString(5), System.Globalization.CultureInfo.InvariantCulture),
                reader.IsDBNull(6) ? null : TimeSpan.FromTicks(reader.GetInt64(6)),
                reader.GetInt32(7)));
        }

        return result;
    }

    private SqliteConnection RequireConnection() =>
        this.connection ?? throw new InvalidOperationException("Call InitializeAsync before using the session store.");

    private static async Task ExecuteAsync(SqliteConnection conn, SqliteTransaction? tx, string sql, CancellationToken cancellationToken)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<long> ScalarLongAsync(SqliteConnection conn, string sql, CancellationToken cancellationToken)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return (long)(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L);
    }

    private const string SchemaV1 = """
        CREATE TABLE sessions (
            id TEXT PRIMARY KEY,
            schema_version INTEGER NOT NULL,
            title TEXT NOT NULL,
            source_type INTEGER NOT NULL,
            status INTEGER NOT NULL,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            duration_ticks INTEGER NULL,
            language TEXT NULL,
            active_result_version INTEGER NOT NULL,
            media_json TEXT NULL
        );
        CREATE INDEX ix_sessions_updated ON sessions (updated_at DESC);
        CREATE INDEX ix_sessions_status ON sessions (status);

        CREATE TABLE runs (
            session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
            result_version INTEGER NOT NULL,
            run_json TEXT NOT NULL,
            PRIMARY KEY (session_id, result_version)
        );

        CREATE TABLE speakers (
            session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
            id TEXT NOT NULL,
            default_label TEXT NOT NULL,
            display_name TEXT NULL,
            PRIMARY KEY (session_id, id)
        );

        CREATE TABLE audio_refs (
            session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
            kind INTEGER NOT NULL,
            path TEXT NOT NULL,
            sha256 TEXT NULL
        );
        CREATE INDEX ix_audio_refs_session ON audio_refs (session_id);

        CREATE TABLE segments (
            session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
            result_version INTEGER NOT NULL,
            id TEXT NOT NULL,
            start_ticks INTEGER NOT NULL,
            end_ticks INTEGER NOT NULL,
            raw_text TEXT NOT NULL,
            edited_text TEXT NULL,
            revision INTEGER NOT NULL,
            confidence REAL NULL,
            provenance INTEGER NOT NULL,
            words_json TEXT NULL,
            speakers_json TEXT NOT NULL,
            PRIMARY KEY (session_id, result_version, id)
        );
        CREATE INDEX ix_segments_time ON segments (session_id, result_version, start_ticks);
        """;
}
