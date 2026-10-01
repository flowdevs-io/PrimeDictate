using Avalonia;
using Avalonia.Controls;
using PrimeDictate.Core.Dictation;

namespace PrimeDictate.Desktop.Dictation;

/// <summary>Optional rewriting of the final transcript with a local Ollama model (the WPF "Ollama post-processing" block).</summary>
public sealed class OllamaPanel : StackPanel
{
    private static readonly (OllamaMode Mode, string Label)[] Modes =
    [
        (OllamaMode.Default, "Default (Grammar & Spelling)"),
        (OllamaMode.Prompt, "Prompt (AI Prompt Engineer)"),
        (OllamaMode.Bug, "Bug Report (QA Engineer)"),
        (OllamaMode.Update, "Update (Changelog/Status)"),
        (OllamaMode.Communication, "Communication (Email/Message)"),
        (OllamaMode.Blog, "Blog (Draft/Post)"),
        (OllamaMode.VibeCoding, "Vibe Coding (Agent Instructions)")
    ];

    private readonly CheckBox enabled = FormParts.Check("Enable local Ollama formatting");
    private readonly TextBox endpoint = new() { Width = 300 };
    private readonly TextBox model = new() { Width = 200 };
    private readonly ComboBox mode = new() { ItemsSource = Modes.Select(m => m.Label).ToList(), Width = 300 };
    private readonly CheckBox allowRemote = FormParts.Check("Allow an Ollama server on another computer (your dictated words are sent there)");
    private readonly TextBlock remoteNote = FormParts.Note("This endpoint is not on this computer. Until you allow remote endpoints, your words are typed as spoken and not sent.");
    private readonly TextBlock error = FormParts.ErrorLine();

    public OllamaPanel(DictationSettings settings)
    {
        this.Spacing = 8;
        this.Children.Add(FormParts.Note("After you stop, the final text can be rewritten by a model running in Ollama before it is typed. If Ollama is off or fails, the text is typed as spoken."));
        this.Children.Add(this.enabled);
        this.Children.Add(FormParts.Row("Endpoint", this.endpoint));
        this.Children.Add(FormParts.Row("Model", this.model));
        this.Children.Add(FormParts.Row("Mode", this.mode));
        this.Children.Add(this.allowRemote);
        this.Children.Add(this.remoteNote);
        this.Children.Add(this.error);
        this.enabled.IsChecked = settings.EnableOllamaPostProcessing;
        this.endpoint.Text = settings.OllamaEndpoint;
        this.model.Text = settings.OllamaModel;
        this.mode.SelectedIndex = Math.Max(0, Array.FindIndex(Modes, m => m.Mode == settings.OllamaMode));
        this.allowRemote.IsChecked = settings.OllamaAllowRemoteEndpoint;
        this.endpoint.TextChanged += (_, _) => this.UpdateNote();
        this.allowRemote.IsCheckedChanged += (_, _) => this.UpdateNote();
        this.UpdateNote();
    }

    /// <summary>With rewriting on, the endpoint and model must be usable. The problem is shown under the form.</summary>
    public bool TryValidate()
    {
        var problem = DictationSettingsValidator.ValidateOllama(this.enabled.IsChecked == true, this.endpoint.Text, this.model.Text);
        FormParts.Show(this.error, problem);
        return problem is null;
    }

    public void Apply(DictationSettings settings)
    {
        settings.EnableOllamaPostProcessing = this.enabled.IsChecked == true;
        // Left blank while off, the defaults come back rather than an empty address being saved.
        settings.OllamaEndpoint = string.IsNullOrWhiteSpace(this.endpoint.Text) ? OllamaOptions.Disabled.Endpoint : this.endpoint.Text.Trim();
        settings.OllamaModel = string.IsNullOrWhiteSpace(this.model.Text) ? OllamaOptions.Disabled.Model : this.model.Text.Trim();
        settings.OllamaMode = Modes[Math.Clamp(this.mode.SelectedIndex, 0, Modes.Length - 1)].Mode;
        settings.OllamaAllowRemoteEndpoint = this.allowRemote.IsChecked == true;
    }

    private void UpdateNote() =>
        this.remoteNote.IsVisible = DictationSettingsValidator.IsRemoteEndpoint(this.endpoint.Text) && this.allowRemote.IsChecked != true;
}
