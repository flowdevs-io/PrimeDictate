namespace PrimeDictate.Core.Dictation;

/// <summary>What the overlay is showing.</summary>
public enum OverlayPhase
{
    Ready,
    WakeListening,
    Listening,
    Processing
}

/// <summary>
/// The overlay's and the tray's wording and show/hide rules, ported from the WPF app (<c>TranscriptionOverlayWindow</c>, <c>App.ShouldPersistOverlay</c>,
/// the tray tooltips), kept free of UI types so they can be tested. Nothing here ever receives recognized text except <see cref="IsCopyable"/>,
/// which only inspects it.
/// </summary>
public static class OverlayRules
{
    /// <summary>The model family as the WPF header and tooltips name it.</summary>
    public static string BackendLabel(LegacyBackend backend) => backend switch
    {
        LegacyBackend.QualcommQnn => "Qualcomm AI Hub Whisper QNN",
        LegacyBackend.Moonshine => "Moonshine ONNX",
        LegacyBackend.Parakeet => "Parakeet ONNX",
        LegacyBackend.WhisperNet => "Whisper.net (GGML)",
        _ => "Whisper ONNX"
    };

    /// <summary>"Listening [Whisper.net (GGML)]", "Processing [..]", "Wake listening [..]", "Ready [..]".</summary>
    public static string Header(OverlayPhase phase, string backend) => phase switch
    {
        OverlayPhase.Listening => $"Listening [{backend}]",
        OverlayPhase.Processing => $"Processing [{backend}]",
        OverlayPhase.WakeListening => $"Wake listening [{backend}]",
        _ => $"Ready [{backend}]"
    };

    /// <summary>What the transcript box says when there is no transcript yet.</summary>
    public static string Placeholder(OverlayPhase phase, string backend) => phase switch
    {
        OverlayPhase.Listening or OverlayPhase.Processing => $"Listening with {backend}...",
        OverlayPhase.WakeListening => "Waiting for wake phrase...",
        _ => "Waiting for hotkey..."
    };

    /// <summary>The Copy button works only when there is real text: not empty and not one of the placeholders.</summary>
    public static bool IsCopyable(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return text != "Waiting for hotkey..." && text != "Waiting for wake phrase..." && text != "Listening..." && !text.StartsWith("Listening with ", StringComparison.Ordinal);
    }

    /// <summary>"mm:ss", minutes not wrapping at an hour (the WPF timer showed total minutes).</summary>
    public static string Elapsed(TimeSpan elapsed) =>
        $"{(int)Math.Max(0, elapsed.TotalMinutes):D2}:{Math.Max(0, elapsed.Seconds):D2}";

    /// <summary>
    /// Whether the overlay is on screen. The WPF rule: a compact microphone stays on screen all the time, and so does a pinned overlay
    /// (<c>ShouldPersistOverlay</c>); otherwise it shows while dictating, for a notice or the last words. Closing it hides it until
    /// the next dictation. <paramref name="hideCompactWhenIdle"/> is the new opt-out for the compact microphone's always-on rule.
    /// </summary>
    public static bool ShouldShow(bool dismissed, bool active, bool lingering, bool pinned, OverlayStyle configuredStyle, bool hideCompactWhenIdle)
    {
        if (dismissed)
        {
            return false;
        }

        var staysOnScreen = pinned || (configuredStyle == OverlayStyle.CompactMicrophone && !hideCompactWhenIdle);
        return active || lingering || staysOnScreen;
    }

    /// <summary>
    /// The icon's hover text, worded exactly as the WPF tray ("PrimeDictate - Processing [..]", "PrimeDictate - Listening [.., Exclusive]").
    /// Windows shows at most 63 characters, so the wake phrase is dropped when it would not fit. <paramref name="micAccess"/> is "Exclusive"
    /// or "Shared" while dictating, else null.
    /// </summary>
    public static string TrayTooltip(
        OverlayPhase phase,
        string backend,
        bool needsAttention,
        bool wakeFailed,
        string? meetingSource,
        string wakePhrase,
        string? micAccess = null)
    {
        if (phase == OverlayPhase.Listening)
        {
            return micAccess is { Length: > 0 }
                ? $"PrimeDictate - Listening [{backend}, {micAccess}]"
                : $"PrimeDictate - Listening [{backend}]";
        }

        if (phase == OverlayPhase.Processing)
        {
            return $"PrimeDictate - Processing [{backend}]";
        }

        if (meetingSource is not null)
        {
            return $"PrimeDictate - Recording meeting ({meetingSource})";
        }

        if (needsAttention || wakeFailed)
        {
            return wakeFailed ? "PrimeDictate - Wake listening failed" : "PrimeDictate - Needs attention";
        }

        if (phase == OverlayPhase.WakeListening)
        {
            var phrase = string.IsNullOrWhiteSpace(wakePhrase) ? WakePhrase.Default : wakePhrase.Trim();
            var text = $"PrimeDictate - Wake listening ({phrase})";
            return text.Length <= 63 ? text : "PrimeDictate - Wake listening";
        }

        return $"PrimeDictate - Ready [{backend}]";
    }
}

/// <summary>How long the tray shows its "needs attention" icon after an error, and the rule for it.</summary>
public static class TrayAttention
{
    public static readonly TimeSpan Hold = TimeSpan.FromSeconds(10);

    public static bool IsActive(DateTime attentionUntilUtc, DateTime nowUtc, bool wakeFailed) => nowUtc <= attentionUntilUtc || wakeFailed;
}
