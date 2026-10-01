namespace PrimeDictate.Platforms.Input;

/// <summary>The WPF decision order for typing on Windows, with the two routes injected so it can be tested.</summary>
internal static class WindowsTextEntry
{
    public const string FocusedControlRoute = "Text injection used focused edit-control insertion.";
    public const string KeyboardRoute = "Text injection used keyboard simulation fallback.";

    public static string Enter(string text, Func<string, bool> tryFocusedControl, Action<string> sendKeys)
    {
        if (string.IsNullOrEmpty(text))
        {
            return KeyboardRoute;
        }

        if (tryFocusedControl(text))
        {
            return FocusedControlRoute;
        }

        sendKeys(text);
        return KeyboardRoute;
    }
}
