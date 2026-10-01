using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using PrimeDictate.Core.Dictation;
using PrimeDictate.Platforms.Dictation;

namespace PrimeDictate.Desktop.Dictation;

/// <summary>
/// Everything dictation typed (or refused to type), so a lost transcript can be copied back. Search (every word, in the final or the original
/// text), a delivery filter, an app filter and a window filter, as in the WPF history window; the filters themselves are
/// <see cref="DictationHistoryFilter"/>. Copying is always an explicit click and goes to the clipboard only; nothing is typed from here.
/// </summary>
public sealed class DictationHistoryWindow : Window
{
    private static readonly string[] StatusNames = ["All", "Typed into app", "Not typed"];
    private readonly DictationHost host;
    private readonly TextBox search = new() { PlaceholderText = "Search final and original transcript text", MinWidth = 240 };
    private readonly ComboBox statusBox = new() { ItemsSource = StatusNames, SelectedIndex = 0, MinWidth = 150 };
    private readonly ComboBox appBox = new() { MinWidth = 170 };
    private readonly ComboBox windowBox = new() { MinWidth = 190, MaxDropDownHeight = 320 };
    private readonly Button clearFilters = new() { Content = "Clear filters", IsEnabled = false };
    private readonly ListBox list = new();
    private readonly TextBox detail = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MinHeight = 140 };
    private readonly TextBlock summary = new() { Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center };
    private IReadOnlyList<DictationHistoryEntry> all = [];
    private IReadOnlyList<DictationHistoryEntry> shown = [];
    private IReadOnlyList<HistoryTargetOption> appOptions = [DictationHistoryFilter.AllApps];
    private IReadOnlyList<HistoryTargetOption> windowOptions = [DictationHistoryFilter.AllWindows];
    private bool loading;

    public DictationHistoryWindow(DictationHost host)
    {
        this.host = host;
        this.Title = "PrimeDictate: dictation history";
        this.Width = 860;
        this.Height = 640;
        this.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var copy = new Button { Content = "Copy transcript" };
        copy.Click += async (_, _) => await this.CopyAsync(details: false);
        var copyDetails = new Button { Content = "Copy details" };
        copyDetails.Click += async (_, _) => await this.CopyAsync(details: true);
        var delete = new Button { Content = "Delete" };
        delete.Click += (_, _) => this.DeleteSelected();
        var clear = new Button { Content = "Clear all..." };
        clear.Click += (_, _) => this.ConfirmClear();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 0), Children = { copy, copyDetails, delete, clear } };

        this.clearFilters.Click += (_, _) => this.ClearFilters();
        var searchRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children = { new TextBlock { Text = "Search", VerticalAlignment = VerticalAlignment.Center }, this.search, this.summary, this.clearFilters }
        };
        var filterRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Margin = new Thickness(0, 8, 0, 8),
            Children =
            {
                new TextBlock { Text = "Status", VerticalAlignment = VerticalAlignment.Center }, this.statusBox,
                new TextBlock { Text = "App", VerticalAlignment = VerticalAlignment.Center }, this.appBox,
                new TextBlock { Text = "Window", VerticalAlignment = VerticalAlignment.Center }, this.windowBox
            }
        };
        var top = new StackPanel { Children = { searchRow, filterRow } };

        var layout = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        DockPanel.SetDock(this.detail, Dock.Bottom);
        layout.Children.Add(top);
        layout.Children.Add(buttons);
        layout.Children.Add(this.detail);
        layout.Children.Add(this.list);
        this.Content = layout;

        this.search.TextChanged += (_, _) => this.ApplyFilters();
        this.statusBox.SelectionChanged += (_, _) => this.ApplyFilters();
        this.appBox.SelectionChanged += (_, _) =>
        {
            if (!this.loading)
            {
                this.RebuildWindowOptions();
                this.ApplyFilters();
            }
        };
        this.windowBox.SelectionChanged += (_, _) => this.ApplyFilters();
        this.list.SelectionChanged += (_, _) => this.ShowDetail();
        this.Opened += (_, _) =>
        {
            this.Reload();
            this.search.Focus();
        };
        host.HistoryChanged += this.OnHistoryChanged;
        this.Closed += (_, _) => host.HistoryChanged -= this.OnHistoryChanged;
    }

    private HistoryStatusFilter Status => (HistoryStatusFilter)Math.Max(0, this.statusBox.SelectedIndex);

    private HistoryTargetOption App => this.appBox.SelectedIndex is >= 0 and var i && i < this.appOptions.Count ? this.appOptions[i] : DictationHistoryFilter.AllApps;

    private HistoryTargetOption WindowChoice => this.windowBox.SelectedIndex is >= 0 and var i && i < this.windowOptions.Count ? this.windowOptions[i] : DictationHistoryFilter.AllWindows;

    private bool HasActiveFilters =>
        !string.IsNullOrWhiteSpace(this.search.Text) || this.Status != HistoryStatusFilter.All
        || this.App.Kind != HistoryTargetKind.All || this.WindowChoice.Kind != HistoryTargetKind.All;

    private void OnHistoryChanged(DictationHistoryEntry entry) => Avalonia.Threading.Dispatcher.UIThread.Post(this.Reload);

    /// <summary>Reads the whole log (it keeps at most 1,000 entries) and rebuilds the App and Window lists from it.</summary>
    private void Reload()
    {
        this.all = this.host.History.List(limit: DictationHistoryStore.MaxEntries);
        this.loading = true;
        try
        {
            var app = this.App;
            this.appOptions = DictationHistoryFilter.AppOptions(this.all);
            this.appBox.ItemsSource = this.appOptions.Select(o => o.DisplayName).ToList();
            var appIndex = this.appOptions.ToList().FindIndex(o => o.Kind == app.Kind && o.Value == app.Value);
            this.appBox.SelectedIndex = Math.Max(0, appIndex);
            this.RebuildWindowOptions();
        }
        finally
        {
            this.loading = false;
        }

        this.ApplyFilters();
    }

    private void RebuildWindowOptions()
    {
        var previous = this.WindowChoice;
        this.windowOptions = DictationHistoryFilter.WindowOptions(this.all, this.App);
        this.windowBox.ItemsSource = this.windowOptions.Select(o => o.DisplayName).ToList();
        var index = this.windowOptions.ToList().FindIndex(o => o.Kind == previous.Kind && o.Value == previous.Value);
        this.windowBox.SelectedIndex = Math.Max(0, index);
    }

    private void ApplyFilters()
    {
        if (this.loading)
        {
            return;
        }

        var previous = this.Selected?.Id;
        this.shown = DictationHistoryFilter.Apply(this.all, this.search.Text, this.Status, this.App, this.WindowChoice);
        this.list.ItemsSource = this.shown.Select(Line).ToList();
        this.summary.Text = $"{this.shown.Count:N0} of {this.all.Count:N0} entries";
        this.clearFilters.IsEnabled = this.HasActiveFilters;
        var keep = previous is { } id ? this.shown.ToList().FindIndex(e => e.Id == id) : -1;
        this.list.SelectedIndex = keep >= 0 ? keep : (this.shown.Count > 0 ? 0 : -1);
        if (this.shown.Count == 0)
        {
            this.detail.Text = this.all.Count == 0 ? "Nothing has been dictated yet." : "No entries match the filters.";
        }
    }

    private void ClearFilters()
    {
        this.loading = true;
        try
        {
            this.search.Text = string.Empty;
            this.statusBox.SelectedIndex = 0;
            this.appBox.SelectedIndex = 0;
            this.RebuildWindowOptions();
        }
        finally
        {
            this.loading = false;
        }

        this.ApplyFilters();
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

    private DictationHistoryEntry? Selected => this.list.SelectedIndex is >= 0 and var i && i < this.shown.Count ? this.shown[i] : null;

    /// <summary>The delivery, target, audio length, error, original and rewrite prompt, then the final text: everything the WPF detail pane showed.</summary>
    private void ShowDetail()
    {
        if (this.Selected is { } e)
        {
            this.detail.Text = DictationHistoryFilter.DetailsText(e);
        }
    }

    private async Task CopyAsync(bool details)
    {
        if (this.Selected is not { } e)
        {
            this.summary.Text = "Select a transcript first, then copy.";
            return;
        }

        if (this.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(details ? DictationHistoryFilter.DetailsText(e) : e.Transcript);
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
