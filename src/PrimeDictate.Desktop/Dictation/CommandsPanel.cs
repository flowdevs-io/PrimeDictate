using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using PrimeDictate.Core.Dictation;
using PrimeDictate.Platforms.Dictation;
using PrimeDictate.Platforms.Input;

namespace PrimeDictate.Desktop.Dictation;

/// <summary>
/// The keyboard shortcuts and voice phrases that control dictation (start / stop, emergency stop, history), as the WPF "Commands" tab
/// had them. Used by the Settings window and by the last step of first-run setup. Validation happens here, in the form, with the rules in
/// <see cref="DictationSettingsValidator"/>.
/// </summary>
public sealed class CommandsPanel : StackPanel
{
    private readonly DictationHost host;
    private readonly Action<string> say;
    private readonly CheckBox voiceEnabled = FormParts.Check("Listen for command phrases while dictating");
    private readonly TextBox commit = new() { PlaceholderText = VoiceCommandProcessor.DefaultDictationPhrase };
    private readonly TextBox stop = new() { PlaceholderText = VoiceCommandProcessor.DefaultStopPhrase };
    private readonly TextBox history = new() { PlaceholderText = VoiceCommandProcessor.DefaultHistoryPhrase };
    private readonly Dictionary<HotkeyAction, (TextBlock Label, HotkeyGesture Gesture)> hotkeys = [];
    private readonly TextBlock error = FormParts.ErrorLine();

    /// <param name="say">Where status text goes (the window's status line), for example when hotkeys are unavailable.</param>
    public CommandsPanel(DictationHost host, DictationSettings settings, Action<string> say)
    {
        this.host = host;
        this.say = say;
        this.Spacing = 10;
        this.Children.Add(this.voiceEnabled);
        var bindings = settings.ToBindings();
        this.AddCommand(HotkeyAction.ToggleDictation, "Start / stop dictation (the phrase types what you said, then stops)", bindings, this.commit);
        this.AddCommand(HotkeyAction.EmergencyStop, "Emergency stop (the phrase stops without typing)", bindings, this.stop);
        this.AddCommand(HotkeyAction.ShowHistory, "Open history", bindings, this.history);
        this.Children.Add(FormParts.Note("Click Change, then press Ctrl, Shift or Alt plus a key. A blank phrase uses its default."));
        this.Children.Add(this.error);
        this.voiceEnabled.IsChecked = settings.EnableVoiceCommands;
        this.commit.Text = settings.VoiceDictationPhrase;
        this.stop.Text = settings.VoiceStopPhrase;
        this.history.Text = settings.VoiceHistoryPhrase;
    }

    public bool VoiceCommandsEnabled => this.voiceEnabled.IsChecked == true;

    public HotkeyGesture Gesture(HotkeyAction action) => this.hotkeys[action].Gesture;

    /// <summary>Checks the shortcuts and the three phrases, showing the problem under the form. False blocks Save.</summary>
    public bool TryValidate(out EffectiveVoicePhrases phrases)
    {
        var phraseProblem = DictationSettingsValidator.ValidateVoicePhrases(this.VoiceCommandsEnabled, this.commit.Text, this.stop.Text, this.history.Text, out phrases);
        var problem = DictationSettingsValidator.ValidateHotkeys(
            this.Gesture(HotkeyAction.ToggleDictation), this.Gesture(HotkeyAction.EmergencyStop), this.Gesture(HotkeyAction.ShowHistory)) ?? phraseProblem;
        FormParts.Show(this.error, problem);
        return problem is null;
    }

    /// <summary>Writes the shortcuts, the voice switch and the (already validated) phrases.</summary>
    public void Apply(DictationSettings settings, EffectiveVoicePhrases phrases)
    {
        settings.DictationHotkey = HotkeyDto.From(this.Gesture(HotkeyAction.ToggleDictation));
        settings.StopHotkey = HotkeyDto.From(this.Gesture(HotkeyAction.EmergencyStop));
        settings.HistoryHotkey = HotkeyDto.From(this.Gesture(HotkeyAction.ShowHistory));
        settings.EnableVoiceCommands = this.VoiceCommandsEnabled;
        settings.VoiceDictationPhrase = phrases.Commit;
        settings.VoiceStopPhrase = phrases.Stop;
        settings.VoiceHistoryPhrase = phrases.History;
    }

    private void AddCommand(HotkeyAction action, string title, IReadOnlyDictionary<HotkeyAction, HotkeyGesture> bindings, TextBox phrase)
    {
        var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        this.hotkeys[action] = (label, bindings[action]);
        label.Text = bindings[action].ToString();
        var change = new Button { Content = "Change", Margin = new Thickness(8, 0, 0, 0) };
        change.Click += async (_, _) =>
        {
            if (this.host.HotkeyUnavailableReason is { } reason)
            {
                this.say(reason);
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
        Grid.SetColumn(change, 1);
        phrase.HorizontalAlignment = HorizontalAlignment.Stretch;
        this.Children.Add(new Border
        {
            BorderBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(70, 128, 128, 128)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8),
            Child = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    new TextBlock { Text = title, FontWeight = Avalonia.Media.FontWeight.SemiBold, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { label, change } },
                    FormParts.Row("Voice phrase", phrase)
                }
            }
        });
    }
}
