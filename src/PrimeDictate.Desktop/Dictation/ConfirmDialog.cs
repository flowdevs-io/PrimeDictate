using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace PrimeDictate.Desktop.Dictation;

/// <summary>A small Yes / No question (Avalonia has no message box). The default button is the safe one.</summary>
public static class ConfirmDialog
{
    public static async Task<bool> AskAsync(Window owner, string title, string message, string yes = "Yes", string no = "No")
    {
        var result = false;
        var dialog = new Window
        {
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var yesButton = new Button { Content = yes, MinWidth = 90 };
        var noButton = new Button { Content = no, MinWidth = 90, IsDefault = true };
        yesButton.Click += (_, _) =>
        {
            result = true;
            dialog.Close();
        };
        noButton.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { yesButton, noButton } }
            }
        };
        await dialog.ShowDialog(owner);
        return result;
    }
}
