using PrimeDictate.Core.Diagnostics;

namespace PrimeDictate.Core.Tests;

public sealed class ActivityFeedTests
{
    [Fact]
    public void Identical_consecutive_lines_collapse_into_one_with_a_count()
    {
        var feed = new ActivityFeed();
        feed.Add(ActivityLevel.Warning, "mic", "gone");
        feed.Add(ActivityLevel.Warning, "mic", "gone");
        feed.Add(ActivityLevel.Warning, "mic", "gone");
        var only = Assert.Single(feed.Entries());
        Assert.Equal(3, only.RepeatCount);
        Assert.EndsWith("(x3)", ActivityText.Message(only));

        feed.Add(ActivityLevel.Error, "mic", "gone");
        feed.Add(ActivityLevel.Warning, "mic", "gone");
        Assert.Equal(3, feed.Entries().Count);
        Assert.Equal(1, feed.Entries()[0].RepeatCount);
    }

    [Fact]
    public void Aggregation_does_not_merge_across_sessions()
    {
        var feed = new ActivityFeed();
        feed.Add(ActivityLevel.Info, "dictation", "Listening started.", Guid.NewGuid());
        feed.Add(ActivityLevel.Info, "dictation", "Listening started.", Guid.NewGuid());
        Assert.Equal(2, feed.Entries().Count);
    }

    [Fact]
    public void Global_list_is_capped_at_600_dropping_the_oldest()
    {
        var feed = new ActivityFeed();
        for (var i = 0; i < 650; i++)
        {
            feed.Add(ActivityLevel.Info, "t", $"line {i}");
        }

        var entries = feed.Entries();
        Assert.Equal(600, entries.Count);
        Assert.Equal("line 649", entries[0].Message);
        Assert.Equal("line 50", entries[^1].Message);
    }

    [Fact]
    public void Entries_and_sessions_are_grouped_by_session_id()
    {
        var feed = new ActivityFeed();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var t0 = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        feed.SetSession(a, t0, DictationSessionStatus.Listening, "Notepad");
        feed.Add(ActivityLevel.Info, "dictation", "a1", a);
        feed.SetSession(b, t0.AddMinutes(1), DictationSessionStatus.Listening);
        feed.Add(ActivityLevel.Info, "dictation", "b1", b);
        feed.Add(ActivityLevel.Error, "dictation", "a2", a);
        feed.SetSession(a, t0.AddSeconds(30), DictationSessionStatus.Typed);
        feed.Add(ActivityLevel.Info, "app", "no session");

        Assert.Equal(["a2", "a1"], feed.SessionEntries(a).Select(e => e.Message));
        Assert.Equal(["b1"], feed.SessionEntries(b).Select(e => e.Message));
        Assert.Empty(feed.SessionEntries(Guid.NewGuid()));
        Assert.Equal(4, feed.Entries().Count);

        var sessions = feed.Sessions();
        Assert.Equal([b, a], sessions.Select(s => s.Id));
        var sa = sessions[1];
        Assert.Equal(DictationSessionStatus.Typed, sa.Status);
        Assert.Equal(t0, sa.StartedUtc);
        Assert.Equal(t0.AddSeconds(30), sa.EndedUtc);
        Assert.Equal("Notepad", sa.AppName);
    }

    [Fact]
    public void Old_sessions_are_dropped_with_their_logs()
    {
        var feed = new ActivityFeed();
        var first = Guid.NewGuid();
        feed.SetSession(first, DateTime.UtcNow, DictationSessionStatus.Typed);
        feed.Add(ActivityLevel.Info, "d", "x", first);
        for (var i = 0; i < ActivityFeed.MaxSessions; i++)
        {
            feed.SetSession(Guid.NewGuid(), DateTime.UtcNow, DictationSessionStatus.Typed);
        }

        Assert.Equal(ActivityFeed.MaxSessions, feed.Sessions().Count);
        Assert.DoesNotContain(feed.Sessions(), s => s.Id == first);
        Assert.Empty(feed.SessionEntries(first));
    }

    [Fact]
    public void Copy_text_is_oldest_first_and_session_rows_show_range_status_and_app_only()
    {
        var feed = new ActivityFeed();
        feed.Add(ActivityLevel.Info, "a", "first");
        feed.Add(ActivityLevel.Error, "a", "second");
        var lines = ActivityText.Join(feed.Entries()).Split(Environment.NewLine);
        Assert.Contains("first", lines[0]);
        Assert.Contains("ERR", lines[1]);

        var t = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var row = ActivityText.Session(new DictationSessionInfo(Guid.NewGuid(), t, t.AddSeconds(5), DictationSessionStatus.NotTyped, "Code"));
        Assert.Contains("Not typed", row);
        Assert.Contains("Code", row);
    }

    [Fact]
    public void AppLog_events_and_faults_reach_the_feed_with_levels()
    {
        var id = Guid.NewGuid();
        var marker = $"marker-{id}";
        AppLog.Directory = Path.Combine(Path.GetTempPath(), "pd-activity-" + id);
        AppLog.Event("test", marker, ActivityLevel.Warning, id);
        AppLog.Fault("test", new InvalidOperationException(marker), id);
        var log = AppLog.Feed.SessionEntries(id);
        Assert.Equal([ActivityLevel.Error, ActivityLevel.Warning], log.Select(e => e.Level));
    }
}
