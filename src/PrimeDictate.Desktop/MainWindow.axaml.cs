using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PrimeDictate.Core.Coordination;
using PrimeDictate.Core.Export;
using PrimeDictate.Core.Pipeline;
using PrimeDictate.Core.Providers;
using PrimeDictate.Core.Sessions;
using PrimeDictate.Core.Storage;
using PrimeDictate.Core.Transcripts;
using PrimeDictate.Platforms;
using PrimeDictate.Platforms.Speech;

namespace PrimeDictate.Desktop;

public sealed partial class MainWindow : Window
{
    private static readonly ExportFormat[] Formats = [ExportFormat.Text, ExportFormat.Markdown, ExportFormat.Json, ExportFormat.Srt, ExportFormat.WebVtt];

    private readonly TranscriptionWorkspaceService workspace;
    private readonly ObservableCollection<SegmentRow> rows = [];
    private readonly Dictionary<string, SegmentRow> rowsById = new(StringComparer.Ordinal);
    private IReadOnlyList<InstalledWhisperModel> models = [];
    private SessionDocumentHost? host;
    private CancellationTokenSource? jobCancel;
    private LiveTranscriptionSession? live;
    private int refreshQueued;

    public MainWindow()
    {
        this.InitializeComponent();
        this.PlatformText.Text = $"{RuntimeInformation.OSDescription} · {RuntimeInformation.ProcessArchitecture} · .NET {Environment.Version}";
        var dataDir = Environment.GetEnvironmentVariable("PRIMEDICTATE_DATA_DIR");
        this.workspace = new TranscriptionWorkspaceService(string.IsNullOrWhiteSpace(dataDir) ? null : new AppDataPaths(dataDir));
        this.TranscriptList.ItemsSource = this.rows;

        this.ImportButton.Click += async (_, _) => await this.PickFileAsync();
        this.SourceBox.ItemsSource = new[] { "Microphone", "System audio (speakers)", "Microphone + system audio" };
        this.SourceBox.SelectedIndex = 0;
        this.RecordButton.Click += async (_, _) => await this.StartRecordingAsync();
        this.PauseButton.Click += async (_, _) => await this.TogglePauseAsync();
        this.StopButton.Click += async (_, _) => await this.StopRecordingAsync();
        this.DiscardButton.Click += async (_, _) => await this.DiscardRecordingAsync();
        this.RerunButton.Click += async (_, _) => await this.RerunAsync();
        this.CopyButton.Click += async (_, _) => await this.CopyAsync();
        this.ExportButton.Click += async (_, _) => await this.ExportAsync();
        this.DeleteButton.Click += async (_, _) => await this.DeleteAsync();
        this.SessionList.SelectionChanged += async (_, _) => await this.OpenSelectedAsync();
        this.SearchBox.TextChanged += (_, _) => this.Refresh();
        this.TranscriptList.AddHandler(GotFocusEvent, this.OnRowFocus, RoutingStrategies.Bubble);
        this.TranscriptList.AddHandler(LostFocusEvent, this.OnRowBlur, RoutingStrategies.Bubble);
        DragDrop.SetAllowDrop(this, true);
        this.AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None);
        this.AddHandler(DragDrop.DropEvent, this.OnDrop);
        this.Opened += async (_, _) => await this.InitializeWorkspaceAsync();
        this.Closing += (_, _) => this.workspace.DiscardLiveAsync().GetAwaiter().GetResult();
    }

    /// <summary>Selects the newest session; used by the smoke screenshot.</summary>
    public void SelectFirstSession() => this.SessionList.SelectedIndex = this.SessionList.ItemCount > 0 ? 0 : -1;

    public async Task SaveScreenshotAsync(string path)
    {
        var size = new PixelSize((int)this.Bounds.Width, (int)this.Bounds.Height);
        using var bitmap = new RenderTargetBitmap(size);
        bitmap.Render(this);
        bitmap.Save(path, PngBitmapEncoderOptions.Default);
        await Task.CompletedTask;
    }

    private void Say(string message) => Dispatcher.UIThread.Post(() => this.StatusText.Text = message);

    private async Task InitializeWorkspaceAsync()
    {
        try
        {
            await this.workspace.InitializeAsync(CancellationToken.None);
            this.models = this.workspace.InstalledModels();
            this.ModelBox.ItemsSource = this.models.Select(m => m.DisplayName).ToList();
            this.ModelBox.SelectedIndex = this.models.Count > 0 ? 0 : -1;
            await this.ReloadSessionsAsync();
            var notes = new List<string>();
            if (this.models.Count == 0)
            {
                notes.Add($"No speech model is installed. Put a Whisper ONNX folder (for example sherpa-onnx-whisper-tiny.en) under {Path.Combine(this.workspace.Paths.ModelsDirectory, "whisper")}, or install one with the PrimeDictate Windows app.");
            }

            if (!this.workspace.HasFfmpeg)
            {
                notes.Add("ffmpeg was not found, so only WAV files can be imported.");
            }

            if (!this.workspace.CanRecordMicrophone)
            {
                notes.Add("Microphone recording is unavailable: " + this.workspace.MicrophoneUnavailableReason);
            }

            if (!this.workspace.CanRecordSystemAudio)
            {
                notes.Add(this.workspace.SystemAudioUnavailableReason ?? "System audio capture is unavailable.");
            }

            this.RecordButton.IsEnabled = this.workspace.CanRecord && this.models.Count > 0;
            this.ImportButton.IsEnabled = this.models.Count > 0;
            this.StatusText.Text = notes.Count == 0 ? "Ready." : string.Join(" ", notes);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException or UnauthorizedAccessException)
        {
            this.StatusText.Text = "The transcription workspace could not start: " + ex.Message;
            this.ImportButton.IsEnabled = false;
            this.RecordButton.IsEnabled = false;
        }
    }

    private InstalledWhisperModel? SelectedModel => this.ModelBox.SelectedIndex is >= 0 and var i && i < this.models.Count ? this.models[i] : null;

    private async Task ReloadSessionsAsync(Guid? select = null)
    {
        var list = await this.workspace.ListSessionsAsync(CancellationToken.None);
        var items = list.Select(s => new SessionItem(s.SessionId, s.Title, $"{s.Status} · {s.CreatedAt.LocalDateTime:g}" + (s.Duration is { } d ? $" · {(int)d.TotalMinutes}:{d.Seconds:00}" : string.Empty))).ToList();
        this.SessionList.ItemsSource = items;
        if (select is { } id)
        {
            this.SessionList.SelectedItem = items.FirstOrDefault(i => i.Id == id);
        }
    }

    private async Task OpenSelectedAsync()
    {
        if (this.SessionList.SelectedItem is not SessionItem item || this.host?.Document.SessionId == item.Id)
        {
            return;
        }

        var opened = await this.workspace.OpenSessionAsync(item.Id, CancellationToken.None);
        if (opened is not null)
        {
            this.Attach(opened);
        }
    }

    private void Attach(SessionDocumentHost? newHost)
    {
        if (this.host is not null)
        {
            this.host.Changed -= this.OnDocumentChanged;
        }

        this.host = newHost;
        this.rows.Clear();
        this.rowsById.Clear();
        if (newHost is not null)
        {
            newHost.Changed += this.OnDocumentChanged;
        }

        this.Refresh();
    }

    private void OnDocumentChanged(TranscriptDocument _)
    {
        // Live updates can arrive many times a second; coalesce them into one UI refresh.
        if (Interlocked.Exchange(ref this.refreshQueued, 1) == 0)
        {
            Dispatcher.UIThread.Post(() =>
            {
                Volatile.Write(ref this.refreshQueued, 0);
                this.Refresh();
            });
        }
    }

    private void Refresh()
    {
        var document = this.host?.Document;
        var search = this.SearchBox.Text?.Trim();
        var visible = new List<SegmentRow>();
        if (document is not null)
        {
            foreach (var segment in document.ActiveSegments)
            {
                if (!this.rowsById.TryGetValue(segment.Id, out var row))
                {
                    row = new SegmentRow(segment.Id);
                    this.rowsById[segment.Id] = row;
                }

                row.Update(segment);
                if (string.IsNullOrEmpty(search) || row.Text.Contains(search, StringComparison.OrdinalIgnoreCase))
                {
                    visible.Add(row);
                }
            }
        }

        // Only touch the collection when membership or order changed, so a row being edited keeps focus.
        if (!this.rows.SequenceEqual(visible))
        {
            for (var i = this.rows.Count - 1; i >= 0; i--)
            {
                if (!visible.Contains(this.rows[i]))
                {
                    this.rows.RemoveAt(i);
                }
            }

            for (var i = 0; i < visible.Count; i++)
            {
                if (i >= this.rows.Count)
                {
                    this.rows.Add(visible[i]);
                }
                else if (!ReferenceEquals(this.rows[i], visible[i]))
                {
                    this.rows.Insert(i, visible[i]);
                }
            }
        }

        var has = document is not null && document.ActiveSegments.Any();
        this.EmptyText.IsVisible = this.rows.Count == 0;
        this.CopyButton.IsEnabled = has;
        this.ExportButton.IsEnabled = has;
        var recording = this.live is not null;
        var busy = recording || this.jobCancel is not null;
        this.DeleteButton.IsEnabled = document is not null && !busy;
        this.RerunButton.IsEnabled = document is not null && !busy && this.SelectedModel is not null
            && document.Audio.Count > 0 && document.SourceType == TranscriptSourceType.ImportedFile;
        if (document is not null && (busy || document.Status is TranscriptSessionStatus.Failed or TranscriptSessionStatus.Interrupted))
        {
            this.StatusText.Text = $"{document.Title}: {document.Status}";
        }

        if (this.live is { } session)
        {
            this.LevelText.Text = $"Recorded {(int)session.Elapsed.TotalMinutes}:{session.Elapsed.Seconds:00}" + (session.Backlog > TimeSpan.FromSeconds(5) ? $" · model is {session.Backlog.TotalSeconds:0}s behind" : string.Empty);
        }
    }

    private void OnRowFocus(object? sender, RoutedEventArgs e)
    {
        if (e.Source is TextBox { Tag: string id } && this.rowsById.TryGetValue(id, out var row))
        {
            row.IsEditing = true;
        }
    }

    private void OnRowBlur(object? sender, RoutedEventArgs e)
    {
        if (e.Source is TextBox { Tag: string id } box && this.rowsById.TryGetValue(id, out var row))
        {
            row.IsEditing = false;
            var current = this.host?.Document.Segments.FirstOrDefault(s => s.Id == id && s.ResultVersion == this.host.Document.ActiveResultVersion);
            if (current is not null && box.Text is { } text && text != current.DisplayText)
            {
                this.host!.Edit(id, text);
                _ = this.host.CheckpointAsync().AsTask();
            }
        }
    }

    private async Task PickFileAsync()
    {
        var files = await this.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import audio or video",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Audio and video") { Patterns = ["*.wav", "*.mp3", "*.m4a", "*.aac", "*.flac", "*.ogg", "*.opus", "*.wma", "*.mp4", "*.mov", "*.mkv", "*.webm", "*.avi"] },
                FilePickerFileTypes.All
            ]
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
        {
            await this.ImportAsync(path);
        }
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        var file = e.DataTransfer.TryGetFile();
        if (file?.TryGetLocalPath() is { } path)
        {
            await this.ImportAsync(path);
        }
    }

    private async Task ImportAsync(string path)
    {
        if (this.SelectedModel is not { } model)
        {
            this.Say("Install a speech model first.");
            return;
        }

        if (this.jobCancel is not null || this.live is not null)
        {
            this.Say("Wait for the current job to finish, or stop it first.");
            return;
        }

        try
        {
            this.jobCancel = new CancellationTokenSource();
            this.SetJobUi(active: true, canStop: true);
            this.Say($"Reading {Path.GetFileName(path)}…");
            var progress = new Progress<ProgressChanged>(p =>
            {
                if (p.Fraction is { } f)
                {
                    this.Progress.IsIndeterminate = false;
                    this.Progress.Value = f;
                }
            });
            var (newHost, completion) = await this.workspace.ImportAsync(path, model, AudioRetention.KeepAudio, progress, this.jobCancel.Token);
            this.Attach(newHost);
            await this.ReloadSessionsAsync(newHost.Document.SessionId);
            this.Say($"Transcribing with {model.DisplayName}…");
            await this.FinishJobAsync(completion, newHost);
        }
        catch (MediaDecodeException ex)
        {
            this.EndJob();
            this.Say(ex.Message);
        }
    }

    private async Task RerunAsync()
    {
        if (this.host is null || this.SelectedModel is not { } model || this.jobCancel is not null)
        {
            return;
        }

        try
        {
            this.jobCancel = new CancellationTokenSource();
            this.SetJobUi(active: true, canStop: true);
            var target = this.host;
            this.Say($"Rerunning with {model.DisplayName}; earlier results and edits are kept.");
            var progress = new Progress<ProgressChanged>(p => this.Progress.Value = p.Fraction ?? 0);
            await this.FinishJobAsync(this.workspace.RerunAsync(target, model, progress, this.jobCancel.Token), target);
        }
        catch (InvalidOperationException ex)
        {
            this.EndJob();
            this.Say(ex.Message);
        }
    }

    private async Task FinishJobAsync(Task completion, SessionDocumentHost target)
    {
        try
        {
            await completion;
            this.Say(target.Document.Status == TranscriptSessionStatus.Completed ? "Done." : $"Finished: {target.Document.Status}.");
        }
        catch (MediaDecodeException ex)
        {
            this.Say("Could not decode this file: " + ex.Message);
        }
        catch (OperationCanceledException)
        {
            this.Say("Canceled.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or FileNotFoundException)
        {
            this.Say("Transcription failed: " + ex.Message);
        }
        finally
        {
            this.EndJob();
            await this.ReloadSessionsAsync(target.Document.SessionId);
        }
    }

    private void SetJobUi(bool active, bool canStop)
    {
        this.Progress.IsVisible = active;
        this.Progress.IsIndeterminate = active;
        this.ImportButton.IsEnabled = !active;
        this.RecordButton.IsEnabled = !active && this.workspace.CanRecord;
        this.SourceBox.IsEnabled = !active;
        this.StopButton.IsVisible = active && canStop;
        this.StopButton.Content = this.live is null ? "Cancel" : "Stop";
        this.ModelBox.IsEnabled = !active;
    }

    private void EndJob()
    {
        this.jobCancel?.Dispose();
        this.jobCancel = null;
        this.SetJobUi(active: false, canStop: false);
        this.PauseButton.IsVisible = false;
        this.DiscardButton.IsVisible = false;
        this.RecordingIndicator.IsVisible = false;
        this.Refresh();
    }

    private string recordingLabel = "microphone";

    private async Task StartRecordingAsync()
    {
        if (this.SelectedModel is not { } model || this.jobCancel is not null)
        {
            return;
        }

        try
        {
            var mode = (AudioCaptureMode)Math.Max(0, this.SourceBox.SelectedIndex);
            var label = mode switch
            {
                AudioCaptureMode.SystemAudio => "system audio",
                AudioCaptureMode.MicrophoneAndSystemAudio => "microphone and system audio",
                _ => "microphone"
            };
            this.live = await this.workspace.StartLiveAsync(model, null, AudioRetention.KeepAudio, $"{(mode == AudioCaptureMode.Microphone ? "Recording" : "Meeting")} {DateTime.Now:g}", CancellationToken.None, mode);
            this.recordingLabel = label;
            this.RecordingIndicator.Text = "● Recording " + label;
            this.RecordingIndicator.IsVisible = true;
            this.live.Error += this.Say;
            this.live.LevelChanged += level => Dispatcher.UIThread.Post(() => this.LevelText.Text = $"Level {new string('█', (int)Math.Min(20, level * 60))}");
            this.jobCancel = new CancellationTokenSource();
            this.SetJobUi(active: true, canStop: true);
            this.Progress.IsVisible = false;
            this.PauseButton.IsVisible = true;
            this.PauseButton.Content = "Pause";
            this.DiscardButton.IsVisible = true;
            this.Attach(this.live.Host!);
            await this.ReloadSessionsAsync(this.live.Host!.Document.SessionId);
            this.Say("Recording. Text appears as you speak; gray lines can still change.");
        }
        catch (MicrophoneBusyException ex)
        {
            this.Say(ex.Message);
        }
        catch (AudioSourceException ex)
        {
            this.Say(ex.Kind switch
            {
                AudioSourceErrorKind.PermissionDenied => "Audio access was denied. Allow it in your system privacy settings.",
                AudioSourceErrorKind.NotSupported => ex.Message,
                _ => "Audio problem: " + ex.Message
            });
        }
    }

    private async Task TogglePauseAsync()
    {
        if (this.live is not { } session)
        {
            return;
        }

        if (session.Host!.Document.Status == TranscriptSessionStatus.Paused)
        {
            await session.ResumeAsync(CancellationToken.None);
            this.PauseButton.Content = "Pause";
            this.RecordingIndicator.Text = "● Recording " + this.recordingLabel;
            this.Say("Recording.");
        }
        else
        {
            await session.PauseAsync(CancellationToken.None);
            this.PauseButton.Content = "Resume";
            this.RecordingIndicator.Text = "Ⅱ Paused, not recording";
            this.Say("Paused. Nothing is being recorded.");
        }
    }

    private async Task StopRecordingAsync()
    {
        if (this.live is null)
        {
            this.jobCancel?.Cancel();
            return;
        }

        var target = this.host;
        this.Say("Finishing the last words…");
        this.StopButton.IsEnabled = false;
        await this.workspace.StopLiveAsync(CancellationToken.None);
        this.live = null;
        this.StopButton.IsEnabled = true;
        this.EndJob();
        this.Say(target?.Document.Status == TranscriptSessionStatus.Completed ? "Saved." : $"Stopped: {target?.Document.Status}. What was captured is kept.");
        await this.ReloadSessionsAsync(target?.Document.SessionId);
    }

    private async Task DiscardRecordingAsync()
    {
        var id = this.host?.Document.SessionId;
        await this.workspace.DiscardLiveAsync();
        this.live = null;
        this.EndJob();
        this.Say("Recording canceled. You can delete it from the list if you don't want to keep it.");
        await this.ReloadSessionsAsync(id);
    }

    private async Task CopyAsync()
    {
        if (this.host is null || this.Clipboard is null)
        {
            return;
        }

        var text = TranscriptExporter.Export(this.host.Document, new ExportOptions(ExportFormat.Text, ExportTextSource.Edited, IncludeTimestamps: false));
        await this.Clipboard.SetTextAsync(text);
        this.Say("Copied. Nothing was typed anywhere.");
    }

    private async Task ExportAsync()
    {
        if (this.host is null)
        {
            return;
        }

        var format = Formats[Math.Max(0, this.FormatBox.SelectedIndex)];
        var extension = TranscriptExporter.FileExtension(format).TrimStart('.');
        var file = await this.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export transcript",
            SuggestedFileName = SafeName(this.host.Document.Title) + "." + extension,
            DefaultExtension = extension
        });
        if (file?.TryGetLocalPath() is not { } path)
        {
            return;
        }

        try
        {
            await TranscriptionWorkspaceService.ExportAsync(this.host.Document, path, new ExportOptions(format), CancellationToken.None);
            this.Say($"Exported to {path}");
        }
        catch (IOException ex)
        {
            this.Say("Export failed: " + ex.Message);
        }
    }

    private async Task DeleteAsync()
    {
        if (this.host is null)
        {
            return;
        }

        var result = await this.workspace.DeleteSessionAsync(this.host.Document.SessionId, CancellationToken.None);
        this.Attach(null);
        await this.ReloadSessionsAsync();
        this.Say(result.KeptExternalFiles.Count > 0
            ? $"Session deleted. Your original file was not touched: {result.KeptExternalFiles[0]}"
            : "Session deleted.");
    }

    private static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return cleaned.Length == 0 ? "transcript" : cleaned;
    }
}
