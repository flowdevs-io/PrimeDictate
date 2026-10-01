using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using PrimeDictate.Core.Dictation;
using PrimeDictate.Platforms.Dictation;
using PrimeDictate.Platforms.Speech;

namespace PrimeDictate.Desktop.Dictation;

/// <summary>
/// First-run setup, the same three steps as the WPF wizard: Welcome (what works here, how commits behave), Model (pick, download or point at
/// one), Commands (shortcuts and voice phrases). Next and Back move between them; Finish validates everything with the same rules as the
/// Settings window and saves. Closing early asks "Finish setup later?", and setup shows again at the next start until it is finished.
/// </summary>
public sealed class DictationOnboardingWindow : Window
{
    private static readonly string[] StepTitles = ["Welcome", "Choose your model", "Configure commands"];

    private readonly DictationHost host;
    private readonly ComboBox modelBox = new() { MinWidth = 280 };
    private readonly StackPanel checks = new() { Spacing = 8 };
    private readonly NumericUpDown silence = new() { Minimum = 0, Maximum = 30, Increment = 1, FormatString = "0", Width = 120 };
    private readonly CheckBox sendEnter = FormParts.Check("Send Enter after committing the transcript (coding mode)");
    private readonly CheckBox audioCues = FormParts.Check("Play start and stop tones");
    private readonly CheckBox autoUpdate = FormParts.Check("Check GitHub Releases for software updates automatically");
    private readonly ModelDownloadPanel downloads;
    private readonly CustomModelPanel customModel;
    private readonly CommandsPanel commands;
    private readonly Control[] steps;
    private readonly ScrollViewer body = new() { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    private readonly TextBlock stepHeader = new() { FontWeight = FontWeight.SemiBold, Opacity = 0.8 };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
    private readonly TextBlock modelError = FormParts.ErrorLine();
    private readonly Button back = new() { Content = "Back" };
    private readonly Button next = new() { Content = "Next", IsDefault = true };
    private List<string> shown = [];
    private int step;
    private bool allowClose;
    private bool asking;

    public DictationOnboardingWindow(DictationHost host, Action openSettings)
    {
        this.host = host;
        this.Title = "Welcome to PrimeDictate";
        this.Width = 600;
        this.Height = 680;
        this.MaxHeight = 900;
        this.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        this.downloads = new ModelDownloadPanel(host, id => this.Populate(id));
        this.customModel = new CustomModelPanel(host.ModelsFolder);
        this.commands = new CommandsPanel(host, host.Settings, message => this.status.Text = message);
        this.modelBox.SelectionChanged += (_, _) => this.ShowSelectedModelPath();
        this.silence.Value = host.Settings.AutoCommitSilenceSeconds;
        this.sendEnter.IsChecked = host.Settings.SendEnterAfterCommit;
        this.audioCues.IsChecked = host.Settings.PlayAudioCues;
        this.autoUpdate.IsChecked = host.Settings.CheckForUpdatesAutomatically;
        this.steps = [this.BuildWelcome(), this.BuildModel(), this.BuildCommands(openSettings)];

        this.back.Click += (_, _) => this.Go(this.step - 1);
        this.next.Click += (_, _) => this.OnNext();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { this.back, this.next } };
        var footer = new DockPanel { Margin = new Thickness(24, 10, 24, 16) };
        DockPanel.SetDock(buttons, Dock.Right);
        footer.Children.Add(buttons);
        footer.Children.Add(this.status);
        var header = new StackPanel
        {
            Margin = new Thickness(24, 20, 24, 4),
            Children = { new TextBlock { Text = "Set up PrimeDictate", FontSize = 20, FontWeight = FontWeight.SemiBold }, this.stepHeader }
        };
        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(footer);
        root.Children.Add(this.body);
        this.Content = root;
        this.Go(0);
        this.Opened += (_, _) => this.Populate();
        this.Closing += (_, e) =>
        {
            // Only the user closing the window asks; the app shutting down or the OS signing out must not be blocked.
            if (this.allowClose || e.CloseReason is not (WindowCloseReason.WindowClosing or WindowCloseReason.Undefined))
            {
                return;
            }

            e.Cancel = true;
            _ = this.ConfirmCloseAsync();
        };
    }

