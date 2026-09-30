using PrimeDictate.Core.Dictation;
using SharpHook;
using SharpHook.Data;

namespace PrimeDictate.Platforms.Input;

/// <summary>
/// Global hotkeys through SharpHook (libuiohook): Windows, macOS (needs Accessibility permission) and X11/XWayland.
/// Wayland has no global keyboard hook, so it is reported instead of failing silently.
/// </summary>
/// <remarks>
/// The hook callback runs on SharpHook's thread. It only matches and raises <see cref="Pressed"/>; handlers offload work.
/// A matched key press is suppressed so the shortcut does not also reach the focused app.
/// </remarks>
public sealed class SharpHookHotkeySource : IHotkeySource
{
    private readonly object sync = new();
    private readonly IGlobalHook hook = new SimpleGlobalHook(GlobalHookType.Keyboard);
    private IReadOnlyDictionary<HotkeyAction, HotkeyGesture> bindings = new Dictionary<HotkeyAction, HotkeyGesture>();

    public SharpHookHotkeySource()
    {
        this.hook.KeyPressed += this.OnKeyPressed;
    }

    public event Action<HotkeyAction>? Pressed;

    public string? UnavailableReason => DetectUnavailableReason();

    public void SetBindings(IReadOnlyDictionary<HotkeyAction, HotkeyGesture> bindings)
    {
        lock (this.sync)
        {
            this.bindings = new Dictionary<HotkeyAction, HotkeyGesture>(bindings);
        }
    }

    public Task RunAsync() => this.hook.RunAsync();

    public void Dispose()
    {
        this.hook.KeyPressed -= this.OnKeyPressed;
        this.hook.Dispose();
    }

    public static string? DetectUnavailableReason(Func<string, string?>? env = null, bool? isLinux = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        if (!(isLinux ?? OperatingSystem.IsLinux()))
        {
            return null;
        }

        var wayland = string.Equals(env("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(env("WAYLAND_DISPLAY"));
        // XWayland exposes DISPLAY, and the hook then sees keys typed into X11 apps only.
        if (wayland && string.IsNullOrEmpty(env("DISPLAY")))
        {
            return "Wayland does not allow global hotkeys. Log in with an X11 session, or start dictation from the tray.";
        }

        return string.IsNullOrEmpty(env("DISPLAY")) && string.IsNullOrEmpty(env("WAYLAND_DISPLAY"))
            ? "No display server was found for global hotkeys."
            : null;
    }

    private void OnKeyPressed(object? sender, KeyboardHookEventArgs args)
    {
        IReadOnlyDictionary<HotkeyAction, HotkeyGesture> current;
        lock (this.sync)
        {
            current = this.bindings;
        }

        var mask = args.RawEvent.Mask;
        var action = HotkeyMatcher.Match(args.Data.KeyCode.ToString(), mask.HasCtrl(), mask.HasShift(), mask.HasAlt(), current);
        if (action is null)
        {
            return;
        }

        args.SuppressEvent = true;
        this.Pressed?.Invoke(action.Value);
    }
}
