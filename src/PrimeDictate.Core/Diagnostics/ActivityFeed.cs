namespace PrimeDictate.Core.Diagnostics;

public enum ActivityLevel
{
    Info = 0,
    Warning = 1,
    Error = 2
}

/// <summary>One line in the activity feed. Never carries recognized text; callers pass only reasons and counts.</summary>
public sealed record ActivityEntry(DateTime TimestampUtc, ActivityLevel Level, string Source, string Message, Guid? SessionId = null, int RepeatCount = 1);

/// <summary>How a dictation session ended, without the words: what the Dictation activity view shows per session.</summary>
public enum DictationSessionStatus
{
    Listening = 0,
    Processing = 1,
    Typed = 2,
    NotTyped = 3,
    Error = 4,
    VoiceCommand = 5,
    Discarded = 6
}

/// <summary>One dictation session: time range, latest status and the app it was aimed at. No text.</summary>
public sealed record DictationSessionInfo(Guid Id, DateTime StartedUtc, DateTime EndedUtc, DictationSessionStatus Status, string? AppName);

/// <summary>
/// In-memory, newest-first activity feed fed by <see cref="AppLog"/>: repeated identical lines collapse into one with a
/// count, the global list holds at most 600 entries, and entries and sessions are grouped by dictation session id. It is
/// a diagnostic aid, so by the same rule as the log file nothing recognized is ever put in it.
/// </summary>
public sealed class ActivityFeed
{
    public const int MaxGlobalEntries = 600;
    public const int MaxSessionEntries = 300;
    public const int MaxSessions = 100;

    private readonly object gate = new();
    private readonly List<ActivityEntry> global = [];
    private readonly Dictionary<Guid, List<ActivityEntry>> bySession = [];
    private readonly List<DictationSessionInfo> sessions = []; // newest first

    /// <summary>Raised after any change, on the thread that made it.</summary>
    public event Action? Changed;

    public void Add(ActivityLevel level, string source, string message, Guid? sessionId = null, DateTime? timestampUtc = null)
    {
        var entry = new ActivityEntry(timestampUtc ?? DateTime.UtcNow, level, source, message, sessionId);
        lock (this.gate)
        {
            InsertWithAggregation(this.global, entry, MaxGlobalEntries);
            if (sessionId is { } id)
            {
                if (!this.bySession.TryGetValue(id, out var list))
                {
                    list = [];
                    this.bySession[id] = list;
                }

                InsertWithAggregation(list, entry, MaxSessionEntries);
            }
        }

        this.Changed?.Invoke();
    }

    /// <summary>Creates or updates a session row. A session never moves back in time: the start stays, the end follows.</summary>
    public void SetSession(Guid id, DateTime timestampUtc, DictationSessionStatus status, string? appName = null)
    {
        lock (this.gate)
        {
            var i = this.sessions.FindIndex(s => s.Id == id);
            if (i < 0)
            {
                this.sessions.Insert(0, new DictationSessionInfo(id, timestampUtc, timestampUtc, status, appName));
                while (this.sessions.Count > MaxSessions)
                {
                    var dropped = this.sessions[^1];
                    this.sessions.RemoveAt(this.sessions.Count - 1);
                    this.bySession.Remove(dropped.Id);
                }
            }
            else
            {
                var old = this.sessions[i];
                this.sessions[i] = old with
                {
                    EndedUtc = timestampUtc > old.EndedUtc ? timestampUtc : old.EndedUtc,
                    Status = status,
                    AppName = string.IsNullOrWhiteSpace(appName) ? old.AppName : appName
                };
            }
        }

        this.Changed?.Invoke();
    }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<ActivityEntry> Entries()
    {
        lock (this.gate)
        {
            return [.. this.global];
        }
    }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<ActivityEntry> SessionEntries(Guid id)
    {
        lock (this.gate)
        {
            return this.bySession.TryGetValue(id, out var list) ? [.. list] : [];
        }
    }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<DictationSessionInfo> Sessions()
    {
        lock (this.gate)
        {
            return [.. this.sessions];
        }
    }

    public void Clear()
    {
        lock (this.gate)
        {
            this.global.Clear();
            this.bySession.Clear();
            this.sessions.Clear();
        }

        this.Changed?.Invoke();
    }

    private static void InsertWithAggregation(List<ActivityEntry> target, ActivityEntry entry, int max)
    {
        if (target.Count > 0 && target[0].Level == entry.Level && target[0].SessionId == entry.SessionId
            && string.Equals(target[0].Source, entry.Source, StringComparison.Ordinal)
            && string.Equals(target[0].Message, entry.Message, StringComparison.Ordinal))
        {
            target[0] = target[0] with { TimestampUtc = entry.TimestampUtc, RepeatCount = target[0].RepeatCount + 1 };
            return;
        }

        target.Insert(0, entry);
        if (target.Count > max)
        {
            target.RemoveRange(max, target.Count - max);
        }
    }
}
