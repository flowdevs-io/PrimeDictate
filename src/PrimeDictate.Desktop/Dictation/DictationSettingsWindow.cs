using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using PrimeDictate.Core.Dictation;
using PrimeDictate.Core.Providers;
using PrimeDictate.Platforms.Dictation;
using PrimeDictate.Platforms.Input;

namespace PrimeDictate.Desktop.Dictation;

/// <summary>Dictation settings. Built in code so it stays small; the fields mirror the WPF app's dictation tab.</summary>
public sealed class DictationSettingsWindow : Window
{
    private readonly DictationHost host;
    private readonly IAudioSource? audio;
    private readonly ComboBox modelBox = new() { MinWidth = 260 };
    private readonly ComboBox micBox = new() { MinWidth = 260 };
    private readonly Slider gain = new() { Minimum = 0.5, Maximum = 4, Width = 200 };
    private readonly NumericUpDown silence = new() { Minimum = 0, Maximum = 30, Increment = 1, FormatString = "0", Width = 120 };
    private readonly CheckBox sendEnter = new() { Content = "Coding mode: press Enter after typing" };
    private readonly CheckBox returnToStart = new() { Content = "If focus moved, return to the window I started in" };
    private readonly CheckBox typeWithoutGuard = new() { Content = "Type even when the app cannot check which window is in front" };
    private readonly CheckBox wakeEnabled = new() { Content = "Wake word: start dictation when I say the phrase (listens on the idle microphone, audio stays in memory)" };
    private readonly TextBox wakePhrase = new() { Width = 260 };
    private readonly CheckBox voiceCommands = new() { Content = "Voice commands while dictating" };
    private readonly TextBox commitPhrase = new() { Width = 260 };
    private readonly TextBox stopPhrase = new() { Width = 260 };
    private readonly TextBox historyPhrase = new() { Width = 260 };
    private readonly ComboBox overlayBox = new() { ItemsSource = new[] { "Compact microphone", "Full panel" } };
    private readonly CheckBox sticky = new() { Content = "Keep the overlay pinned on screen" };
    private readonly TextBox replacements = new() { AcceptsReturn = true, MinHeight = 90, PlaceholderText = "spoken phrase => replacement (one per line)" };
    private readonly Dictionary<HotkeyAction, (TextBlock Label, HotkeyGesture Gesture)> hotkeys = [];
    private readonly TextBlock status = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap, Opacity = 0.75 };
    private DictationSettings working;
    private IReadOnlyList<AudioInputDevice> devices = [];

    public DictationSettingsWindow(DictationHost host, IAudioSource? audio)
    {
        this.host = host;
        this.audio = audio;
        this.working = host.Settings;
        this.Title = "PrimeDictate: dictation settings";
        this.Width = 560;
        this.SizeToContent = SizeToContent.Height;
        this.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 10 };
        panel.Children.Add(Row("Model", this.modelBox));
        panel.Children.Add(Row("Microphone", this.micBox));
        panel.Children.Add(Row("Input gain", this.gain));
        panel.Children.Add(Row("Auto-commit after silence (seconds, 0 = hotkey only)", this.silence));
        foreach (var (action, name) in new[] { (HotkeyAction.ToggleDictation, "Start / stop dictation"), (HotkeyAction.EmergencyStop, "Emergency stop (discard)"), (HotkeyAction.ShowHistory, "Show history") })
        {
            panel.Children.Add(this.HotkeyRow(action, name));
        }

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
        panel.Children.Add(this.sticky);
        panel.Children.Add(new TextBlock { Text = "Replacements" });
        panel.Children.Add(this.replacements);
        panel.Children.Add(this.status);

        var save = new Button { Content = "Save", HorizontalAlignment = HorizontalAlignment.Right };
        save.Click += (_, _) => this.Save();
        panel.Children.Add(save);
        this.Content = new ScrollViewer { Content = panel };
        this.Opened += async (_, _) => await this.LoadAsync();
    }

    private static Control Row(string label, Control input) => new StackPanel
    {
        Spacing = 4,
        Children = { new TextBlock { Text = label }, input }
    };

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
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children = { new TextBlock { Text = name, MinWidth = 190, VerticalAlignment = VerticalAlignment.Center }, label, change }
        };
    }

    private async Task LoadAsync()
    {
        var models = this.host.InstalledModels();
        this.modelBox.ItemsSource = models.Select(m => m.DisplayName).ToList();
        var wanted = this.working.ResolveModelId();
        this.modelBox.SelectedIndex = Math.Max(0, models.ToList().FindIndex(m => m.ModelId == wanted));
        if (models.Count == 0)
        {
            this.status.Text = "No Whisper model is installed. Put one in the models folder or download it from the WPF app; it is shared.";
        }

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

    private void Save()
    {
        var models = this.host.InstalledModels();
        var s = this.working;
        if (this.modelBox.SelectedIndex is >= 0 and var mi && mi < models.Count)
        {
            s.SelectedModelId = models[mi].Id;
            s.TranscriptionBackend = LegacyBackend.Whisper;
        }

        s.SelectedInputDeviceId = this.micBox.SelectedIndex is > 0 and var di && di - 1 < this.devices.Count ? this.devices[di - 1].Id : null;
        s.InputGainMultiplier = this.gain.Value;
        s.AutoCommitSilenceSeconds = (int)(this.silence.Value ?? 3);
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
        s.DictationHotkey = HotkeyDto.From(this.hotkeys[HotkeyAction.ToggleDictation].Gesture);
        s.StopHotkey = HotkeyDto.From(this.hotkeys[HotkeyAction.EmergencyStop].Gesture);
        s.HistoryHotkey = HotkeyDto.From(this.hotkeys[HotkeyAction.ShowHistory].Gesture);
        s.TranscriptReplacements = (this.replacements.Text ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split("=>", 2))
            .Where(parts => parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0]))
            .Select(parts => new ReplacementDto { Find = parts[0].Trim(), Replace = parts[1].Trim() })
            .ToList();
        this.host.ApplySettings(s);
        this.Close();
    }
}
