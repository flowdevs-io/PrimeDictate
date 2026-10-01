using System.Text;

namespace PrimeDictate.Core.Dictation;

public enum HistoryStatusFilter
{
    All,
    Typed,
    NotTyped
}

public enum HistoryTargetKind
{
    All,
    Known,
    Unknown
}

/// <summary>One choice in the history window's App or Window drop-down.</summary>
public sealed record HistoryTargetOption(HistoryTargetKind Kind, string? Value, string DisplayName)
{
    public override string ToString() => this.DisplayName;
}

/// <summary>
/// The history window's filters, as the WPF window had them: search (every word must appear in the final or the original text), delivery
/// status, target app, and target window within that app. Pure functions over the entries, so they are testable without a window.
/// </summary>
public static class DictationHistoryFilter
{
    public static readonly HistoryTargetOption AllApps = new(HistoryTargetKind.All, null, "All apps");
    public static readonly HistoryTargetOption AllWindows = new(HistoryTargetKind.All, null, "All windows");
    private static readonly HistoryTargetOption UnknownApp = new(HistoryTargetKind.Unknown, null, "Unknown app");
    private static readonly HistoryTargetOption UnknownWindow = new(HistoryTargetKind.Unknown, null, "Unknown window");

    public static string AppName(DictationHistoryEntry entry) => Normalize(entry.TargetAppName) ?? string.Empty;

    /// <summary>The window title, else the one inside the display name ("Title (0x1234)"), else empty.</summary>
    public static string WindowTitle(DictationHistoryEntry entry) =>
        Normalize(entry.TargetWindowTitle) ?? ExtractWindowTitle(entry.TargetDisplayName) ?? string.Empty;

    public static IReadOnlyList<DictationHistoryEntry> Apply(
        IEnumerable<DictationHistoryEntry> entries,
        string? search,
        HistoryStatusFilter status,
        HistoryTargetOption app,
        HistoryTargetOption window)
    {
        var terms = (search ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return entries
            .Where(e => status switch
            {
                HistoryStatusFilter.Typed => e.Status == DictationDeliveryStatus.Injected,
                HistoryStatusFilter.NotTyped => e.Status != DictationDeliveryStatus.Injected,
                _ => true
            })
            .Where(e => terms.Length == 0 || terms.All(t => e.Transcript.Contains(t, StringComparison.OrdinalIgnoreCase)
                || (e.OriginalTranscript?.Contains(t, StringComparison.OrdinalIgnoreCase) ?? false)))
            .Where(e => Matches(AppName(e), app))
            .Where(e => Matches(WindowTitle(e), window))
            .ToList();
    }

    public static IReadOnlyList<HistoryTargetOption> AppOptions(IReadOnlyCollection<DictationHistoryEntry> entries)
    {
        var options = new List<HistoryTargetOption> { AllApps };
        options.AddRange(Known(entries.Select(AppName)));
        if (entries.Any(e => AppName(e).Length == 0))
        {
            options.Add(UnknownApp);
        }

        return options;
    }

    /// <summary>The windows of the entries that belong to the chosen app.</summary>
    public static IReadOnlyList<HistoryTargetOption> WindowOptions(IReadOnlyCollection<DictationHistoryEntry> entries, HistoryTargetOption app)
    {
        var ofApp = entries.Where(e => Matches(AppName(e), app)).ToList();
        var options = new List<HistoryTargetOption> { AllWindows };
        options.AddRange(Known(ofApp.Select(WindowTitle)));
        if (ofApp.Any(e => WindowTitle(e).Length == 0))
        {
            options.Add(UnknownWindow);
        }

        return options;
    }

    /// <summary>The text "Copy details" puts on the clipboard: when and where it went, the result and, if it was rewritten, the original and the prompt.</summary>
    public static string DetailsText(DictationHistoryEntry e)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Timestamp (UTC): {e.TimestampUtc:O}");
        sb.AppendLine($"Thread: {e.SessionId}");
        sb.AppendLine($"Delivery: {DeliveryDisplay(e.Status)}");
        sb.AppendLine($"Target app: {(AppName(e).Length > 0 ? AppName(e) : "Unknown app")}");
        sb.AppendLine($"Target window: {(WindowTitle(e).Length > 0 ? WindowTitle(e) : "Unknown window")}");
        sb.AppendLine($"Audio seconds: {e.AudioSeconds:N1}");
        if (!string.IsNullOrWhiteSpace(e.Error))
        {
            sb.AppendLine($"Error: {e.Error}");
        }

        if (!string.IsNullOrWhiteSpace(e.OriginalTranscript))
        {
            sb.AppendLine();
            sb.AppendLine("Original Transcript:");
            sb.AppendLine(e.OriginalTranscript);
            sb.AppendLine();
            sb.AppendLine("Ollama System Prompt:");
            sb.AppendLine(e.RewriteSystemPrompt);
            sb.AppendLine();
            sb.AppendLine("Final Injected Transcript:");
        }

        sb.AppendLine();
        sb.AppendLine(e.Transcript);
        return sb.ToString();
    }

    /// <summary>The delivery wording of the WPF history window (it never printed the enum name); the two statuses only this app has are worded the same way.</summary>
    public static string DeliveryDisplay(DictationDeliveryStatus status) => status switch
    {
        DictationDeliveryStatus.Injected => "Typed into app",
        DictationDeliveryStatus.SkippedFocusChanged => "Skipped — focus changed",
        DictationDeliveryStatus.SkippedNoFocusGuard => "Skipped — no focus check",
        DictationDeliveryStatus.FailedToInject => "Failed to type",
        DictationDeliveryStatus.Discarded => "Discarded",
        DictationDeliveryStatus.CommandExecuted => "Command ran",
        DictationDeliveryStatus.CommandFailed => "Command failed",
        _ => status.ToString()
    };

    private static IEnumerable<HistoryTargetOption> Known(IEnumerable<string> values) =>
        values.Where(v => v.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
            .Select(v => new HistoryTargetOption(HistoryTargetKind.Known, v, v));

    private static bool Matches(string value, HistoryTargetOption option) => option.Kind switch
    {
        HistoryTargetKind.Known => string.Equals(value, option.Value, StringComparison.OrdinalIgnoreCase),
        HistoryTargetKind.Unknown => value.Length == 0,
        _ => true
    };

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? ExtractWindowTitle(string? targetDisplayName)
    {
        var text = Normalize(targetDisplayName);
        if (text is null)
        {
            return null;
        }

        var handle = text.LastIndexOf(" (0x", StringComparison.OrdinalIgnoreCase);
        if (handle > 0 && text.EndsWith(')'))
        {
            return text[..handle].Trim();
        }

        return text.StartsWith("window 0x", StringComparison.OrdinalIgnoreCase) ? null : text;
    }
}
