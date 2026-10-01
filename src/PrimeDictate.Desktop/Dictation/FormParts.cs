using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace PrimeDictate.Desktop.Dictation;

/// <summary>Small building blocks shared by the settings controls and the first-run wizard.</summary>
internal static class FormParts
{
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.FromRgb(224, 82, 82));

    public static CheckBox Check(string text) => new() { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap } };

    public static Control Row(string label, Control input)
    {
        // Under its label, not centered in the window.
        input.HorizontalAlignment = HorizontalAlignment.Left;
        return new StackPanel { Spacing = 4, Children = { new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }, input } };
    }

    public static TextBlock Note(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.7 };

    /// <summary>A red line that stays hidden until <see cref="Show"/> gives it a message.</summary>
    public static TextBlock ErrorLine() => new() { Foreground = ErrorBrush, TextWrapping = TextWrapping.Wrap, IsVisible = false, FontWeight = FontWeight.SemiBold };

    public static void Show(TextBlock line, string? message)
    {
        line.Text = message ?? string.Empty;
        line.IsVisible = !string.IsNullOrEmpty(message);
    }

    public static string Normalize(string? path) =>
        string.IsNullOrWhiteSpace(path) ? string.Empty : System.IO.Path.GetFullPath(path.Trim().Trim('"')).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
}
