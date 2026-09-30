using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Platform;
using Avalonia.Threading;
using PrimeDictate.Core.Dictation;
using PrimeDictate.Core.Providers;
using PrimeDictate.Core.Settings;
using PrimeDictate.Platforms.Dictation;
using PrimeDictate.Platforms.Speech;
using PrimeDictate.Platforms.Startup;
using PrimeDictate.Platforms.Input;

namespace PrimeDictate.Desktop.Dictation;

/// <summary>Dictation settings. Built in code so it stays small; the fields mirror the WPF app's dictation tab.</summary>
public sealed class DictationSettingsWindow : Window
{
    private readonly DictationHost host;
    private readonly IAudioSource? audio;
    private readonly TranscriptionPrefsService prefs;
    private List<string> shownModels = [];
    private readonly ComboBox modelBox = new() { MinWidth = 260 };
    private readonly ComboBox micBox = new() { MinWidth = 260 };
    private readonly Slider gain = new() { Minimum = 0.5, Maximum = 4, Width = 200 };
    private readonly NumericUpDown silence = new() { Minimum = 0, Maximum = 30, Increment = 1, FormatString = "0", Width = 120 };
    private readonly CheckBox audioCues = Check("Play start and stop sounds");
    private readonly LaunchAtLogin launch = new();
    private readonly ComboBox deviceBox = new() { ItemsSource = new[] { "Auto (CUDA if ready, else CPU)", "CPU", "CUDA" }, MinWidth = 260 };
    private readonly CheckBox launchAtLogin = Check("Start PrimeDictate when I sign in (tray only)");
    private readonly CheckBox sendEnter = Check("Coding mode: press Enter after typing");
    private readonly CheckBox returnToStart = Check("If focus moved, return to the window I started in");
    private readonly CheckBox typeWithoutGuard = Check("Type even when the app cannot check which window is in front");
    private readonly CheckBox wakeEnabled = Check("Wake word: start dictation when I say the phrase (listens on the idle microphone, audio stays in memory)");
    private readonly TextBox wakePhrase = new() { Width = 260 };
    private readonly CheckBox voiceCommands = Check("Voice commands while dictating");
    private readonly TextBox commitPhrase = new() { Width = 260 };
    private readonly TextBox stopPhrase = new() { Width = 260 };
    private readonly TextBox historyPhrase = new() { Width = 260 };
    private readonly ComboBox overlayBox = new() { ItemsSource = new[] { "Compact microphone", "Full panel" } };
    private readonly CheckBox sticky = Check("Keep the overlay on screen when not dictating");
    private bool resetOverlayPosition;
    private readonly TextBox replacements = new() { AcceptsReturn = true, MinHeight = 90, MaxHeight = 220, PlaceholderText = "spoken phrase => replacement (one per line)" };
    private readonly Dictionary<HotkeyAction, (TextBlock Label, HotkeyGesture Gesture)> hotkeys = [];
    private readonly TextBlock status = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap, Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
    private readonly ComboBox liveTextBox = new()
    {
        ItemsSource = new[] { "Off: record only, transcribe after Stop", "Fast draft while people talk, then final pass" },
        MinWidth = 260
    };
    private readonly ComboBox sourceBox = new() { ItemsSource = new[] { "Microphone", "System audio (speakers)", "Microphone + system audio" }, MinWidth = 260 };
    private readonly CheckBox speakerLabels = Check("Meetings: speaker labels after Stop (needs Nemotron and its diarizer)");
    private readonly CheckBox boostAudio = Check("Boost quiet audio for recognition (the saved recording is not changed)");
    private readonly StackPanel shellRows = new() { Spacing = 8 };
    private readonly List<ShellRow> shellEditors = [];
    private DictationSettings working;
    private IReadOnlyList<AudioInputDevice> devices = [];

    private sealed record ShellRow(Control Root, CheckBox On, TextBox Phrase, ComboBox After, TextBox Command);

