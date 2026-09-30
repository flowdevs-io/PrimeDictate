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

    public DictationStatsWindow(DictationHost host)
    {
        this.Title = "PrimeDictate: dictation stats";
        this.Width = 620;
        this.SizeToContent = SizeToContent.Height;
        this.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var state = host.Stats();
        var baseline = host.Settings.BaselineTypingSpeedWpm;
        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 16 };
        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 28,
            Children =
            {
                Figure("Words typed", state.TotalWords.ToString("N0", CultureInfo.InvariantCulture)),
                Figure("Time saved", FormatDuration(state.TimeSaved(baseline))),
                Figure("Speaking speed", $"{state.AverageWordsPerMinute:N0} WPM"),
                Figure("Dictations", state.InjectedSessions.ToString("N0", CultureInfo.InvariantCulture))
            }
        });
        panel.Children.Add(new TextBlock { Text = $"Time saved compares with typing at {(baseline is >= 20 and <= 120 ? baseline : DictationStatsStore.DefaultBaselineWpm)} WPM. Only text that was typed into an app counts.", Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = "Last 14 days", FontWeight = FontWeight.SemiBold });
        panel.Children.Add(this.Bars(state));
        panel.Children.Add(new TextBlock { Text = "Milestones", FontWeight = FontWeight.SemiBold });
        foreach (var a in DictationStatsStore.Achievements)
        {
            var unlocked = state.UnlockedAchievementIds.Contains(a.Id) || state.TotalWords >= a.WordThreshold;
            var remaining = Math.Max(0, a.WordThreshold - state.TotalWords);
            panel.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                Children =
                {
                    new TextBlock { Text = unlocked ? "✓" : "○", Foreground = unlocked ? Brushes.SeaGreen : Brushes.Gray, Width = 16 },
                    new TextBlock { Text = a.Title, FontWeight = FontWeight.SemiBold, MinWidth = 190 },
                    new TextBlock { Text = unlocked ? a.Message : $"{Math.Min(100, state.TotalWords * 100.0 / a.WordThreshold):N0}% - {remaining:N0} words to go", Opacity = 0.8, TextWrapping = TextWrapping.Wrap, MaxWidth = 340 }
                }
            });
        }

        this.Content = panel;
    }

    private static StackPanel Figure(string label, string value) => new()
    {
        Children = { new TextBlock { Text = value, FontSize = 22, FontWeight = FontWeight.Bold }, new TextBlock { Text = label, Opacity = 0.7 } }
    };

    private Control Bars(DictationStatsState state)
    {
        var days = state.LastDays(DateTime.Today);
        var max = Math.Max(1, days.Max(d => d.Words));
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Height = 150 };
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
