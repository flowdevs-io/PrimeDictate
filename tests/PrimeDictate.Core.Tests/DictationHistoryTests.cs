using PrimeDictate.Core.Dictation;
using PrimeDictate.Core.Storage;

namespace PrimeDictate.Core.Tests;

public sealed class DictationHistoryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pd-hist-" + Guid.NewGuid().ToString("N"));

    public DictationHistoryTests() => Directory.CreateDirectory(this.root);

    public void Dispose() => Directory.Delete(this.root, recursive: true);

    private static DictationHistoryEntry Entry(string text, string app = "notepad", DictationDeliveryStatus status = DictationDeliveryStatus.Injected, int minutesAgo = 0) =>
        new(Guid.NewGuid(), Guid.NewGuid(), DateTime.UtcNow.AddMinutes(-minutesAgo), text, status, app, app, "notes.txt", null, 2.5, false, null, null);

    [Fact]
    public void Entries_round_trip_newest_first_and_search_target_and_text()
    {
        var store = new DictationHistoryStore(new AppDataPaths(this.root));
        store.Add(Entry("older note", minutesAgo: 5));
        store.Add(Entry("send the invoice", app: "outlook"));
        var all = store.List();
        Assert.Equal(["send the invoice", "older note"], all.Select(e => e.Transcript));
        Assert.Equal("send the invoice", Assert.Single(store.List("outlook")).Transcript);
        Assert.Equal("older note", Assert.Single(store.List("OLDER")).Transcript);
        Assert.Empty(store.List("100%"));
    }

    [Fact]
    public void Rewrites_keep_the_original_and_failures_keep_their_status()
    {
        var store = new DictationHistoryStore(new AppDataPaths(this.root));
        store.Add(Entry("polished", status: DictationDeliveryStatus.SkippedFocusChanged) with { OriginalTranscript = "raw", RewriteSystemPrompt = "p", Error = "focus" });
        var e = Assert.Single(store.List());
        Assert.Equal("raw", e.OriginalTranscript);
        Assert.Equal(DictationDeliveryStatus.SkippedFocusChanged, e.Status);
        Assert.Equal("focus", e.Error);
    }

    [Fact]
    public void Delete_clear_and_the_size_cap_work()
    {
        var store = new DictationHistoryStore(new AppDataPaths(this.root));
        var keep = Entry("keep");
        var drop = Entry("drop");
        store.Add(keep);
        store.Add(drop);
        store.Delete(drop.Id);
        Assert.Equal("keep", Assert.Single(store.List()).Transcript);
        store.Clear();
        Assert.Empty(store.List());
        for (var i = 0; i < DictationHistoryStore.MaxEntries + 5; i++)
        {
            store.Add(Entry("n" + i, minutesAgo: 2000 - i));
        }

        Assert.Equal(DictationHistoryStore.MaxEntries, store.List(limit: 5_000).Count);
    }

    [Fact]
    public void The_wpf_history_file_is_imported_once_and_never_modified()
    {
        var legacy = Path.Combine(this.root, "history.json");
        const string json = """
            [ { "Id": "11111111-1111-1111-1111-111111111111", "ThreadId": "22222222-2222-2222-2222-222222222222",
                "TimestampUtc": "2026-08-01T10:00:00Z", "Transcript": "old dictation", "DeliveryStatus": "CommandExecuted",
                "TargetAppName": "cmd", "AudioDurationSeconds": 3.5, "SendEnterAfterCommit": true } ]
            """;
        File.WriteAllText(legacy, json);
        var store = new DictationHistoryStore(new AppDataPaths(this.root));
        var e = Assert.Single(store.List());
        Assert.Equal("old dictation", e.Transcript);
        Assert.Equal(DictationDeliveryStatus.CommandExecuted, e.Status);
        Assert.True(e.EnterSent);
        Assert.Equal(json, File.ReadAllText(legacy));

        store.Clear();
        Assert.Empty(new DictationHistoryStore(new AppDataPaths(this.root)).List());
    }

    [Fact]
    public void A_broken_legacy_file_is_ignored()
    {
        File.WriteAllText(Path.Combine(this.root, "history.json"), "{{{");
        Assert.Empty(new DictationHistoryStore(new AppDataPaths(this.root)).List());
    }
}
