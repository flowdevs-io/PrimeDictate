using PrimeDictate.Core.Dictation;

namespace PrimeDictate.Core.Tests;

public sealed class DictationStatsTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "pd-stats-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(this.dir))
        {
            Directory.Delete(this.dir, true);
        }
    }

    private static DictationHistoryEntry Entry(string text, DictationDeliveryStatus status = DictationDeliveryStatus.Injected, double seconds = 6, DateTime? at = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), at ?? DateTime.UtcNow, text, status, null, null, null, null, seconds, false, null, null);

    private DictationStatsStore Store() => new(Path.Combine(this.dir, "stats.json"));

    [Theory]
    [InlineData("", 0)]
    [InlineData("hello world", 2)]
    [InlineData("don't split it", 3)]
    [InlineData("one, two... 3!", 3)]
    public void Words_are_counted_like_the_wpf_app(string text, int expected) => Assert.Equal(expected, DictationStatsStore.CountWords(text));

    [Fact]
    public void Only_typed_text_counts_and_sessions_track_everything()
    {
        var store = this.Store();
        store.Record(Entry("hello there friend", seconds: 3));
        store.Record(Entry("skipped text", DictationDeliveryStatus.SkippedFocusChanged));
        var state = store.Record(Entry("two words", seconds: 2)).State;
        Assert.Equal(5, state.TotalWords);
        Assert.Equal(3, state.TotalSessions);
        Assert.Equal(2, state.InjectedSessions);
        Assert.Equal(5, state.TotalAudioSeconds);
        Assert.Equal(60.0, state.AverageWordsPerMinute);
        Assert.Single(state.DailyStats);
    }

    [Fact]
    public void Milestone_unlocks_once_and_survives_reload()
    {
        var store = this.Store();
        var text = string.Join(' ', Enumerable.Repeat("word", 999));
        Assert.Empty(store.Record(Entry(text)).NewAchievements);
        var update = store.Record(Entry("two more"));
        Assert.Equal(["words-1000"], update.NewAchievements.Select(a => a.Id));
        Assert.Empty(store.Record(Entry("again")).NewAchievements);
        Assert.Contains("words-1000", this.Store().LoadOrCreate(() => []).UnlockedAchievementIds);
    }

    [Fact]
    public void First_load_is_built_from_history_then_saved()
    {
        var store = this.Store();
        var state = store.LoadOrCreate(() => [Entry("a b c"), Entry("nope", DictationDeliveryStatus.Discarded)]);
        Assert.Equal(3, state.TotalWords);
        Assert.True(File.Exists(store.StatsPath));
        Assert.Equal(3, this.Store().LoadOrCreate(() => throw new InvalidOperationException("must not rebuild")).TotalWords);
    }

    [Fact]
    public void Reads_a_wpf_stats_file()
    {
        Directory.CreateDirectory(this.dir);
        File.WriteAllText(Path.Combine(this.dir, "stats.json"),
            """{"TotalWords":1200,"TotalCharacters":5000,"TotalAudioSeconds":600,"TotalSessions":40,"InjectedSessions":38,"DailyStats":[{"Date":"2026-09-01","Words":300,"Characters":1,"AudioSeconds":1,"Sessions":2}],"UnlockedAchievementIds":["words-1000"]}""");
        var state = this.Store().LoadOrCreate(() => []);
        Assert.Equal(1200, state.TotalWords);
        Assert.Equal(TimeSpan.FromMinutes(1200 / 40.0 - 10), state.TimeSaved(40));
        Assert.Equal(TimeSpan.FromMinutes(1200 / 40.0 - 10), state.TimeSaved(5)); // out of range falls back to 40
    }

    [Fact]
    public void Last_days_fills_gaps_with_zero()
    {
        var state = new DictationStatsState { DailyStats = [new DailyDictationStats { Date = "2026-09-28", Words = 50 }] };
        var days = state.LastDays(new DateTime(2026, 9, 29), 3);
        Assert.Equal(3, days.Count);
        Assert.Equal(50, days[1].Words);
    }

    [Fact]
    public void A_corrupt_stats_file_is_rebuilt_not_fatal()
    {
        Directory.CreateDirectory(this.dir);
        File.WriteAllText(Path.Combine(this.dir, "stats.json"), "{ not json");
        Assert.Equal(2, this.Store().LoadOrCreate(() => [Entry("x y")]).TotalWords);
    }
}
