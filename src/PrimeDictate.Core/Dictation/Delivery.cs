namespace PrimeDictate.Core.Dictation;

/// <summary>The window that had focus when dictation started.</summary>
public interface IForegroundTarget
{
    string DisplayName { get; }

    string? AppName { get; }

    string? WindowTitle { get; }

    /// <summary>True while the same window is still in front.</summary>
    bool IsStillForeground();

    /// <summary>Best-effort return to this window so a late final can still land there.</summary>
    bool TryRestore() => false;

    /// <summary>
    /// Best-effort insertion straight into the control that had focus, without bringing the window forward
    /// (Windows edit controls). False means the caller should restore the window and type instead.
    /// </summary>
    bool TryInjectDirectly(string text) => false;
}

/// <summary>
/// Knows which window is in front so dictation can refuse to type into the wrong one. Where a platform has
/// no such check, <see cref="IsAvailable"/> is false and delivery does not type unless the user opted in.
/// </summary>
public interface IForegroundTargetGuard
{
    bool IsAvailable { get; }

    string? UnavailableReason { get; }

    IForegroundTarget? Capture();
}

/// <summary>Types into whatever has focus. Implementations never touch the clipboard.</summary>
public interface ITextInjector
{
    void TypeText(string text);

    void SendEnter();

    /// <summary>How long the target needs, after the last <see cref="TypeText"/>, before Enter is pressed (see <see cref="EnterTiming"/>).</summary>
    TimeSpan EnterDelay => TimeSpan.Zero;
}

/// <summary>
/// The pause between typed keystrokes and coding-mode Enter. The target takes the keys in after they were sent (a browser
/// queues them for the page), and a chat box that gets Enter before it has caught up, or before its "can send" state has
/// followed the text, keeps the text and does not send (reported in Edge with a few hundred characters typed). Text put
/// straight into an edit control is there when the call returns and needs no pause.
/// </summary>
public static class EnterTiming
{
    public static readonly TimeSpan Base = TimeSpan.FromMilliseconds(120);

    public static readonly TimeSpan PerCharacter = TimeSpan.FromMicroseconds(1_500);

    public static readonly TimeSpan Max = TimeSpan.FromMilliseconds(1_500);

    public static TimeSpan AfterKeystrokes(int characters) =>
        characters <= 0 ? TimeSpan.Zero : TimeSpan.FromTicks(Math.Min(Max.Ticks, Base.Ticks + (PerCharacter.Ticks * characters)));
}

public enum DictationDeliveryStatus
{
    Injected = 0,
    SkippedFocusChanged = 1,
    SkippedNoFocusGuard = 2,
    FailedToInject = 3,
    Discarded = 4,

    /// <summary>Legacy history only: a voice shell command ran.</summary>
    CommandExecuted = 5,

    CommandFailed = 6
}

public sealed record DeliveryResult(DictationDeliveryStatus Status, bool EnterSent, string? Error);

/// <summary>
/// Final-only delivery: one text entry after the foreground guard passes, then optionally Enter (coding mode).
/// Nothing is typed while the user is still talking; live text belongs in the overlay.
/// </summary>
public static class TranscriptDelivery
{

    public static DeliveryResult Deliver(
        string text,
        IForegroundTarget? target,
        IForegroundTargetGuard guard,
        ITextInjector injector,
        DictationOptions options)
    {
        if (!guard.IsAvailable && !options.TypeWithoutFocusGuard)
        {
            return new DeliveryResult(
                DictationDeliveryStatus.SkippedNoFocusGuard,
                false,
                guard.UnavailableReason ?? "This platform cannot verify which window is in front, so PrimeDictate did not type.");
        }

        if (target is not null && !target.IsStillForeground())
        {
            if (!options.ReturnToStartTarget)
            {
                return new DeliveryResult(DictationDeliveryStatus.SkippedFocusChanged, false, "Focused window changed before transcript typing.");
            }

            // WPF order: without coding-mode Enter, put the text into the original target's focused edit control
            // without reactivating it; otherwise (or if that is not possible) restore the window and type.
            if (!options.SendEnterAfterCommit && target.TryInjectDirectly(text))
            {
                return new DeliveryResult(DictationDeliveryStatus.Injected, false, null);
            }

            if (!target.TryRestore())
            {
                return new DeliveryResult(
                    DictationDeliveryStatus.SkippedFocusChanged,
                    false,
                    "Focused window changed and PrimeDictate could not restore the original target.");
            }
        }

        try
        {
            injector.TypeText(text);
        }
        catch (Exception ex)
        {
            return new DeliveryResult(DictationDeliveryStatus.FailedToInject, false, ex.Message);
        }

        if (!options.SendEnterAfterCommit)
        {
            return new DeliveryResult(DictationDeliveryStatus.Injected, false, null);
        }

        // Let the target take the text in, then check again that it is in front: Enter must not go to a window the user
        // switched to while the text was typed or during the pause.
        var delay = injector.EnterDelay;
        if (delay > TimeSpan.Zero)
        {
            Thread.Sleep(delay);
        }

        if (target is not null && !target.IsStillForeground())
        {
            return new DeliveryResult(DictationDeliveryStatus.Injected, false, "Text typed, but Enter was not sent because another window came to the front.");
        }

        try
        {
            injector.SendEnter();
            return new DeliveryResult(DictationDeliveryStatus.Injected, true, null);
        }
        catch (Exception ex)
        {
            // The text is in; report that, and why Enter did not follow.
            return new DeliveryResult(DictationDeliveryStatus.Injected, false, $"Text typed, but Enter failed: {ex.Message}");
        }
    }
}
