using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;

namespace PrimeDictate.Desktop.Updates;

/// <summary>A small message box for the updater: one OK button, or a two-button question.</summary>
internal static class UpdateDialog
{
    public static Task ShowAsync(string title, string message) => ShowCoreAsync(title, message, "OK", null);

    /// <summary>True when the user picked <paramref name="yes"/>.</summary>
    public static Task<bool> AskAsync(string title, string message, string yes, string no) => ShowCoreAsync(title, message, yes, no);

    private static async Task<bool> ShowCoreAsync(string title, string message, string yes, string? no)
    {
        var result = false;
        var dialog = new Window
        {
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ShowInTaskbar = true
        };

        var yesButton = new Button { Content = yes, IsDefault = true, MinWidth = 96 };
        yesButton.Click += (_, _) =>
        {
            result = true;
            dialog.Close();
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        buttons.Children.Add(yesButton);
        if (no is not null)
        {
            var noButton = new Button { Content = no, IsCancel = true, MinWidth = 96 };
            noButton.Click += (_, _) => dialog.Close();
            buttons.Children.Add(noButton);
        }

        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 16 };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        panel.Children.Add(buttons);
        dialog.Content = panel;

        var owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner is { IsVisible: true })
        {
            await dialog.ShowDialog(owner);
        }
        else
        {
            // The window may be hidden in the tray: show the dialog on its own and wait for it to close.
            var closed = new TaskCompletionSource();
            dialog.Closed += (_, _) => closed.TrySetResult();
            dialog.Show();
            dialog.Activate();
            await closed.Task;
        }

        return result;
    }
}
