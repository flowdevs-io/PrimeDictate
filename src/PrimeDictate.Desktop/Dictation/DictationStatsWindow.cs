using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using PrimeDictate.Core.Dictation;
using PrimeDictate.Platforms.Dictation;

namespace PrimeDictate.Desktop.Dictation;

/// <summary>Lifetime words, time saved against typing, average speaking speed, the last 14 days and word milestones.</summary>
public sealed class DictationStatsWindow : Window
{
    private static readonly Color[] Palette = [Color.Parse("#38BDF8"), Color.Parse("#A78BFA"), Color.Parse("#34D399"), Color.Parse("#F59E0B")];

    private readonly DictationHost host;
    private readonly StackPanel content = new() { Spacing = 16 };
    private readonly NumericUpDown baselineBox = new()
    {
        Minimum = DictationStatsStore.MinBaselineWpm,
        Maximum = DictationStatsStore.MaxBaselineWpm,
        Increment = 5,
        FormatString = "0",
        Width = 120
    };

    private bool loading;

    public DictationStatsWindow(DictationHost host)
    {
        this.host = host;
        this.Title = "PrimeDictate: dictation stats";
        this.Width = 620;
        this.SizeToContent = SizeToContent.Height;
        this.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        this.content.Margin = new Thickness(24);
        this.Content = new ScrollViewer { Content = this.content, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        this.MaxHeight = 760;
        this.baselineBox.ValueChanged += (_, _) => this.OnBaselineChanged();
        this.Render();
    }

    /// <summary>
    /// The "compare against typing at N WPM" setting, as on the WPF Impact tab: whole numbers from 20 to 120, saved at once, and the
    /// time-saved figure follows. A value outside the range (an empty box) is not saved.
    /// </summary>
    private void OnBaselineChanged()
    {
        if (this.loading || this.baselineBox.Value is not { } value)
        {
            return;
        }

        var wpm = (int)Math.Round(value);
        if (wpm is < DictationStatsStore.MinBaselineWpm or > DictationStatsStore.MaxBaselineWpm || wpm == this.host.Settings.BaselineTypingSpeedWpm)
        {
            return;
        }

        this.host.Settings.BaselineTypingSpeedWpm = wpm;
        this.host.ApplySettings(this.host.Settings);
        this.Render();
    }

    private void Render()
    {
        var state = this.host.Stats();
        var baseline = DictationStatsStore.NormalizeBaselineWpm(this.host.Settings.BaselineTypingSpeedWpm);
        this.loading = true;
        this.baselineBox.Value = baseline;
        this.loading = false;
        var panel = this.content;
        panel.Children.Clear();
        panel.Children.Add(new TextBlock { Text = "Dictation impact", FontSize = 18, FontWeight = FontWeight.SemiBold });
        panel.Children.Add(new TextBlock { Text = "Local stats are counted from successful transcript commits and stay on this PC. Only text that was typed into an app counts.", Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 28,
            Children =
            {
                Figure("Words typed", state.TotalWords.ToString("N0", CultureInfo.InvariantCulture)),
                Figure("Net time saved", FormatDuration(state.TimeSaved(baseline))),
                Figure("Average speech pace", $"{state.AverageWordsPerMinute:N0} WPM"),
                Figure("Successful sessions", state.InjectedSessions.ToString("N0", CultureInfo.InvariantCulture))
            }
        });
        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "Compare against typing at", VerticalAlignment = VerticalAlignment.Center },
                this.baselineBox,
                new TextBlock { Text = "WPM (20 to 120)", VerticalAlignment = VerticalAlignment.Center }
            }
        });
        panel.Children.Add(new TextBlock { Text = "Last 14 days", FontWeight = FontWeight.SemiBold });
        panel.Children.Add(this.Bars(state));
        panel.Children.Add(new TextBlock { Text = "Achievements", FontWeight = FontWeight.SemiBold });
        foreach (var a in DictationStatsStore.Achievements)
        {
            var unlocked = state.UnlockedAchievementIds.Contains(a.Id) || state.TotalWords >= a.WordThreshold;
            var remaining = Math.Max(0, a.WordThreshold - state.TotalWords);
            var accent = unlocked ? Color.Parse("#34D399") : Color.Parse("#64748B");
            panel.Children.Add(new Border
            {
                BorderBrush = new SolidColorBrush(accent),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12),
                Child = new DockPanel
                {
                    LastChildFill = true,
                    Children =
                    {
                        new TextBlock { Text = unlocked ? "Unlocked" : "Locked", Foreground = new SolidColorBrush(accent), FontWeight = FontWeight.SemiBold, [DockPanel.DockProperty] = Dock.Right, Margin = new Thickness(14, 0, 0, 0) },
                        new StackPanel
                        {
                            Children =
                            {
                                new TextBlock { Text = a.Title, FontWeight = FontWeight.SemiBold },
                                new TextBlock { Text = unlocked ? a.Message : $"{Math.Min(100, state.TotalWords * 100.0 / a.WordThreshold):N0}% complete - {remaining:N0} words to go.", Opacity = 0.8, TextWrapping = TextWrapping.Wrap }
                            }
                        }
                    }
                }
            });
        }
    }

    private static StackPanel Figure(string label, string value) => new()
    {
        Children = { new TextBlock { Text = value, FontSize = 22, FontWeight = FontWeight.Bold }, new TextBlock { Text = label, Opacity = 0.7 } }
    };

    private Control Bars(DictationStatsState state)
    {
        var days = state.LastDays(DateTime.Today);
        var max = Math.Max(1, days.Max(d => d.Words));
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Height = 166 };
        for (var i = 0; i < days.Count; i++)
        {
            var (day, words) = days[i];
            var height = words == 0 ? 4 : Math.Max(8, Math.Round(words / (double)max * 112));
            var bar = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Bottom,
                Width = 36,
                Children =
                {
                    new TextBlock { Text = words.ToString(CultureInfo.InvariantCulture), FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center, Opacity = 0.7 },
                    new Border { Height = height, Background = new SolidColorBrush(Palette[i % Palette.Length]), CornerRadius = new CornerRadius(3) },
                    new TextBlock { Text = day.ToString("M/d", CultureInfo.InvariantCulture), FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center, Opacity = 0.7 }
                }
            };
            ToolTip.SetTip(bar, $"{words:N0} words");
            row.Children.Add(bar);
        }

        return row;
    }

    internal static string FormatDuration(TimeSpan d) =>
        d.TotalMinutes < 1 ? "<1 min"
        : d.TotalHours < 1 ? $"{d.TotalMinutes:N0} min"
        : d.TotalDays < 1 ? $"{d.TotalHours:N1} hr"
        : $"{d.TotalDays:N1} days";
}
