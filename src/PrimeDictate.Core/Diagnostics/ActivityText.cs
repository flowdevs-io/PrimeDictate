namespace PrimeDictate.Core.Diagnostics;

/// <summary>Plain-text rendering shared by the activity view and its Copy buttons.</summary>
public static class ActivityText
{
    public static string Level(ActivityLevel level) => level switch { ActivityLevel.Error => "ERR", ActivityLevel.Warning => "WRN", _ => "INF" };

    public static string Status(DictationSessionStatus status) => status switch
    {
        DictationSessionStatus.Listening => "Listening",
        DictationSessionStatus.Processing => "Processing",
        DictationSessionStatus.Typed => "Typed",
        DictationSessionStatus.NotTyped => "Not typed",
        DictationSessionStatus.Error => "Error",
        DictationSessionStatus.VoiceCommand => "Voice command",
        _ => "Discarded"
    };

    public static string Message(ActivityEntry e) => e.RepeatCount > 1 ? $"{e.Message} (x{e.RepeatCount})" : e.Message;

    public static string Line(ActivityEntry e) =>
        $"{e.TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} {Level(e.Level)} [{e.Source}] {Message(e)}";

    public static string Session(DictationSessionInfo s)
    {
        var start = s.StartedUtc.ToLocalTime();
        var end = s.EndedUtc.ToLocalTime();
        var range = end - start < TimeSpan.FromSeconds(1) ? $"{start:HH:mm:ss}" : $"{start:HH:mm:ss}-{end:HH:mm:ss}";
        return string.IsNullOrWhiteSpace(s.AppName) ? $"{range}  {Status(s.Status)}" : $"{range}  {Status(s.Status)}  {s.AppName}";
    }

    /// <summary>Oldest first, one line each, for pasting into a bug report.</summary>
    public static string Join(IEnumerable<ActivityEntry> newestFirst) =>
        string.Join(Environment.NewLine, newestFirst.Reverse().Select(Line));
}
