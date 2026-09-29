using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using PrimeDictate.Core.Providers;
using PrimeDictate.Platforms.Audio;

namespace PrimeDictate.Desktop;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        this.InitializeComponent();
        this.PlatformText.Text = $"{RuntimeInformation.OSDescription} · {RuntimeInformation.ProcessArchitecture} · .NET {Environment.Version}";
        this.DictationStatus.Text = OperatingSystem.IsWindows()
            ? "Global dictation stays in the Windows app during the migration."
            : "Global dictation (hotkey and typing into other apps) is not available on this platform yet. Use Transcription, then Copy or Export.";
        this.Opened += async (_, _) => await this.ProbeAudioAsync();
    }

    public async Task SaveScreenshotAsync(string path)
    {
        var size = new PixelSize((int)this.Bounds.Width, (int)this.Bounds.Height);
        using var bitmap = new RenderTargetBitmap(size);
        bitmap.Render(this);
        bitmap.Save(path, PngBitmapEncoderOptions.Default);
        await Task.CompletedTask;
    }

    private async Task ProbeAudioAsync()
    {
        try
        {
            using var source = new MiniAudioCaptureSource();
            var devices = await source.ListDevicesAsync(CancellationToken.None);
            this.AudioStatus.Text = $"Audio backend: {source.ActiveBackend}. Input devices: {devices.Count}.";
        }
        catch (AudioSourceException ex)
        {
            this.AudioStatus.Text = $"Microphone unavailable: {ex.Message}";
        }
    }
}
