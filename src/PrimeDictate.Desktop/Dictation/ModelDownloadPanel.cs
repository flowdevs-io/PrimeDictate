using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using PrimeDictate.Platforms.Dictation;
using PrimeDictate.Platforms.Speech;

namespace PrimeDictate.Desktop.Dictation;

/// <summary>Pick a catalog model, download it with progress, cancel it. Calls <paramref name="installed"/> once a model is ready.</summary>
public sealed class ModelDownloadPanel : StackPanel
{
    private readonly DictationHost host;
    private readonly ComboBox choice = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button start = new() { Content = "Download", Margin = new Avalonia.Thickness(8, 0, 0, 0) };
    private readonly Button cancel = new() { Content = "Cancel", IsVisible = false, Margin = new Avalonia.Thickness(8, 0, 0, 0) };
    private readonly ProgressBar bar = new() { Minimum = 0, Maximum = 1, IsVisible = false };
    private readonly TextBlock text = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap, Opacity = 0.8 };
    private CancellationTokenSource? running;

    public ModelDownloadPanel(DictationHost host, Action installed)
    {
        this.host = host;
        this.Spacing = 6;
        this.choice.ItemsSource = SpeechModelCatalog.Options
            .Select(o => $"{o.Backend}: {o.DisplayName} ({SpeechModelCatalog.FormatSize(o.ApproximateBytes)}){(o.Recommended ? " - recommended" : string.Empty)}")
            .ToList();
        this.choice.SelectedIndex = 0;
        this.start.Click += async (_, _) => await this.RunAsync(installed);
        this.cancel.Click += (_, _) => this.running?.Cancel();
        // The list takes what is left of the row (the model names are long), so the buttons stay inside the window.
        Grid.SetColumn(this.start, 1);
        Grid.SetColumn(this.cancel, 2);
        this.Children.Add(new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Children = { this.choice, this.start, this.cancel } });
        this.Children.Add(this.bar);
        this.Children.Add(this.text);
    }

    private async Task RunAsync(Action installed)
    {
        var option = SpeechModelCatalog.Options[Math.Max(0, this.choice.SelectedIndex)];
        this.running = new CancellationTokenSource();
        this.SetBusy(true);
        this.text.Text = $"Downloading {option.DisplayName} from GitHub...";
        var progress = new Progress<ModelDownloadProgress>(p => Dispatcher.UIThread.Post(() =>
        {
            this.bar.IsIndeterminate = p.Fraction is null;
            this.bar.Value = p.Fraction ?? 0;
            this.text.Text = p.Stage switch
            {
                "download" => $"Downloading {option.DisplayName}: {p.BytesDownloaded / (1024 * 1024):N0} MB{(p.TotalBytes is { } t ? $" of {t / (1024 * 1024):N0} MB" : string.Empty)}",
                "extract" => "Unpacking...",
                _ => "Ready."
            };
        }));
        try
        {
            await this.host.DownloadModelAsync(option, progress, this.running.Token);
            this.text.Text = $"{option.DisplayName} is installed.";
            installed();
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
