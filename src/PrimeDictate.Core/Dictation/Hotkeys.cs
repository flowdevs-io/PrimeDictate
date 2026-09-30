namespace PrimeDictate.Core.Dictation;

public enum HotkeyAction
{
    ToggleDictation = 0,
    EmergencyStop = 1,
    ShowHistory = 2
}

/// <summary>
/// A key plus modifiers. <see cref="Key"/> holds the SharpHook key-code name (for example "VcSpace"), the same string the
/// WPF app writes to settings.json, so existing hotkey settings are read unchanged.
/// </summary>
public sealed record HotkeyGesture(string Key, bool Ctrl, bool Shift, bool Alt)
{
    public static HotkeyGesture Default { get; } = new("VcSpace", true, true, false);

    public static HotkeyGesture DefaultStop { get; } = new("VcEnter", true, true, false);

    public static HotkeyGesture DefaultHistory { get; } = new("VcH", true, true, false);

    private static readonly HashSet<string> ModifierKeys =
        ["VcLeftControl", "VcRightControl", "VcLeftShift", "VcRightShift", "VcLeftAlt", "VcRightAlt"];

    public bool IsValid(out string error)
    {
        if (!this.Ctrl && !this.Shift && !this.Alt)
        {
            error = "Hotkey must include at least one modifier key (Ctrl, Shift, or Alt).";
            return false;
        }

        if (string.IsNullOrWhiteSpace(this.Key) || ModifierKeys.Contains(this.Key))
        {
            error = "Hotkey key cannot be a modifier key.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    public override string ToString()
    {
        var parts = new List<string>(4);
        if (this.Ctrl)
        {
            parts.Add("Ctrl");
        }

        if (this.Shift)
        {
            parts.Add("Shift");
        }

        if (this.Alt)
        {
            parts.Add("Alt");
        }

        parts.Add(this.Key switch
        {
            "VcSpace" => "Space",
            "VcEnter" => "Enter",
            "VcEscape" => "Esc",
            var k when k.StartsWith("Vc", StringComparison.Ordinal) => k[2..],
            var k => k
        });
        return string.Join("+", parts);
    }
}

public interface IHotkeySource : IDisposable
{
    event Action<HotkeyAction>? Pressed;

    /// <summary>Why global hotkeys cannot work here (for example Wayland), or null when they can.</summary>
    string? UnavailableReason { get; }

    void SetBindings(IReadOnlyDictionary<HotkeyAction, HotkeyGesture> bindings);

    /// <summary>Starts listening. Completes when the source is disposed.</summary>
    Task RunAsync();
}

/// <summary>
/// Chooses the action for a key press. Like the WPF app, extra modifiers do not block a match, and when bindings
/// overlap the stop hotkey wins over history, which wins over dictation, so an emergency stop is never shadowed.
/// </summary>
public static class HotkeyMatcher
{
    public static HotkeyAction? Match(string key, bool ctrl, bool shift, bool alt, IReadOnlyDictionary<HotkeyAction, HotkeyGesture> bindings)
    {
        foreach (var action in new[] { HotkeyAction.EmergencyStop, HotkeyAction.ShowHistory, HotkeyAction.ToggleDictation })
        {
            if (bindings.TryGetValue(action, out var gesture) &&
                gesture.Key == key &&
                (!gesture.Ctrl || ctrl) &&
                (!gesture.Shift || shift) &&
                (!gesture.Alt || alt))
            {
                return action;
            }
        }

        return null;
    }
}
