using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using PrimeDictate.Core.Dictation;
using PrimeDictate.Platforms.Speech;

namespace PrimeDictate.Desktop.Dictation;

/// <summary>
/// The WPF "Resolved model path" box with Browse: shows where the selected model lives and lets the user point at a model that is not
/// in a PrimeDictate models folder (a folder, or for Whisper.net a ggml <c>.bin</c> file). The file rules come from
/// <see cref="SpeechModelLocator"/>; this control only asks it.
/// </summary>
public sealed class CustomModelPanel : StackPanel
{
    private readonly TextBox path = new() { PlaceholderText = "Model folder, or a Whisper GGML .bin file" };
    private readonly TextBlock status = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap, Opacity = 0.8 };
    private readonly TextBlock error = FormParts.ErrorLine();
    private bool suppress;

    public CustomModelPanel(string managedFolder)
    {
        this.Spacing = 6;
        var folder = new Button { Content = "Browse folder...", Margin = new Thickness(8, 0, 0, 0) };
        var file = new Button { Content = "Browse file...", Margin = new Thickness(8, 0, 0, 0) };
        folder.Click += async (_, _) => await this.BrowseFolderAsync();
        file.Click += async (_, _) => await this.BrowseFileAsync();
        Grid.SetColumn(folder, 1);
        Grid.SetColumn(file, 2);
        this.Children.Add(new TextBlock { Text = "Model location (use a downloaded model, or browse to one you already have)", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        this.Children.Add(new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Children = { this.path, folder, file } });
        this.Children.Add(this.status);
        this.Children.Add(this.error);
        this.Children.Add(FormParts.Note($"Downloaded models are stored in {managedFolder}. Models next to the app, or in a models folder beside where it is started, are found too."));
        this.path.TextChanged += (_, _) =>
        {
            if (!this.suppress)
            {
                this.Describe();
            }
        };
    }

    /// <summary>Shows where the selected model lives (blank for none). Called when the model list or its selection changes.</summary>
    public void ShowSelected(InstalledSpeechModel? selected)
    {
        this.suppress = true;
        this.path.Text = selected?.Directory ?? string.Empty;
        this.suppress = false;
        FormParts.Show(this.error, null);
        this.status.Text = selected is null ? string.Empty : $"Using {selected.DisplayName}.";
    }

    /// <summary>
    /// Decides what to save. A path that differs from the selected model's own is a custom model and must validate (the problem is shown and
    /// false returned); then <paramref name="custom"/> is that model. A blank or unchanged path means the list selection applies.
    /// <paramref name="modelPath"/> is what goes in <c>ModelPath</c>: the custom path, or null.
    /// </summary>
    public bool TryResolve(InstalledSpeechModel? selected, LegacyBackend? preferred, out string? modelPath, out InstalledSpeechModel? custom)
    {
        modelPath = null;
        custom = null;
        FormParts.Show(this.error, null);
        var text = this.path.Text?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return true;
        }

        if (selected is not null && PathsEqual(text, selected.Directory))
        {
            if (selected.IsCustom)
            {
                custom = selected;
                modelPath = selected.Directory;
            }

            return true;
        }

        if (SpeechModelLocator.TryResolveCustom(text, preferred, out var model, out var problem))
        {
            custom = model;
            modelPath = model.Directory;
            return true;
        }

        FormParts.Show(this.error, problem);
        return false;
    }

    private static bool PathsEqual(string a, string b)
    {
        try
        {
            return string.Equals(FormParts.Normalize(a), FormParts.Normalize(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private void Describe()
    {
        var text = this.path.Text?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            this.status.Text = string.Empty;
            FormParts.Show(this.error, null);
            return;
        }

        if (SpeechModelLocator.TryResolveCustom(text, null, out var model, out var problem))
        {
            FormParts.Show(this.error, null);
            this.status.Text = $"Found {model.DisplayName}. It is used when you save.";
        }
        else
        {
            this.status.Text = string.Empty;
            FormParts.Show(this.error, problem);
        }
    }

    private async Task BrowseFolderAsync()
    {
        if (TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }

        var picked = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Select a speech model folder", AllowMultiple = false });
        if (picked.Count > 0 && picked[0].TryGetLocalPath() is { } local)
        {
            this.path.Text = local;
        }
    }

    private async Task BrowseFileAsync()
    {
        if (TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }

        var picked = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select a Whisper GGML model (.bin)",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("GGML model files") { Patterns = ["*.bin"] }, FilePickerFileTypes.All]
        });
        if (picked.Count > 0 && picked[0].TryGetLocalPath() is { } local)
        {
            this.path.Text = local;
        }
    }
}
