using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using PrimeDictate.Core.Dictation;
using PrimeDictate.Platforms.Dictation;

namespace PrimeDictate.Desktop.Dictation;

/// <summary>Everything dictation typed (or refused to type), searchable, so a lost transcript can be copied back.</summary>
public sealed class DictationHistoryWindow : Window
{
    private readonly DictationHost host;
    private readonly TextBox search = new() { PlaceholderText = "Search transcripts, apps and windows" };
    private readonly ListBox list = new();
    private readonly TextBox detail = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MinHeight = 120 };
    private readonly TextBlock summary = new() { Opacity = 0.7 };
    private IReadOnlyList<DictationHistoryEntry> entries = [];

    public DictationHistoryWindow(DictationHost host)
    {
        this.host = host;
        this.Title = "PrimeDictate: dictation history";
        this.Width = 760;
        this.Height = 560;
        this.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var copy = new Button { Content = "Copy transcript" };
        copy.Click += async (_, _) => await this.CopySelectedAsync();
        var delete = new Button { Content = "Delete" };
        delete.Click += (_, _) => this.DeleteSelected();
        var clear = new Button { Content = "Clear all..." };
        clear.Click += (_, _) => this.ConfirmClear();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { copy, delete, clear } };

        var layout = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(this.search, Dock.Top);
        DockPanel.SetDock(this.summary, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        DockPanel.SetDock(this.detail, Dock.Bottom);
        layout.Children.Add(this.search);
        layout.Children.Add(this.summary);
        layout.Children.Add(buttons);
        layout.Children.Add(this.detail);
        layout.Children.Add(this.list);
        this.Content = layout;

        this.search.TextChanged += (_, _) => this.Reload();
        this.list.SelectionChanged += (_, _) => this.ShowDetail();
        this.Opened += (_, _) => this.Reload();
        host.HistoryChanged += this.OnHistoryChanged;
        this.Closed += (_, _) => host.HistoryChanged -= this.OnHistoryChanged;
    }

    private void OnHistoryChanged(DictationHistoryEntry entry) => Avalonia.Threading.Dispatcher.UIThread.Post(this.Reload);

    private void Reload()
    {
        this.entries = this.host.History.List(this.search.Text);
        this.list.ItemsSource = this.entries.Select(Line).ToList();
        this.summary.Text = $"{this.entries.Count:N0} entries";
        this.detail.Text = string.Empty;
    }

    private static string Line(DictationHistoryEntry e)
    {
        var text = e.Transcript.ReplaceLineEndings(" ");
        if (text.Length > 90)
        {
            text = text[..90] + "...";
        }

        var app = string.IsNullOrWhiteSpace(e.TargetAppName) ? "unknown app" : e.TargetAppName;
        return $"{e.TimestampUtc.ToLocalTime():g}  [{Describe(e.Status)}]  {app}: {text}";
    }

    private static string Describe(DictationDeliveryStatus s) => s switch
    {
        DictationDeliveryStatus.Injected => "typed",
        DictationDeliveryStatus.SkippedFocusChanged => "not typed: focus moved",
        DictationDeliveryStatus.SkippedNoFocusGuard => "not typed: no focus check",
        DictationDeliveryStatus.FailedToInject => "typing failed",
        DictationDeliveryStatus.CommandExecuted => "command ran",
        DictationDeliveryStatus.CommandFailed => "command failed",
        _ => "discarded"
    };

    private DictationHistoryEntry? Selected => this.list.SelectedIndex is >= 0 and var i && i < this.entries.Count ? this.entries[i] : null;

    private void ShowDetail()
    {
        if (this.Selected is not { } e)
        {
            return;
        }

        var text = e.Transcript;
        if (e.OriginalTranscript is not null)
        {
            text += $"\n\n--- as spoken ---\n{e.OriginalTranscript}";
        }

        if (e.Error is not null)
        {
            text += $"\n\n{e.Error}";
        }

        this.detail.Text = text;
    }

    private async Task CopySelectedAsync()
    {
        if (this.Selected is { } e && this.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(e.Transcript);
        }
    }

    private void DeleteSelected()
    {
        if (this.Selected is { } e)
        {
            this.host.History.Delete(e.Id);
            this.Reload();
        }
    }

    private void ConfirmClear()
    {
        var yes = new Button { Content = "Delete all history" };
        var no = new Button { Content = "Cancel" };
        var dialog = new Window
        {
            Title = "Clear history",
            Width = 360,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = "Delete every dictation history entry? This cannot be undone.", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { yes, no } }
                }
            }
        };
        yes.Click += (_, _) =>
        {
            this.host.History.Clear();
            this.Reload();
            dialog.Close();
        };
        no.Click += (_, _) => dialog.Close();
        dialog.ShowDialog(this);
    }
}