    public DictationSettingsWindow(DictationHost host, IAudioSource? audio, TranscriptionPrefsService prefs)
    {
        this.host = host;
        this.audio = audio;
        this.prefs = prefs;
        this.working = host.Settings;
        this.Title = "PrimeDictate: Settings";
        this.Width = 560;
        this.SizeToContent = SizeToContent.Height;
        this.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        this.FitToScreen(this.Screens.Primary);

        var panel = new StackPanel { Margin = new Thickness(20, 20, 20, 10), Spacing = 10 };
        panel.Children.Add(Heading("Dictation"));
        panel.Children.Add(Row("Model", this.modelBox));
        panel.Children.Add(new TextBlock { Text = "Download another model", FontWeight = Avalonia.Media.FontWeight.SemiBold });
        panel.Children.Add(new ModelDownloadPanel(host, this.RefreshModels));
        panel.Children.Add(Row("Speech model device (applies after restart)", this.deviceBox));
        panel.Children.Add(new TextBlock { Text = OnnxRuntimeDevice.Summary, Opacity = 0.7, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        panel.Children.Add(Row("Microphone", this.micBox));
        panel.Children.Add(Row("Input gain", this.gain));
        panel.Children.Add(Row("Auto-commit after silence (seconds, 0 = hotkey only)", this.silence));
        foreach (var (action, name) in new[] { (HotkeyAction.ToggleDictation, "Start / stop dictation"), (HotkeyAction.EmergencyStop, "Emergency stop (discard)"), (HotkeyAction.ShowHistory, "Show history") })
        {
            panel.Children.Add(this.HotkeyRow(action, name));
        }

        panel.Children.Add(this.audioCues);
        panel.Children.Add(this.launchAtLogin);
        panel.Children.Add(this.sendEnter);
        if (OperatingSystem.IsWindows())
        {
            panel.Children.Add(this.returnToStart);
        }

        if (!host.FocusGuardAvailable)
        {
            panel.Children.Add(this.typeWithoutGuard);
        }

        panel.Children.Add(this.wakeEnabled);
        panel.Children.Add(Row("Wake phrase", this.wakePhrase));
        panel.Children.Add(this.voiceCommands);
        panel.Children.Add(Row("Commit phrase (types what you said, then stops)", this.commitPhrase));
        panel.Children.Add(Row("Discard phrase (stops without typing)", this.stopPhrase));
        panel.Children.Add(Row("History phrase", this.historyPhrase));
        panel.Children.Add(Row("Overlay", this.overlayBox));
        panel.Children.Add(new TextBlock
        {
            Text = "Shows while you dictate: a microphone, or a panel with the words so far. Drag it anywhere; ✕ hides it until the next dictation.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Opacity = 0.7
        });
        panel.Children.Add(this.sticky);
        var resetOverlay = new Button { Content = "Move the overlay back to the bottom of the screen" };
        resetOverlay.Click += (_, _) =>
        {
            this.resetOverlayPosition = true;
            this.status.Text = "The overlay goes back to the bottom of the screen when you save.";
        };
        panel.Children.Add(resetOverlay);
        panel.Children.Add(new TextBlock { Text = "Replacements" });
        panel.Children.Add(this.replacements);

        panel.Children.Add(Heading("Transcription & meetings"));
        panel.Children.Add(new TextBlock
        {
            Text = "These apply to the recording and transcription side of PrimeDictate (the main window). The same options are in its left column; a change in either place shows in both.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Opacity = 0.7
        });
        panel.Children.Add(Row("Recording source (what Record meeting captures)", this.sourceBox));
        panel.Children.Add(Row("Live text during a meeting", this.liveTextBox));
        panel.Children.Add(this.speakerLabels);
        panel.Children.Add(this.boostAudio);

        panel.Children.Add(Heading("Commands that run programs (dictation only)"));
        panel.Children.Add(new Border
        {
            Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(40, 255, 160, 0)),
            BorderBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(230, 140, 0)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10),
            Child = new TextBlock
            {
                Text = "Warning: when you say one of these phrases while dictating, PrimeDictate runs the command on this computer with your account's permissions. "
                    + "Only add commands you understand and trust. The command is exactly what you type here; nothing you say is added to it. "
                    + "They never run from transcription or meetings. New commands start Off. Commands imported from the older PrimeDictate app keep the On/Off setting they had there.",
                TextWrapping = Avalonia.Media.TextWrapping.Wrap
            }
        });
        panel.Children.Add(this.shellRows);
        var addShell = new Button { Content = "Add command", HorizontalAlignment = HorizontalAlignment.Left };
        addShell.Click += (_, _) => this.AddShellRow(new VoiceShellCommand { Enabled = false });
        panel.Children.Add(addShell);

        // Save and the status line stay in view under the form, which scrolls when it is taller than the screen.
        var save = new Button { Content = "Save", VerticalAlignment = VerticalAlignment.Center };
        save.Click += (_, _) => this.Save();
        var footer = new DockPanel { Margin = new Thickness(20, 10, 20, 16) };
        DockPanel.SetDock(save, Dock.Right);
        footer.Children.Add(save);
        footer.Children.Add(this.status);
        var root = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        this.Content = root;
        this.Opened += async (_, _) =>
        {
            this.FitToScreen(this.Screens.ScreenFromWindow(this));
            await this.LoadAsync();
        };
        // A change made in the main window's left column shows here while this window is open.
        void OnPrefsChanged(object? origin)
        {
            if (!ReferenceEquals(origin, this))
            {
                this.LoadTranscriptionFields();
            }
        }

        prefs.Changed += OnPrefsChanged;
        this.Closed += (_, _) => prefs.Changed -= OnPrefsChanged;
    }

