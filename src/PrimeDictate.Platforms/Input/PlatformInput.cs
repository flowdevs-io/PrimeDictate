using PrimeDictate.Core.Dictation;

namespace PrimeDictate.Platforms.Input;

public static class PlatformInput
{
    public static IForegroundTargetGuard CreateForegroundGuard() =>
        OperatingSystem.IsWindows()
            ? new WindowsForegroundGuard()
            : new UnavailableForegroundGuard(
                OperatingSystem.IsMacOS()
                    ? "macOS foreground-app checking is not built yet, so dictation will not type. Enable 'Type without focus check' in Settings to override."
                    : "Linux foreground-window checking is not built yet, so dictation will not type. Enable 'Type without focus check' in Settings to override.");
}

/// <summary>No foreground check on this platform. Delivery then declines to type unless the user opts in.</summary>
public sealed class UnavailableForegroundGuard(string reason) : IForegroundTargetGuard
{
    public bool IsAvailable => false;

    public string? UnavailableReason => reason;

    public IForegroundTarget? Capture() => null;
}
