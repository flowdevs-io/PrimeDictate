using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using PrimeDictate.Core.Diagnostics;

namespace PrimeDictate.Desktop.Dictation;

/// <summary>
/// Dictation activity: dictation sessions (time range, status, app, never the text), the log of the selected session and
/// the app-wide activity log, with Copy buttons for bug reports. Backed by <see cref="AppLog.Feed"/>.
/// </summary>
public sealed class DictationActivityWindow : Window
{
    private readonly ActivityFeed feed = AppLog.Feed;
    private readonly ListBox sessionList = new() { MinWidth = 260 };
    private readonly ListBox sessionLog = new();
    private readonly ListBox globalLog = new();
    private readonly TextBlock sessionHeading = new() { Text = "Select a session", FontWeight = FontWeight.SemiBold };
    private IReadOnlyList<DictationSessionInfo> sessions = [];
    private int refreshQueued;

    public DictationActivityWindow()
    {
        this.Title = "PrimeDictate: dictation activity";
        this.Width = 980;
        this.Height = 640;
        this.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var copyErrors = new Button { Content = "Copy errors" };
        copyErrors.Click += async (_, _) => await this.CopyAsync(this.feed.Entries().Where(e => e.Level == ActivityLevel.Error));
        var copyAll = new Button { Content = "Copy all activity" };
        copyAll.Click += async (_, _) => await this.CopyAsync(this.feed.Entries());
        var clear = new Button { Content = "Clear" };
        clear.Click += (_, _) => this.feed.Clear();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 0), Children = { copyErrors, copyAll, clear } };

        var left = new DockPanel { Margin = new Thickness(0, 0, 12, 0) };
        var sessionsTitle = new TextBlock { Text = "Dictation sessions", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(sessionsTitle, Dock.Top);
        left.Children.Add(sessionsTitle);
        left.Children.Add(this.sessionList);

        var perSession = new DockPanel { Height = 240 };
        DockPanel.SetDock(this.sessionHeading, Dock.Top);
        perSession.Children.Add(this.sessionHeading);
        perSession.Children.Add(this.sessionLog);
        DockPanel.SetDock(perSession, Dock.Top);

        var globalTitle = new TextBlock { Text = "All activity (newest first, last 600)", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 10, 0, 6) };
        DockPanel.SetDock(globalTitle, Dock.Top);
        var right = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        right.Children.Add(buttons);
        right.Children.Add(perSession);
        right.Children.Add(globalTitle);
        right.Children.Add(this.globalLog);

        var privacy = new TextBlock { Text = "No recognized text is kept here: only when, what happened and why.", Opacity = 0.6, FontSize = 12, Margin = new Thickness(0, 0, 0, 8) };
        var layout = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(privacy, Dock.Top);
        DockPanel.SetDock(left, Dock.Left);
        layout.Children.Add(privacy);
        layout.Children.Add(left);
        layout.Children.Add(right);
        this.Content = layout;

        this.sessionList.SelectionChanged += (_, _) => this.ShowSession();
        this.Opened += (_, _) => this.Refresh();
        this.feed.Changed += this.OnChanged;
        this.Closed += (_, _) => this.feed.Changed -= this.OnChanged;
    }

    // The feed changes on worker threads, often in bursts; coalesce into one refresh per UI turn.
    private void OnChanged()
    {
        if (Interlocked.Exchange(ref this.refreshQueued, 1) == 1)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref this.refreshQueued, 0);
            this.Refresh();
        });
    }

    private void Refresh()
    {
        var selected = this.SelectedSession()?.Id;
        this.sessions = this.feed.Sessions();
        this.sessionList.ItemsSource = this.sessions.Select(ActivityText.Session).ToList();
        if (selected is { } id)
        {
            var i = this.sessions.ToList().FindIndex(s => s.Id == id);
            if (i >= 0)
            {
                this.sessionList.SelectedIndex = i;
            }
        }

        this.globalLog.ItemsSource = this.feed.Entries().Select(ActivityText.Line).ToList();
        this.ShowSession();
    }

    private DictationSessionInfo? SelectedSession() =>
        this.sessionList.SelectedIndex is >= 0 and var i && i < this.sessions.Count ? this.sessions[i] : null;

    private void ShowSession()
    {
        if (this.SelectedSession() is not { } session)
        {
            this.sessionHeading.Text = "Select a session";
            this.sessionLog.ItemsSource = null;
            return;
        }

        this.sessionHeading.Text = ActivityText.Session(session);
        this.sessionLog.ItemsSource = this.feed.SessionEntries(session.Id).Select(ActivityText.Line).ToList();
    }

    private async Task CopyAsync(IEnumerable<ActivityEntry> entries)
    {
        var text = ActivityText.Join(entries);
        if (this.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text.Length == 0 ? "(no activity)" : text);
        }
    }
}