    private static Control Heading(string text) => new TextBlock
    {
        Text = text,
        FontSize = 16,
        FontWeight = Avalonia.Media.FontWeight.SemiBold,
        Margin = new Thickness(0, 14, 0, 0)
    };

    private void LoadTranscriptionFields()
    {
        var p = this.prefs.Current;
        this.sourceBox.SelectedIndex = p.LastSource switch { RecordingSources.System => 1, RecordingSources.Meeting => 2, _ => 0 };
        this.liveTextBox.SelectedIndex = p.LiveTextMode == LiveTextModes.Draft ? 1 : 0;
        this.speakerLabels.IsChecked = p.SpeakerLabelsAfterStop;
        this.boostAudio.IsChecked = p.BoostQuietAudio;
    }

    private void AddShellRow(VoiceShellCommand command)
    {
        var on = new CheckBox { Content = "On", IsChecked = command.Enabled, VerticalAlignment = VerticalAlignment.Center };
        var phrase = new TextBox { Text = command.Phrase, PlaceholderText = "When I say", VerticalAlignment = VerticalAlignment.Center };
        var after = new ComboBox
        {
            ItemsSource = new[] { "Then stop", "Then keep typing" },
            SelectedIndex = command.CompletionBehavior == VoiceShellCommandCompletionBehavior.Continue ? 1 : 0,
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(after, "Then stop: nothing else from that dictation is typed. Then keep typing: the words after the phrase are typed as usual.");
        var cmd = new TextBox { Text = command.Command, PlaceholderText = "Command to run (as you would type it in a terminal)" };
        var remove = new Button { Content = "Remove", VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(phrase, 1);
        Grid.SetColumn(after, 2);
        Grid.SetColumn(remove, 3);
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), Children = { on, phrase, after, remove } };
        top.ColumnDefinitions[1].MinWidth = 120;
        foreach (var child in top.Children.Skip(1))
        {
            ((Control)child).Margin = new Thickness(8, 0, 0, 0);
        }

        var root = new Border
        {
            BorderBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(70, 128, 128, 128)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8),
            Child = new StackPanel { Spacing = 6, Children = { top, cmd } }
        };
        var row = new ShellRow(root, on, phrase, after, cmd);
        remove.Click += (_, _) =>
        {
            this.shellEditors.Remove(row);
            this.shellRows.Children.Remove(root);
        };
        this.shellEditors.Add(row);
        this.shellRows.Children.Add(root);
    }

    /// <summary>Rows that are fully empty are dropped; a row with only a phrase or only a command is an error the user must fix.</summary>
    private bool TryBuildShellCommands(out List<VoiceShellCommand> commands)
    {
        commands = [];
        foreach (var row in this.shellEditors)
        {
            var phrase = row.Phrase.Text?.Trim() ?? string.Empty;
            var command = row.Command.Text?.Trim() ?? string.Empty;
            if (phrase.Length == 0 && command.Length == 0)
            {
                continue;
            }

            if (phrase.Length == 0 || command.Length == 0)
            {
                this.status.Text = "Each voice command needs both a phrase to say and a command to run.";
                return false;
            }

            commands.Add(new VoiceShellCommand
            {
                Enabled = row.On.IsChecked == true,
                Phrase = phrase,
                Command = command,
                CompletionBehavior = row.After.SelectedIndex == 1 ? VoiceShellCommandCompletionBehavior.Continue : VoiceShellCommandCompletionBehavior.Stop
            });
        }

        return true;
    }

    private static CheckBox Check(string text) => new() { Content = new TextBlock { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap } };

    private static Control Row(string label, Control input)
    {
        // Under its label, not centered in the window.
        input.HorizontalAlignment = HorizontalAlignment.Left;
        return new StackPanel
        {
            Spacing = 4,
            Children = { new TextBlock { Text = label, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, input }
        };
    }

    /// <summary>
    /// Never taller than the screen the window is on (the whole form is, on a laptop or a scaled screen): the form scrolls
    /// instead, and once shown the window is moved back inside the screen if it hangs over an edge.
    /// </summary>
    private void FitToScreen(Screen? screen)
    {
        if (screen is null)
        {
            return;
        }

        var area = screen.WorkingArea;
        // The working area is in pixels; the title bar and a little air stay outside the client area.
        this.MaxHeight = Math.Max(320, (area.Height / screen.Scaling) - 80);
        if (!this.IsVisible)
        {
            return;
        }

        Dispatcher.UIThread.Post(
            () =>
            {
                var frame = this.FrameSize ?? this.ClientSize;
                var width = (int)(frame.Width * screen.Scaling);
                var height = (int)(frame.Height * screen.Scaling);
                var x = Math.Clamp(this.Position.X, area.X, Math.Max(area.X, area.Right - width));
                var y = Math.Clamp(this.Position.Y, area.Y, Math.Max(area.Y, area.Bottom - height));
                if (x != this.Position.X || y != this.Position.Y)
                {
                    this.Position = new PixelPoint(x, y);
                }
            },
            DispatcherPriority.Background);
    }

    private Control HotkeyRow(HotkeyAction action, string name)
    {
        var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MinWidth = 140 };
        this.hotkeys[action] = (label, this.working.ToBindings()[action]);
        label.Text = this.hotkeys[action].Gesture.ToString();
        var change = new Button { Content = "Change" };
        change.Click += async (_, _) =>
        {
            if (this.host.HotkeyUnavailableReason is { } reason)
            {
                this.status.Text = reason;
                return;
            }

            label.Text = "Press the new shortcut (Esc cancels)...";
            var gesture = await HotkeyCapture.CaptureAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
            if (gesture is not null)
            {
                this.hotkeys[action] = (label, gesture);
            }

            label.Text = this.hotkeys[action].Gesture.ToString();
        };
        // Columns, not a horizontal stack, so "Press the new shortcut..." wraps inside the window instead of running past it.
        var title = new TextBlock { Text = name, TextWrapping = Avalonia.Media.TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        label.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        label.MinWidth = 0;
        label.Margin = new Thickness(10, 0);
        Grid.SetColumn(label, 1);
        Grid.SetColumn(change, 2);
        return new Grid { ColumnDefinitions = new ColumnDefinitions("190,*,Auto"), Children = { title, label, change } };
    }

    private void RefreshModels()
    {
        var models = this.host.InstalledModels();
        var current = this.modelBox.SelectedIndex is >= 0 and var ci && ci < this.shownModels.Count ? this.shownModels[ci] : this.working.ResolveModelId();
        this.shownModels = models.Select(m => m.ModelId).ToList();
        this.modelBox.ItemsSource = models.Select(m => m.DisplayName).ToList();
        this.modelBox.SelectedIndex = Math.Max(0, this.shownModels.IndexOf(current ?? string.Empty));
        this.status.Text = models.Count == 0 ? "No speech model is installed. Download one below." : string.Empty;
    }

    private async Task LoadAsync()
    {
        this.RefreshModels();
        if (this.audio is not null)
        {
            this.devices = await this.audio.ListDevicesAsync(CancellationToken.None);
            var names = new List<string> { "System default" };
            names.AddRange(this.devices.Select(d => d.Name));
            this.micBox.ItemsSource = names;
            var index = this.devices.ToList().FindIndex(d => d.Id == this.working.SelectedInputDeviceId);
            this.micBox.SelectedIndex = index < 0 ? 0 : index + 1;
        }

        this.gain.Value = this.working.InputGainMultiplier;
        this.silence.Value = this.working.AutoCommitSilenceSeconds;
        this.audioCues.IsChecked = this.working.PlayAudioCues;
        this.deviceBox.SelectedIndex = (int)OnnxRuntimeDevice.ParsePreference(this.working.OnnxDevice);
        this.launchAtLogin.IsChecked = this.launch.IsEnabled;
        this.sendEnter.IsChecked = this.working.SendEnterAfterCommit;
        this.returnToStart.IsChecked = this.working.ReturnToStartTargetOnCommit;
        this.typeWithoutGuard.IsChecked = this.working.TypeWithoutFocusGuard;
        this.wakeEnabled.IsChecked = this.working.EnableWakeWord;
        this.wakePhrase.Text = this.working.WakeWordPhrase;
        this.voiceCommands.IsChecked = this.working.EnableVoiceCommands;
        this.commitPhrase.Text = this.working.VoiceDictationPhrase;
        this.stopPhrase.Text = this.working.VoiceStopPhrase;
        this.historyPhrase.Text = this.working.VoiceHistoryPhrase;
        this.overlayBox.SelectedIndex = (int)this.working.OverlayMode;
        this.sticky.IsChecked = this.working.IsOverlaySticky;
        this.replacements.Text = string.Join('\n', this.working.TranscriptReplacements.Select(r => $"{r.Find} => {r.Replace}"));
        this.LoadTranscriptionFields();
        this.shellRows.Children.Clear();
        this.shellEditors.Clear();
        foreach (var command in this.working.VoiceShellCommands ?? [])
        {
            this.AddShellRow(command);
        }

        var notes = new List<string>();
        if (this.host.FocusGuardNotice is { } guardNotice)
        {
            notes.Add(guardNotice);
        }

        if (this.host.HotkeyUnavailableReason is { } hotkeyNotice)
        {
            notes.Add(hotkeyNotice);
        }

        if (notes.Count > 0)
        {
            this.status.Text = string.Join(' ', notes);
        }
    }

    private void ApplyLaunchAtLogin()
    {
        var wanted = this.launchAtLogin.IsChecked == true;
        if (wanted != this.launch.IsEnabled && this.launch.Apply(wanted) is { } problem)
        {
            this.status.Text = problem;
            this.launchAtLogin.IsChecked = this.launch.IsEnabled;
        }
    }

    private void Save()
    {
        if (!this.TryBuildShellCommands(out var shellCommands))
        {
            return;
        }

        var models = this.host.InstalledModels();
        var s = this.working;
        if (this.modelBox.SelectedIndex is >= 0 and var mi && mi < models.Count)
        {
            s.SelectedModelId = models[mi].Id;
            s.TranscriptionBackend = models[mi].Backend;
        }

        s.SelectedInputDeviceId = this.micBox.SelectedIndex is > 0 and var di && di - 1 < this.devices.Count ? this.devices[di - 1].Id : null;
        s.InputGainMultiplier = this.gain.Value;
        s.AutoCommitSilenceSeconds = (int)(this.silence.Value ?? 3);
        s.PlayAudioCues = this.audioCues.IsChecked == true;
        s.OnnxDevice = ((OnnxDevicePreference)Math.Max(0, this.deviceBox.SelectedIndex)).ToString().ToLowerInvariant();
        this.ApplyLaunchAtLogin();
        s.SendEnterAfterCommit = this.sendEnter.IsChecked == true;
        s.ReturnToStartTargetOnCommit = this.returnToStart.IsChecked == true;
        s.TypeWithoutFocusGuard = this.typeWithoutGuard.IsChecked == true;
        s.EnableWakeWord = this.wakeEnabled.IsChecked == true;
        s.WakeWordPhrase = WakePhrase.Normalize(this.wakePhrase.Text);
        s.EnableVoiceCommands = this.voiceCommands.IsChecked == true;
        s.VoiceDictationPhrase = this.commitPhrase.Text?.Trim() ?? string.Empty;
        s.VoiceStopPhrase = this.stopPhrase.Text?.Trim() ?? string.Empty;
        s.VoiceHistoryPhrase = this.historyPhrase.Text?.Trim() ?? string.Empty;
        s.OverlayMode = (OverlayStyle)Math.Max(0, this.overlayBox.SelectedIndex);
        s.IsOverlaySticky = this.sticky.IsChecked == true;
        if (this.resetOverlayPosition)
        {
            s.OverlayAnchorX = null;
            s.OverlayAnchorY = null;
        }
        s.DictationHotkey = HotkeyDto.From(this.hotkeys[HotkeyAction.ToggleDictation].Gesture);
        s.StopHotkey = HotkeyDto.From(this.hotkeys[HotkeyAction.EmergencyStop].Gesture);
        s.HistoryHotkey = HotkeyDto.From(this.hotkeys[HotkeyAction.ShowHistory].Gesture);
        s.TranscriptReplacements = (this.replacements.Text ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split("=>", 2))
            .Where(parts => parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0]))
            .Select(parts => new ReplacementDto { Find = parts[0].Trim(), Replace = parts[1].Trim() })
            .ToList();
        s.VoiceShellCommands = shellCommands;
        this.host.ApplySettings(s);
        var source = this.sourceBox.SelectedIndex switch { 1 => RecordingSources.System, 2 => RecordingSources.Meeting, _ => RecordingSources.Microphone };
        this.prefs.Update(
            p => p with
            {
                LastSource = source,
                LiveTextMode = this.liveTextBox.SelectedIndex == 1 ? LiveTextModes.Draft : LiveTextModes.Off,
                SpeakerLabelsAfterStop = this.speakerLabels.IsChecked == true,
                BoostQuietAudio = this.boostAudio.IsChecked == true
            },
            this);
        this.Close();
    }
}
