using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using PrimeDictate.Core.Storage;

namespace PrimeDictate.Core.Dictation;

/// <summary>Lifetime dictation totals. Same JSON shape and file (<c>stats.json</c>) as the WPF app, so the totals carry over.</summary>
public sealed class DictationStatsState
{
    public long TotalWords { get; set; }

    public long TotalCharacters { get; set; }

    public double TotalAudioSeconds { get; set; }

    public int TotalSessions { get; set; }

    public int InjectedSessions { get; set; }

    public DateTime? FirstCommitUtc { get; set; }

    public DateTime? LastCommitUtc { get; set; }

    public List<DailyDictationStats> DailyStats { get; set; } = [];

    public HashSet<string> UnlockedAchievementIds { get; set; } = [];

    public double AverageWordsPerMinute => this.TotalAudioSeconds > 0 ? this.TotalWords / (this.TotalAudioSeconds / 60.0) : 0;

    /// <summary>Time saved against typing the same words at <paramref name="baselineWpm"/> (20 to 120, else 40), net of speaking time.</summary>
    public TimeSpan TimeSaved(int baselineWpm)
    {
        var baseline = DictationStatsStore.NormalizeBaselineWpm(baselineWpm);
        var saved = TimeSpan.FromMinutes(this.TotalWords / (double)baseline) - TimeSpan.FromSeconds(this.TotalAudioSeconds);
        return saved < TimeSpan.Zero ? TimeSpan.Zero : saved;
    }

    /// <summary>The last <paramref name="days"/> days ending today, oldest first, with zero-word days included.</summary>
    public IReadOnlyList<(DateTime Day, long Words)> LastDays(DateTime today, int days = 14)
    {
        var byDate = this.DailyStats
            .Where(d => !string.IsNullOrWhiteSpace(d.Date))
            .GroupBy(d => d.Date, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Sum(d => d.Words), StringComparer.Ordinal);
        return Enumerable.Range(0, days)
            .Select(offset => today.Date.AddDays(offset - (days - 1)))
            .Select(day => (day, byDate.GetValueOrDefault(day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))))
            .ToList();
    }
}

public sealed class DailyDictationStats
{
    public string Date { get; set; } = string.Empty;

    public long Words { get; set; }

    public long Characters { get; set; }

    public double AudioSeconds { get; set; }

    public int Sessions { get; set; }
}

public sealed record DictationAchievement(string Id, string Title, string Message, long WordThreshold);

public sealed record DictationStatsUpdate(DictationStatsState State, IReadOnlyList<DictationAchievement> NewAchievements);

public sealed class DictationStatsStore(string path)
{
    public const int DefaultBaselineWpm = 40;
    public const int MinBaselineWpm = 20;
    public const int MaxBaselineWpm = 120;

    /// <summary>The typing speed to compare against: the value when it is 20 to 120, else the default of 40 (as the WPF app did).</summary>
    public static int NormalizeBaselineWpm(int wpm) => wpm is >= MinBaselineWpm and <= MaxBaselineWpm ? wpm : DefaultBaselineWpm;
    private const int MaxDailyBuckets = 370;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static IReadOnlyList<DictationAchievement> Achievements { get; } =
    [
        new("words-1000", "First 1,000 words", "PrimeDictate has typed your first 1,000 words.", 1_000),
        new("words-10000", "10,000-word flow", "You have dictated 10,000 words locally.", 10_000),
        new("words-100000", "100,000-word milestone", "PrimeDictate has helped you type 100,000 words.", 100_000),
        new("words-1000000", "Million-word engine", "PrimeDictate has helped you type 1,000,000 words.", 1_000_000)
    ];

    private readonly object sync = new();

    public DictationStatsStore(AppDataPaths paths)
        : this(Path.Combine(paths.Root, "stats.json"))
    {
    }

    public string StatsPath { get; } = path;

    /// <summary>Loads the totals, or, when there is no stats file yet, builds them from history and saves them.</summary>
    public DictationStatsState LoadOrCreate(Func<IEnumerable<DictationHistoryEntry>> history)
    {
        lock (this.sync)
        {
            if (this.TryRead() is { } existing)
            {
                return existing;
            }

            var state = new DictationStatsState();
            foreach (var entry in history().OrderBy(e => e.TimestampUtc))
            {
                Apply(state, entry);
            }

            Unlock(state, 0);
            this.Save(state);
            return state;
        }
    }

    public DictationStatsUpdate Record(DictationHistoryEntry entry)
    {
        lock (this.sync)
        {
            var state = this.TryRead() ?? new DictationStatsState();
            var before = state.TotalWords;
            Apply(state, entry);
            var unlocked = Unlock(state, before);
            this.Save(state);
            return new DictationStatsUpdate(state, unlocked);
        }
    }

    /// <summary>Counts runs of letters and digits; an apostrophe inside a word does not split it.</summary>
    public static int CountWords(string text)
    {
        var words = 0;
        var inWord = false;
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                if (!inWord)
                {
                    words++;
                    inWord = true;
                }
            }
            else if (!(c == '\'' && inWord))
            {
                inWord = false;
            }
        }

        return words;
    }

    private DictationStatsState? TryRead()
    {
        try
        {
            return File.Exists(this.StatsPath) ? JsonSerializer.Deserialize<DictationStatsState>(File.ReadAllText(this.StatsPath), JsonOptions) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void Apply(DictationStatsState state, DictationHistoryEntry entry)
    {
        state.TotalSessions++;
        state.FirstCommitUtc ??= entry.TimestampUtc;
        state.LastCommitUtc = entry.TimestampUtc;
        if (entry.Status != DictationDeliveryStatus.Injected)
        {
            return;
        }

        var words = CountWords(entry.Transcript);
        if (words == 0)
        {
            return;
        }

        var characters = entry.Transcript.Count(c => !char.IsWhiteSpace(c));
        var seconds = Math.Max(0, entry.AudioSeconds);
        state.TotalWords += words;
        state.TotalCharacters += characters;
        state.TotalAudioSeconds += seconds;
        state.InjectedSessions++;

        var date = entry.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var day = state.DailyStats.FirstOrDefault(d => d.Date == date);
        if (day is null)
        {
            day = new DailyDictationStats { Date = date };
            state.DailyStats.Add(day);
            state.DailyStats.Sort((a, b) => string.CompareOrdinal(a.Date, b.Date));
            if (state.DailyStats.Count > MaxDailyBuckets)
            {
                state.DailyStats.RemoveRange(0, state.DailyStats.Count - MaxDailyBuckets);
            }
        }

        day.Words += words;
        day.Characters += characters;
        day.AudioSeconds += seconds;
        day.Sessions++;
    }

    private static List<DictationAchievement> Unlock(DictationStatsState state, long before)
    {
        var unlocked = new List<DictationAchievement>();
        foreach (var a in Achievements)
        {
            if (before < a.WordThreshold && state.TotalWords >= a.WordThreshold && state.UnlockedAchievementIds.Add(a.Id))
            {
                unlocked.Add(a);
            }
        }

        return unlocked;
    }

    private void Save(DictationStatsState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(this.StatsPath)!);
        var temp = this.StatsPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state, JsonOptions));
        File.Move(temp, this.StatsPath, overwrite: true);
    }
}