    private Control BuildWelcome() => new StackPanel
    {
        Margin = new Thickness(24, 8, 24, 8),
        Spacing = 14,
        Children =
        {
            new TextBlock
            {
                Text = "PrimeDictate is tuned for dictating into editors, chat apps, documentation tools, terminals and browser text fields. You pick a local speech model, set your shortcut, and the app types the final transcript into the app you are in. Everything runs on this computer.",
                TextWrapping = TextWrapping.Wrap
            },
            new TextBlock { Text = "Here is what works right now.", FontWeight = FontWeight.SemiBold },
            this.checks,
            new TextBlock { Text = "Quick setup checklist", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) },
            Step("1. Pick a local speech model", "Choose Whisper, Parakeet, Moonshine or Whisper.net; download a model, or browse to one you already have."),
            Step("2. Set your dictation shortcut", "PrimeDictate listens globally, so you can start and stop dictation without changing apps."),
            Step("3. Tune how commits behave", "Choose silence auto-commit timing, the optional coding-mode Enter, and the start and stop tones."),
            new TextBlock { Text = "General dictation behavior", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) },
            FormParts.Row("Commit after this many seconds of silence (0 = shortcut only)", this.silence),
            this.sendEnter,
            this.audioCues,
            this.autoUpdate,
            FormParts.Note("Emergency stop shortcuts and stop phrases discard the active capture without typing text. Use silence auto-commit or the start / stop shortcut when you want to commit."),
            FormParts.Note("You can revisit any of these settings from the tray icon later.")
        }
    };

    private Control BuildModel() => new StackPanel
    {
        Margin = new Thickness(24, 8, 24, 8),
        Spacing = 10,
        Children =
        {
            new TextBlock { Text = "Choose a speech model", FontWeight = FontWeight.SemiBold },
            FormParts.Note("PrimeDictate supports several local speech engines. Pick a model that fits your computer, then download it or browse to an existing install."),
            new TextBlock { Text = "Installed models" },
            this.modelBox,
            new TextBlock { Text = "Download a model", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) },
            this.downloads,
            this.customModel,
            this.modelError
        }
    };

    private Control BuildCommands(Action openSettings)
    {
        var more = new Button { Content = "More settings...", HorizontalAlignment = HorizontalAlignment.Left };
        more.Click += (_, _) =>
        {
            if (this.TryFinish())
            {
                this.Close();
                openSettings();
            }
        };
        return new StackPanel
        {
            Margin = new Thickness(24, 8, 24, 8),
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = "Keyboard and voice commands", FontWeight = FontWeight.SemiBold },
                FormParts.Note("These work anywhere. Press the start / stop shortcut to begin dictating, and again to type what you said."),
                this.commands,
                more
            }
        };
    }

    private static Control Step(string title, string detail) => new Border
    {
        BorderBrush = new SolidColorBrush(Color.FromArgb(70, 128, 128, 128)),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(10),
        Child = new StackPanel
        {
            Children =
            {
                new TextBlock { Text = title, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 }
            }
        }
    };

    private void Go(int index)
    {
        this.step = Math.Clamp(index, 0, this.steps.Length - 1);
        this.body.Content = this.steps[this.step];
        this.stepHeader.Text = $"Step {this.step + 1} of {this.steps.Length}: {StepTitles[this.step]}";
        this.back.IsEnabled = this.step > 0;
        this.next.Content = this.step == this.steps.Length - 1 ? "Finish" : "Next";
        this.status.Text = string.Empty;
    }

    private void OnNext()
    {
        switch (this.step)
        {
            case 0:
                this.Go(1);
                // The WPF wizard started the first download as you moved on; do the same when nothing is installed yet.
                if (this.host.InstalledModels().Count == 0)
                {
                    this.downloads.StartDownload(id => this.Populate(id));
                }

                break;
            case 1:
                if (this.TryValidateModel(out _, out _, out _))
                {
                    this.Go(2);
                }

                break;
            default:
                if (this.TryFinish())
                {
                    this.Close();
                }

                break;
        }
    }

    private void Populate(string? select = null)
    {
        this.checks.Children.Clear();
        this.Add(this.host.UnavailableReason is null, "Microphone", this.host.UnavailableReason ?? "A capture device is available.");
        var models = this.host.InstalledModels();
        this.Add(models.Count > 0, "Speech model", models.Count > 0
            ? $"{models.Count} installed."
            : $"None installed yet. The next step downloads one (Whisper Tiny English is the fastest to start with), or you can copy a model folder into {this.host.ModelsFolder}.");
        this.Add(this.host.HotkeyUnavailableReason is null, "Global hotkey", this.host.HotkeyUnavailableReason ?? "Available.");
        this.Add(this.host.FocusGuardAvailable, "Typing into the right window", this.host.FocusGuardAvailable
            ? "PrimeDictate checks that the window you started in is still in front before typing."
            : this.host.FocusGuardNotice ?? "Not available here; PrimeDictate will not type unless you allow it in settings.");

        var keep = this.modelBox.SelectedIndex is >= 0 and var i && i < this.shown.Count ? this.shown[i] : null;
        this.shown = models.Select(m => m.ModelId).ToList();
        this.modelBox.ItemsSource = models.Select(m => m.DisplayName).ToList();
        var wanted = select ?? keep ?? this.host.WantedModelId();
        this.modelBox.SelectedIndex = models.Count == 0 ? -1 : Math.Max(0, this.shown.IndexOf(wanted ?? string.Empty));
        this.modelBox.IsEnabled = models.Count > 0;
        this.ShowSelectedModelPath();
    }

    private InstalledSpeechModel? SelectedModel()
    {
        var models = this.host.InstalledModels();
        return this.modelBox.SelectedIndex is >= 0 and var i && i < models.Count ? models[i] : null;
    }

    private void ShowSelectedModelPath() => this.customModel.ShowSelected(this.SelectedModel());

    /// <summary>A model is needed to finish: an installed one that is selected, or a valid custom path. Same rules as Settings.</summary>
    private bool TryValidateModel(out string? modelPath, out InstalledSpeechModel? custom, out InstalledSpeechModel? selected)
    {
        modelPath = null;
        custom = null;
        selected = this.SelectedModel();
        FormParts.Show(this.modelError, null);
        if (this.downloads.IsBusy)
        {
            FormParts.Show(this.modelError, "Wait for the current model download to finish or cancel it before continuing.");
            return false;
        }

        if (!this.customModel.TryResolve(selected, selected?.Backend ?? this.host.Settings.TranscriptionBackend, out modelPath, out custom))
        {
            return false;
        }

        if (custom is null && selected is null)
        {
            FormParts.Show(this.modelError, "Choose a model to download, or browse to an existing model folder, before continuing.");
            return false;
        }

        return true;
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
                        new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap, MaxWidth = 480, Opacity = 0.8 }
                    }
                }
            }
        });

    /// <summary>Validates every step and saves. Problems are shown on the step that has them.</summary>
    private bool TryFinish()
    {
        if (!this.TryValidateModel(out var modelPath, out var custom, out var selected))
        {
            this.Go(1);
            return false;
        }

        if (!this.commands.TryValidate(out var phrases))
        {
            this.Go(2);
            this.status.Text = "Not finished: fix the red message above.";
            return false;
        }

        var settings = this.host.Settings;
        if (custom is not null)
        {
            settings.ModelPath = modelPath;
            settings.SelectedModelId = custom.Id;
            settings.TranscriptionBackend = custom.Backend;
        }
        else if (selected is not null)
        {
            settings.ModelPath = null;
            settings.SelectedModelId = selected.Id;
            settings.TranscriptionBackend = selected.Backend;
        }

        settings.AutoCommitSilenceSeconds = (int)(this.silence.Value ?? 3);
        settings.SendEnterAfterCommit = this.sendEnter.IsChecked == true;
        settings.PlayAudioCues = this.audioCues.IsChecked == true;
        settings.CheckForUpdatesAutomatically = this.autoUpdate.IsChecked != false;
        this.commands.Apply(settings, phrases);
        settings.FirstRunCompleted = true;
        this.host.ApplySettings(settings);
        this.allowClose = true;
        return true;
    }

    private async Task ConfirmCloseAsync()
    {
        if (this.asking)
        {
            return;
        }

        this.asking = true;
        try
        {
            var later = await ConfirmDialog.AskAsync(
                this,
                "Finish setup later?",
                "Setup is not finished yet. PrimeDictate will keep running in the tray, and this setup window will open again the next time you start the app.\n\nClose setup for now?");
            if (later)
            {
                this.allowClose = true;
                this.Close();
            }
        }
        finally
        {
            this.asking = false;
        }
    }
}
