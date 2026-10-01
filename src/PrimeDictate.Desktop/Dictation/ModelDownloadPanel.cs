using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using PrimeDictate.Platforms.Dictation;
using PrimeDictate.Platforms.Speech;

namespace PrimeDictate.Desktop.Dictation;

/// <summary>Pick a catalog model, download it with progress, cancel it. Calls <paramref name="installed"/> with the model's id once a model is ready.</summary>
public sealed class ModelDownloadPanel : StackPanel
{
    private readonly DictationHost host;
    private readonly ComboBox choice = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button start = new() { Content = "Download", Margin = new Avalonia.Thickness(8, 0, 0, 0) };
    private readonly Button cancel = new() { Content = "Cancel", IsVisible = false, Margin = new Avalonia.Thickness(8, 0, 0, 0) };
    private readonly ProgressBar bar = new() { Minimum = 0, Maximum = 1, IsVisible = false };
    private readonly TextBlock text = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap, Opacity = 0.8 };
    private readonly IReadOnlyList<ModelDownloadOption> options;
    private CancellationTokenSource? running;

    private static string FamilyName(PrimeDictate.Core.Dictation.LegacyBackend backend) => backend switch
    {
        PrimeDictate.Core.Dictation.LegacyBackend.WhisperNet => "Whisper.net",
        PrimeDictate.Core.Dictation.LegacyBackend.QualcommQnn => "Qualcomm NPU",
        _ => backend.ToString()
    };

    public ModelDownloadPanel(DictationHost host, Action<string> installed)
    {
        this.host = host;
        this.Spacing = 6;
        // Qualcomm NPU models are offered only on a Windows ARM64 build that has the QNN runtime, as in the WPF app.
        this.options = SpeechModelCatalog.AvailableOptions(MachineSupport.Current);
        this.choice.ItemsSource = this.options
            .Select(o => $"{FamilyName(o.Backend)}: {o.DisplayName} ({SpeechModelCatalog.FormatSize(o.ApproximateBytes)}){(o.Recommended ? " - recommended" : string.Empty)}")
            .ToList();
        this.choice.SelectedIndex = PreselectedIndex(this.options, host.Settings);
        this.start.Click += async (_, _) => await this.RunAsync(installed);
        this.cancel.Click += (_, _) => this.running?.Cancel();
        // The list takes what is left of the row (the model names are long), so the buttons stay inside the window.
        Grid.SetColumn(this.start, 1);
        Grid.SetColumn(this.cancel, 2);
        this.Children.Add(new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Children = { this.choice, this.start, this.cancel } });
        this.Children.Add(this.bar);
        this.Children.Add(this.text);
    }

    /// <summary>
    /// The model shown first, as the WPF Settings window chose it: the one the settings already name, else the recommended model of that
    /// family (Whisper by default), else the first. For a new install that is Whisper Tiny English.
    /// </summary>
    internal static int PreselectedIndex(IReadOnlyList<ModelDownloadOption> options, PrimeDictate.Core.Dictation.DictationSettings settings)
    {
        var wanted = settings.ResolveModelId();
        var index = wanted is null ? -1 : options.ToList().FindIndex(o => o.ModelId == wanted);
        if (index < 0)
        {
            index = options.ToList().FindIndex(o => o.Backend == settings.TranscriptionBackend && o.Recommended);
        }

        return Math.Max(0, index);
    }

    /// <summary>True while a download is running.</summary>
    public bool IsBusy => this.running is not null;

    /// <summary>Starts downloading the model currently shown in the list (first-run setup does this when no model is installed, as the WPF wizard did).</summary>
    public void StartDownload(Action<string> installed)
    {
        if (!this.IsBusy)
        {
            _ = this.RunAsync(installed);
        }
    }

    private async Task RunAsync(Action<string> installed)
    {
        var option = this.options[Math.Max(0, this.choice.SelectedIndex)];
        this.running = new CancellationTokenSource();
        this.SetBusy(true);
        this.text.Text = $"Downloading {option.DisplayName}...";
        var progress = new Progress<ModelDownloadProgress>(p => Dispatcher.UIThread.Post(() =>
        {
            this.bar.IsIndeterminate = p.Fraction is null;
            this.bar.Value = p.Fraction ?? 0;
            this.text.Text = p.Stage switch
            {
                "download" => $"Downloading {option.DisplayName}: {p.BytesDownloaded / (1024 * 1024):N0} MB{(p.TotalBytes is { } t ? $" of {t / (1024 * 1024):N0} MB" : string.Empty)}",
                "extract" => "Unpacking...",
                "tokenizer" => "Adding the Whisper tokenizer...",
                _ => "Ready."
            };
        }));
        try
        {
            await this.host.DownloadModelAsync(option, progress, this.running.Token);
            this.text.Text = $"{option.DisplayName} is installed.";
            installed(option.ModelId);
        }
        catch (OperationCanceledException)
        {
            this.text.Text = "Download cancelled. Nothing was installed.";
        }
        catch (Exception ex)
        {
            this.text.Text = $"Could not install the model: {ex.Message}";
        }
        finally
        {
            this.running.Dispose();
            this.running = null;
            this.SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        this.start.IsEnabled = !busy;
        this.choice.IsEnabled = !busy;
        this.cancel.IsVisible = busy;
        this.bar.IsVisible = busy;
    }
}
