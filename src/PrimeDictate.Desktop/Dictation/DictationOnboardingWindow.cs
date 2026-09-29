using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using PrimeDictate.Core.Dictation;
using PrimeDictate.Platforms.Dictation;

namespace PrimeDictate.Desktop.Dictation;

/// <summary>
/// First-run check. It says plainly what will work on this computer (microphone, model, hotkeys, typing) and lets the
/// user pick a model. Saving writes <c>dictation-settings.json</c>, which is also what stops it from showing again.
/// </summary>
public sealed class DictationOnboardingWindow : Window
{
    private readonly DictationHost host;
    private readonly ComboBox modelBox = new() { MinWidth = 280 };
    private readonly StackPanel checks = new() { Spacing = 8 };

    public DictationOnboardingWindow(DictationHost host, Action openSettings)
    {
        this.host = host;
        this.Title = "Welcome to PrimeDictate";
        this.Width = 560;
        this.SizeToContent = SizeToContent.Height;
        this.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var done = new Button { Content = "Start using PrimeDictate", HorizontalAlignment = HorizontalAlignment.Right };
        done.Click += (_, _) => this.Finish();
        var more = new Button { Content = "More settings..." };
        more.Click += (_, _) =>
        {
            this.Finish();
            openSettings();
        };

        var hotkey = host.Settings.ToBindings()[HotkeyAction.ToggleDictation];
        this.Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 14,
            Children =
            {
                new TextBlock { Text = "Everything runs on this computer. Here is what works right now.", TextWrapping = TextWrapping.Wrap, FontSize = 16 },
                this.checks,
                new TextBlock { Text = "Speech model" },
                this.modelBox,
                new ModelDownloadPanel(host, this.Populate),
                new TextBlock { Text = $"Press {hotkey} in any app to start dictating, and again to type what you said.", TextWrapping = TextWrapping.Wrap, Opacity = 0.8 },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { more, done } }
            }
        };
        this.Opened += (_, _) => this.Populate();
    }

    private void Populate()
    {
        this.checks.Children.Clear();
        this.Add(this.host.UnavailableReason is null, "Microphone", this.host.UnavailableReason ?? "A capture device is available.");
        var models = this.host.InstalledModels();
        this.Add(models.Count > 0, "Speech model", models.Count > 0
            ? $"{models.Count} installed."
            : $"None installed yet. Download one below (Whisper Tiny English is the fastest to start with), or copy a model folder into {this.host.ModelsFolder}.");
        this.Add(this.host.HotkeyUnavailableReason is null, "Global hotkey", this.host.HotkeyUnavailableReason ?? "Available.");
        this.Add(this.host.FocusGuardAvailable, "Typing into the right window", this.host.FocusGuardAvailable
            ? "PrimeDictate checks that the window you started in is still in front before typing."
            : this.host.FocusGuardNotice ?? "Not available here; PrimeDictate will not type unless you allow it in settings.");

        this.modelBox.ItemsSource = models.Select(m => m.DisplayName).ToList();
        var wanted = this.host.Settings.ResolveModelId();
        this.modelBox.SelectedIndex = models.Count == 0 ? -1 : Math.Max(0, models.ToList().FindIndex(m => m.ModelId == wanted));
        this.modelBox.IsEnabled = models.Count > 0;
    }

    private void Add(bool ok, string title, string detail) =>
        this.checks.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = ok ? "✓" : "!", Foreground = ok ? Brushes.SeaGreen : Brushes.DarkOrange, FontWeight = FontWeight.Bold, Width = 16 },
                new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = title, FontWeight = FontWeight.SemiBold },
                        new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap, MaxWidth = 460, Opacity = 0.8 }
                    }
                }
            }
        });

    private void Finish()
    {
        var settings = this.host.Settings;
        var models = this.host.InstalledModels();
        if (this.modelBox.SelectedIndex is >= 0 and var i && i < models.Count)
        {
            settings.SelectedModelId = models[i].Id;
            settings.TranscriptionBackend = models[i].Backend;
        }

        this.host.ApplySettings(settings);
        this.Close();
    }
}
