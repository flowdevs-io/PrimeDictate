using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using PrimeDictate.Core.Sessions;
using PrimeDictate.Core.Settings;
using PrimeDictate.Core.Storage;
using PrimeDictate.Core.Transcripts;
using static PrimeDictate.Core.Tests.TestData;

namespace PrimeDictate.Core.Tests;

public sealed class StorageTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pd-core-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(this.root))
        {
            Directory.Delete(this.root, recursive: true);
        }
    }

    private AppDataPaths Paths => new(this.root);

    private async Task<SqliteTranscriptionSessionStore> OpenStoreAsync()
    {
        var store = SqliteTranscriptionSessionStore.Create(this.Paths);
        await store.InitializeAsync(CancellationToken.None);
        return store;
    }

    [Fact]
    public async Task Segments_saved_in_finish_order_load_sorted_by_start_and_a_late_note_needs_its_own_checkpoint()
    {
        await using var store = await this.OpenStoreAsync();
        var doc = NewDocument();
        doc = TranscriptDocumentReducer.Apply(doc, new SessionStarted(doc.SessionId, Run()), Now);
        // The two meeting streams finish in any order, so segments are stored in the order they were finalized.
        foreach (var (id, start) in new[] { ("us0.0", 5.0), ("um0.0", 1.0), ("us0.1", 3.0) })
        {
            doc = TranscriptDocumentReducer.Apply(doc, new SegmentFinalized(doc.SessionId, Segment(id, id, start, start + 1)), Now);
        }

        var host = new PrimeDictate.Core.Pipeline.SessionDocumentHost(doc, store);
        await host.CheckpointAsync();
        host.AddNote("added after the session was already saved");

        Assert.DoesNotContain("added after the session was already saved", (await store.LoadAsync(doc.SessionId, default))!.Notes);
        await host.CheckpointAsync();
        var loaded = (await store.LoadAsync(doc.SessionId, default))!;

        Assert.Equal(["um0.0", "us0.1", "us0.0"], loaded.Segments.Select(s => s.Id));
        Assert.Contains("added after the session was already saved", loaded.Notes);
    }

    [Fact]
    public async Task Checkpoint_round_trips_final_content_but_not_partials()
    {
        await using var store = await this.OpenStoreAsync();
        var doc = NewDocument();
        doc = TranscriptDocumentReducer.Apply(doc, new SessionStarted(doc.SessionId, Run()), Now);
        doc = TranscriptDocumentReducer.Apply(doc, new SpeakerUpdated(doc.SessionId, new TranscriptSpeaker("spk0", "Speaker 1", null)), Now);
        var final = Segment("s1", "Größe — 日本語 \"quoted\"\nline", 0, 2.5) with
        {
            Confidence = 0.8,
            Words = [new WordTiming("Größe", TimeSpan.Zero, TimeSpan.FromSeconds(0.4), 0.9, TimingProvenance.Model, "spk0")],
            Speakers = [new SpeakerAttribution("spk0", TimeSpan.Zero, TimeSpan.FromSeconds(2.5), null)],
            TimingProvenance = TimingProvenance.Model
        };
        doc = TranscriptDocumentReducer.Apply(doc, new SegmentFinalized(doc.SessionId, final), Now);
        doc = TranscriptDocumentReducer.Apply(doc, new SegmentUpserted(doc.SessionId, Segment("s2", "partial", 2.5, 3, state: SegmentState.Provisional)), Now);
        doc = TranscriptDocumentReducer.RenameSpeaker(doc, "spk0", "Justin", Now);
        doc = doc with { Media = new MediaMetadata("call.m4a", "mov", "aac", 48_000, 2, 0, "downmix", TimeSpan.FromMinutes(3)) };

        await store.SaveCheckpointAsync(doc, CancellationToken.None);
        var loaded = await store.LoadAsync(doc.SessionId, CancellationToken.None);

        Assert.NotNull(loaded);
        var segment = Assert.Single(loaded!.Segments);
        Assert.Equal(final.RawText, segment.RawText);
        Assert.Equal(final.Start, segment.Start);
        Assert.Equal(final.End, segment.End);
        Assert.Equal(0.8, segment.Confidence);
        Assert.Equal(TimingProvenance.Model, segment.TimingProvenance);
        var storedWord = Assert.Single(segment.Words!);
        Assert.Equal("Größe", storedWord.Text);
        Assert.Equal("spk0", storedWord.SpeakerId);
        Assert.Equal("spk0", Assert.Single(segment.Speakers).SpeakerId);
        Assert.Equal("Justin", Assert.Single(loaded.Speakers).Name);
        Assert.Equal("whisper-onnx/base.en", Assert.Single(loaded.Runs).AsrModelId);
        Assert.Equal(48_000, loaded.Media!.SampleRate);
        Assert.Equal(TranscriptSessionStatus.Running, loaded.Status);
    }

    [Fact]
    public async Task Edits_persist_and_stale_revisions_do_not_overwrite()
    {
        await using var store = await this.OpenStoreAsync();
        var doc = NewDocument();
        doc = TranscriptDocumentReducer.Apply(doc, new SegmentFinalized(doc.SessionId, Segment("s1", "new text", 0, 1, revision: 5)), Now);
        await store.SaveCheckpointAsync(doc, CancellationToken.None);

        var stale = doc with { Segments = [Segment("s1", "old text", 0, 1, revision: 3) with { EditedText = "edited" }] };
        await store.SaveCheckpointAsync(stale, CancellationToken.None);

        var segment = Assert.Single((await store.LoadAsync(doc.SessionId, CancellationToken.None))!.Segments);
        Assert.Equal("new text", segment.RawText);
        Assert.Equal(5, segment.Revision);
        Assert.Equal("edited", segment.EditedText);
    }

    [Fact]
    public async Task Startup_marks_in_flight_sessions_interrupted()
    {
        await using var store = await this.OpenStoreAsync();
        var running = NewDocument() with { Status = TranscriptSessionStatus.Running };
        var done = NewDocument() with { Status = TranscriptSessionStatus.Completed };
        await store.SaveCheckpointAsync(running, CancellationToken.None);
        await store.SaveCheckpointAsync(done, CancellationToken.None);

        var interrupted = await store.MarkInterruptedSessionsAsync(CancellationToken.None);
        Assert.Equal(running.SessionId, Assert.Single(interrupted).SessionId);
        Assert.Equal(TranscriptSessionStatus.Interrupted, (await store.LoadAsync(running.SessionId, CancellationToken.None))!.Status);
        Assert.Equal(TranscriptSessionStatus.Completed, (await store.LoadAsync(done.SessionId, CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task Speaker_merges_only_change_the_mapping_and_persist_with_failure_reason_and_notes()
    {
        await using var store = await this.OpenStoreAsync();
        var doc = NewDocument();
        foreach (var n in new[] { 1, 2, 3 })
        {
            doc = TranscriptDocumentReducer.Apply(doc, new SpeakerUpdated(doc.SessionId, new TranscriptSpeaker($"speaker-{n}", $"Speaker {n}", null)), Now);
        }

        TranscriptSegment Spoken(string id, string text, double start, string speaker) =>
            Segment(id, text, start, start + 1) with { Speakers = [new SpeakerAttribution(speaker, TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(start + 1), null)] };
        doc = TranscriptDocumentReducer.Apply(doc, new SegmentFinalized(doc.SessionId, Spoken("a", "one", 0, "speaker-1")), Now);
        doc = TranscriptDocumentReducer.Apply(doc, new SegmentFinalized(doc.SessionId, Spoken("b", "two", 2, "speaker-3")), Now);
        doc = TranscriptDocumentReducer.RenameSpeaker(doc, "speaker-1", "Justin", Now);
        doc = TranscriptDocumentReducer.MergeSpeaker(doc, "speaker-3", "speaker-1", Now);
        doc = TranscriptDocumentReducer.MergeSpeaker(doc, "speaker-2", "speaker-3", Now);
        doc = TranscriptDocumentReducer.MergeSpeaker(doc, "speaker-1", "speaker-2", Now); // would be a cycle: no-op
        doc = TranscriptDocumentReducer.Apply(doc, new SessionFailed(doc.SessionId, "live-failed", "NemotronException: lost", true), Now);
        doc = doc with { Notes = ["00:01:02: restarted"] };

        Assert.Equal("speaker-1", doc.ResolveSpeakerId("speaker-2"));
        Assert.Equal(["speaker-1"], doc.VisibleSpeakers.Select(x => x.Id));
        // Segments keep their original speaker ids.
        Assert.Equal("speaker-3", doc.Segments.Single(x => x.Id == "b").Speakers[0].SpeakerId);
        Assert.Contains("Justin", PrimeDictate.Core.Export.TranscriptExporter.Export(doc, new PrimeDictate.Core.Export.ExportOptions(PrimeDictate.Core.Export.ExportFormat.Text)).Split('\n').Single(l => l.Contains("two")));

        await store.SaveCheckpointAsync(doc, CancellationToken.None);
        var loaded = (await store.LoadAsync(doc.SessionId, CancellationToken.None))!;
        Assert.Equal("speaker-1", loaded.ResolveSpeakerId("speaker-3"));
        Assert.Equal("live-failed: NemotronException: lost", loaded.FailureReason);
        Assert.Equal(["00:01:02: restarted"], loaded.Notes);

        loaded = TranscriptDocumentReducer.UnmergeSpeaker(loaded, "speaker-3", Now);
        Assert.Equal("speaker-3", loaded.ResolveSpeakerId("speaker-3"));
        Assert.Equal("speaker-1", loaded.ResolveSpeakerId("speaker-2")); // merges point at the group root, so splitting 3 out leaves 2 where it was
    }

    [Fact]
    public async Task Delete_removes_owned_audio_and_never_the_users_original()
    {
        await using var store = await this.OpenStoreAsync();
        var doc = NewDocument();
        var mediaDir = store.GetSessionMediaDirectory(doc.SessionId);
        Directory.CreateDirectory(mediaDir);
        var owned = Path.Combine(mediaDir, "recording.flac");
        File.WriteAllText(owned, "audio");
        var original = Path.Combine(this.root, "Documents", "interview.m4a");
        Directory.CreateDirectory(Path.GetDirectoryName(original)!);
        File.WriteAllText(original, "user file");
        var outsideButMarkedOwned = Path.Combine(this.root, "Documents", "not-ours.wav");
        File.WriteAllText(outsideButMarkedOwned, "user file");

        doc = doc with
        {
            Audio =
            [
                new AudioReference(AudioReferenceKind.Owned, owned, null),
                new AudioReference(AudioReferenceKind.ExternalReference, original, null),
                new AudioReference(AudioReferenceKind.Owned, outsideButMarkedOwned, null)
            ]
        };
        await store.SaveCheckpointAsync(doc, CancellationToken.None);

        var result = await store.DeleteAsync(doc.SessionId, CancellationToken.None);

        Assert.True(result.Existed);
        Assert.False(File.Exists(owned));
        Assert.False(Directory.Exists(mediaDir));
        Assert.True(File.Exists(original));
        Assert.True(File.Exists(outsideButMarkedOwned));
        Assert.Contains(original, result.KeptExternalFiles);
        Assert.Null(await store.LoadAsync(doc.SessionId, CancellationToken.None));
    }

    [Fact]
    public async Task Delete_reports_a_locked_file_instead_of_throwing()
    {
        await using var store = await this.OpenStoreAsync();
        var doc = NewDocument();
        var mediaDir = store.GetSessionMediaDirectory(doc.SessionId);
        Directory.CreateDirectory(mediaDir);
        var owned = Path.Combine(mediaDir, "recording-16k-stereo.wav");
        doc = doc with { Audio = [new AudioReference(AudioReferenceKind.Owned, owned, null)] };
        await store.SaveCheckpointAsync(doc, CancellationToken.None);

        // Same situation as a live recording's writer: the file is open with no sharing.
        await using var held = new FileStream(owned, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        var result = await store.DeleteAsync(doc.SessionId, CancellationToken.None);

        Assert.True(result.Existed);
        Assert.Null(await store.LoadAsync(doc.SessionId, CancellationToken.None));
        if (OperatingSystem.IsWindows())
        {
            Assert.Contains(owned, result.FailedFiles);
        }
    }

    [Fact]
    public async Task Transcript_only_retention_deletes_audio_and_keeps_text()
    {
        await using var store = await this.OpenStoreAsync();
        var doc = NewDocument();
        var mediaDir = store.GetSessionMediaDirectory(doc.SessionId);
        Directory.CreateDirectory(mediaDir);
        var owned = Path.Combine(mediaDir, "recording.flac");
        File.WriteAllText(owned, "audio");
        doc = TranscriptDocumentReducer.Apply(doc, new SegmentFinalized(doc.SessionId, Segment("s1", "keep me", 0, 1)), Now);
        doc = doc with { Audio = [new AudioReference(AudioReferenceKind.Owned, owned, null)] };
        await store.SaveCheckpointAsync(doc, CancellationToken.None);

        var deleted = await store.DeleteOwnedAudioAsync(doc.SessionId, CancellationToken.None);

        Assert.Equal([Path.GetFullPath(owned)], deleted);
        var loaded = (await store.LoadAsync(doc.SessionId, CancellationToken.None))!;
        Assert.Empty(loaded.Audio);
        Assert.Equal("keep me", Assert.Single(loaded.Segments).RawText);
    }

    [Fact]
    public async Task List_is_paged_newest_first_with_segment_counts()
    {
        await using var store = await this.OpenStoreAsync();
        for (var i = 0; i < 5; i++)
        {
            var doc = NewDocument() with { Title = $"S{i}", UpdatedAt = Now.AddMinutes(i) };
            doc = TranscriptDocumentReducer.Apply(doc, new SegmentFinalized(doc.SessionId, Segment("a", "x", 0, 1)), Now.AddMinutes(i));
            await store.SaveCheckpointAsync(doc, CancellationToken.None);
        }

        var page = await store.ListAsync(1, 2, CancellationToken.None);
        Assert.Equal(["S3", "S2"], page.Select(p => p.Title));
        Assert.All(page, p => Assert.Equal(1, p.SegmentCount));
    }

    [Fact]
    public async Task A_database_from_a_newer_version_is_not_modified()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(this.Paths.SessionDatabasePath)!);
        await using (var conn = new SqliteConnection($"Data Source={this.Paths.SessionDatabasePath};Pooling=False"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA user_version = 99;";
            await cmd.ExecuteNonQueryAsync();
        }

        await using var store = SqliteTranscriptionSessionStore.Create(this.Paths);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.InitializeAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_run_saved_before_SegmentsAreRows_existed_still_loads_and_reads_false()
    {
        var doc = NewDocument();
        doc = TranscriptDocumentReducer.Apply(doc, new SessionStarted(doc.SessionId, Run() with { SegmentsAreRows = true }), Now);
        await using (var store = await this.OpenStoreAsync())
        {
            await store.SaveCheckpointAsync(doc, CancellationToken.None);
            Assert.True((await store.LoadAsync(doc.SessionId, CancellationToken.None))!.Runs.Single().SegmentsAreRows);
        }

        // Every run stored before the field existed lacks it.
        await using (var conn = new SqliteConnection($"Data Source={this.Paths.SessionDatabasePath};Pooling=False"))
        {
            await conn.OpenAsync();
            await using var read = conn.CreateCommand();
            read.CommandText = "SELECT run_json FROM runs;";
            var run = JsonNode.Parse((string)(await read.ExecuteScalarAsync())!)!.AsObject();
            Assert.True(run.Remove(nameof(RecognitionRunInfo.SegmentsAreRows)));
            await using var write = conn.CreateCommand();
            write.CommandText = "UPDATE runs SET run_json = $json;";
            write.Parameters.AddWithValue("$json", run.ToJsonString());
            await write.ExecuteNonQueryAsync();
        }

        await using (var store = await this.OpenStoreAsync())
        {
            Assert.False((await store.LoadAsync(doc.SessionId, CancellationToken.None))!.Runs.Single().SegmentsAreRows);
        }
    }

    [Fact]
    public async Task Initialize_is_idempotent_across_restarts()
    {
        var doc = NewDocument();
        await using (var store = await this.OpenStoreAsync())
        {
            await store.SaveCheckpointAsync(doc, CancellationToken.None);
        }

        await using (var store = await this.OpenStoreAsync())
        {
            Assert.NotNull(await store.LoadAsync(doc.SessionId, CancellationToken.None));
        }
    }

    [Fact]
    public void Unix_permissions_are_user_only()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var dir = Path.Combine(this.root, "private");
        AppDataPaths.EnsurePrivateDirectory(dir);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(dir));
    }

    [Fact]
    public void Preferences_first_run_uses_seed_without_writing()
    {
        var store = new TranscriptionPreferencesStore(this.Paths.TranscriptionPreferencesPath);
        var result = store.Load(() => new TranscriptionPreferences { AsrModelId = "whisper-onnx/base.en" });
        Assert.Equal("whisper-onnx/base.en", result.Preferences.AsrModelId);
        Assert.False(File.Exists(store.FilePath));
    }

    [Fact]
    public void Meeting_options_default_sensibly_and_an_older_file_without_them_still_loads()
    {
        var path = this.Paths.TranscriptionPreferencesPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{ "SchemaVersion": 1, "AsrModelId": "whisper-onnx:base.en" }""");
        var store = new TranscriptionPreferencesStore(path);
        var loaded = store.Load(() => new TranscriptionPreferences()).Preferences;
        Assert.Equal("whisper-onnx:base.en", loaded.AsrModelId);
        Assert.Equal(LiveTextModes.Off, loaded.LiveTextMode);
        Assert.True(loaded.SpeakerLabelsAfterStop);
        Assert.True(loaded.BoostQuietAudio);
        Assert.Null(loaded.LastSource);

        store.Save(loaded with { LiveTextMode = LiveTextModes.Draft, BoostQuietAudio = false, LastSource = RecordingSources.Meeting });
        var again = store.Load(() => new TranscriptionPreferences()).Preferences;
        Assert.Equal(LiveTextModes.Draft, again.LiveTextMode);
        Assert.False(again.BoostQuietAudio);
        Assert.Equal(RecordingSources.Meeting, again.LastSource);
    }

    [Fact]
    public void Preferences_migration_backs_up_once_and_is_idempotent()
    {
        var path = this.Paths.TranscriptionPreferencesPath;
        Directory.CreateDirectory(this.root);
        File.WriteAllText(path, """{ "SchemaVersion": 1, "Model": "parakeet", "AudioRetention": "TranscriptOnly" }""");
        var migrations = new Dictionary<int, Func<JsonObject, JsonObject>>
        {
            [1] = o =>
            {
                o["AsrModelId"] = o["Model"]?.GetValue<string>();
                o.Remove("Model");
                return o;
            }
        };
        var store = new TranscriptionPreferencesStore(path, migrations);

        var first = store.Load(() => new TranscriptionPreferences(), targetVersion: 2);
        Assert.Equal("parakeet", first.Preferences.AsrModelId);
        Assert.Equal(Sessions.AudioRetention.TranscriptOnly, first.Preferences.AudioRetention);
        Assert.Equal(2, first.Preferences.SchemaVersion);
        Assert.NotNull(first.BackupPath);
        Assert.Contains("\"Model\"", File.ReadAllText(first.BackupPath!));

        var second = store.Load(() => new TranscriptionPreferences(), targetVersion: 2);
        Assert.Null(second.BackupPath);
        Assert.Equal("parakeet", second.Preferences.AsrModelId);
        Assert.Single(Directory.GetFiles(this.root, "*.bak"));
    }

    [Fact]
    public void Preferences_from_a_newer_version_are_read_only_and_untouched()
    {
        var path = this.Paths.TranscriptionPreferencesPath;
        Directory.CreateDirectory(this.root);
        const string newer = """{ "SchemaVersion": 9, "AsrModelId": "future" }""";
        File.WriteAllText(path, newer);
        var result = new TranscriptionPreferencesStore(path).Load(() => new TranscriptionPreferences());
        Assert.True(result.IsReadOnly);
        Assert.NotNull(result.Warning);
        Assert.Equal(newer, File.ReadAllText(path));
    }

    [Fact]
    public void Corrupt_preferences_are_kept_as_backup_and_reset()
    {
        var path = this.Paths.TranscriptionPreferencesPath;
        Directory.CreateDirectory(this.root);
        File.WriteAllText(path, "{ not json");
        var result = new TranscriptionPreferencesStore(path).Load(() => new TranscriptionPreferences { AsrModelId = "seed" });
        Assert.Equal("seed", result.Preferences.AsrModelId);
        Assert.NotNull(result.Warning);
        Assert.Equal("{ not json", File.ReadAllText(result.BackupPath!));
    }
}
