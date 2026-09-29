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
}

public enum DictationDeliveryStatus
{
    Injected = 0,
    SkippedFocusChanged = 1,
    SkippedNoFocusGuard = 2,
    FailedToInject = 3,
    Discarded = 4
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
