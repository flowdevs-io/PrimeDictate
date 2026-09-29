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
    // rowsByLead keeps a row's identity while its turn grows; rowsById finds the row that shows any segment.
    private readonly Dictionary<string, SegmentRow> rowsByLead = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SegmentRow> rowsById = new(StringComparer.Ordinal);

    /// <summary>Lines from the same speaker closer together than this read as one turn, like the demo's transcript.</summary>
    private static readonly TimeSpan TurnGap = TimeSpan.FromSeconds(5);

    private static readonly List<(string Name, string Code)> Languages =
    [
        ("English (US)", "en-US"), ("English (UK)", "en-GB"), ("Spanish", "es-ES"), ("French", "fr-FR"), ("German", "de-DE"),
        ("Italian", "it-IT"), ("Portuguese (Brazil)", "pt-BR"), ("Japanese", "ja-JP"), ("Chinese", "zh-CN"), ("Auto-detect", "auto")
    ];

    private IReadOnlyList<SpeechModelChoice> models = [];
    private SessionDocumentHost? host;
    private CancellationTokenSource? jobCancel;
    private LiveTranscriptionSession? live;
    private int refreshQueued;
    private bool stickToBottom = true;
    private bool scrollingProgrammatically;

    public MainWindow()
    {
        this.InitializeComponent();
        this.PlatformText.Text = $"{RuntimeInformation.OSDescription} · {RuntimeInformation.ProcessArchitecture} · .NET {Environment.Version}";
        var dataDir = Environment.GetEnvironmentVariable("PRIMEDICTATE_DATA_DIR");
        this.workspace = new TranscriptionWorkspaceService(string.IsNullOrWhiteSpace(dataDir) ? null : new AppDataPaths(dataDir));
        this.TranscriptList.ItemsSource = this.rows;
        this.workspace.Notice += this.Say;
        this.LanguageBox.ItemsSource = Languages.Select(l => l.Name).ToList();
        this.LanguageBox.SelectedIndex = Math.Max(0, Languages.FindIndex(l => string.Equals(l.Code, this.workspace.Language, StringComparison.OrdinalIgnoreCase)));
        this.LanguageBox.SelectionChanged += (_, _) =>
        {
            if (this.LanguageBox.SelectedIndex is >= 0 and var i && i < Languages.Count)
            {
                this.workspace.Language = Languages[i].Code;
            }
        };

        this.ImportButton.Click += async (_, _) => await this.RunSafelyAsync(() => this.PickFileAsync());
        this.SourceBox.ItemsSource = new[] { "Microphone", "System audio (speakers)", "Microphone + system audio" };
        this.SourceBox.SelectedIndex = 0;
        this.RecordButton.Click += async (_, _) => await this.RunSafelyAsync(() => this.StartRecordingAsync());
        this.PauseButton.Click += async (_, _) => await this.RunSafelyAsync(() => this.TogglePauseAsync());
        this.StopButton.Click += async (_, _) => await this.RunSafelyAsync(() => this.StopRecordingAsync());
        this.DiscardButton.Click += async (_, _) => await this.RunSafelyAsync(() => this.DiscardRecordingAsync());
        this.RerunButton.Click += async (_, _) => await this.RunSafelyAsync(() => this.RerunAsync());
        this.CopyButton.Click += async (_, _) => await this.RunSafelyAsync(() => this.CopyAsync());
        this.ExportButton.Click += async (_, _) => await this.RunSafelyAsync(() => this.ExportAsync());
        this.DeleteButton.Click += async (_, _) => await this.RunSafelyAsync(() => this.DeleteAsync());
        this.SessionList.SelectionChanged += async (_, _) => await this.RunSafelyAsync(() => this.OpenSelectedAsync());
        this.Timeline.SegmentClicked += this.OnTimelineSegmentClicked;
        this.Timeline.FollowChanged += this.UpdateJumpButton;
        this.JumpToLiveButton.Click += (_, _) =>
        {
            this.Timeline.FollowLive();
            this.stickToBottom = true;
            this.ScrollTranscriptToEnd();
        };
        this.TranscriptList.AttachedToVisualTree += (_, _) =>
        {
            if (this.TranscriptList.Scroll is ScrollViewer viewer)
            {
                viewer.ScrollChanged += (_, _) =>
                {
                    // The user scrolling up stops the automatic scroll; reaching the bottom again resumes it.
                    if (!this.scrollingProgrammatically)
                    {
                        this.stickToBottom = viewer.Offset.Y + viewer.Viewport.Height >= viewer.Extent.Height - 8;
                        this.UpdateJumpButton();
                    }
                };
            }
        };
        this.TranscriptList.SelectionChanged += (_, _) => this.Timeline.SelectedSegmentId = (this.TranscriptList.SelectedItem as SegmentRow)?.Id;
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

    /// <summary>One failed action reports itself in the status line instead of taking the whole app down.</summary>
    private async Task RunSafelyAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            this.Say("That did not work: " + ex.Message);
        }
    }

    /// <summary>Which model and backend the shown run used, kept on screen (unlike the status line, which the next message replaces).</summary>
    private void RefreshBackendBadge(TranscriptDocument? document)
    {
        var run = document?.Runs.LastOrDefault(r => r.ResultVersion == document.ActiveResultVersion);
        this.BackendBadge.IsVisible = run is not null;
        if (run is null)
        {
            return;
        }

        var model = run.AsrModelId.StartsWith("nemotron:", StringComparison.Ordinal) ? "Nemotron" : "Whisper";
        var fellBack = !string.Equals(run.RequestedBackend, run.EffectiveBackend, StringComparison.OrdinalIgnoreCase) && run.RequestedBackend != "cpu";
        this.BackendText.Text = $"{model} on {run.EffectiveBackend}" + (fellBack ? $" (wanted {run.RequestedBackend})" : string.Empty);
        this.BackendBadge.Opacity = fellBack ? 1 : 0.8;
    }

    private void Say(string message) => Dispatcher.UIThread.Post(() => this.StatusText.Text = message);

    private async Task InitializeWorkspaceAsync()
    {
        try
        {
            await this.workspace.InitializeAsync(CancellationToken.None);
            this.models = this.workspace.AvailableModels();
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

    private SpeechModelChoice? SelectedModel => this.ModelBox.SelectedIndex is >= 0 and var i && i < this.models.Count ? this.models[i] : null;

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
        this.rowsByLead.Clear();
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
        this.rowsById.Clear();
        if (document is not null)
        {
            foreach (var turn in TranscriptTurns.Group(document, TurnGap))
            {
                var lead = turn.Segments[0].Id;
                if (!this.rowsByLead.TryGetValue(lead, out var row))
                {
                    row = new SegmentRow(lead);
                    this.rowsByLead[lead] = row;
                }

                row.UpdateTurn(turn.Segments, document.Speakers, turn.GuessedSpeakerId);
                foreach (var segment in turn.Segments)
                {
                    this.rowsById[segment.Id] = row;
                }

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

        this.RefreshBackendBadge(document);
        this.RefreshSpeakers(document);
        this.Timeline.SetDocument(document, this.live is not null);
        this.Timeline.Height = document is { Speakers.Count: > 0 } ? this.Timeline.DesiredContentHeight : 0;
        if (this.live is not null && this.stickToBottom)
        {
            this.ScrollTranscriptToEnd();
        }

        this.UpdateJumpButton();
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

    private readonly Dictionary<string, TextBox> speakerBoxes = new(StringComparer.Ordinal);

    /// <summary>
    /// One editable name per detected speaker, usable while a live recording runs. A rename only
    /// changes the name mapping, so every segment keeps its speaker id.
    /// </summary>
    private void RefreshSpeakers(TranscriptDocument? document)
    {
        var all = document?.Speakers ?? [];
        var speakers = all.Where(s => s.MergedIntoId is null).ToList();
        this.SpeakersPanel.IsVisible = all.Count > 0;
        if (!speakers.Select(s => s.Id).SequenceEqual(this.speakerBoxes.Keys))
        {
            this.speakerBoxes.Clear();
            this.SpeakerBoxes.Children.Clear();
            foreach (var speaker in speakers)
            {
                var box = new TextBox { Width = 140, Tag = speaker.Id, PlaceholderText = speaker.DefaultLabel, Text = speaker.DisplayName ?? string.Empty };
                box.LostFocus += (_, _) => this.CommitSpeakerName(box);
                box.KeyDown += (_, e) =>
                {
                    if (e.Key == Key.Enter)
                    {
                        this.CommitSpeakerName(box);
                        this.TranscriptList.Focus();
                    }
                };
                var more = new Button { Content = "Merge…", Tag = speaker.Id, Padding = new Thickness(6, 2), Margin = new Thickness(4, 0, 0, 0) };
                ToolTip.SetTip(more, "Show another speaker as this one (or split one back out). Nothing in the transcript is rewritten.");
                more.Click += (_, _) => this.OpenMergeMenu(more);
                this.speakerBoxes[speaker.Id] = box;
                this.SpeakerBoxes.Children.Add(new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Children = { box, more } });
            }

            return;
        }

        // While recording, outline the box of whoever spoke most recently, in their timeline color.
        var latest = this.live is null || document is null ? null : document.ActiveSegments.LastOrDefault(s => s.Speakers.Count > 0) is { } last ? document.ResolveSpeakerId(last.Speakers[0].SpeakerId) : null;
        foreach (var speaker in speakers)
        {
            var active = speaker.Id == latest;
            var chip = this.speakerBoxes[speaker.Id];
            chip.BorderBrush = active ? SpeakerPalette.BrushFor(all.ToList().FindIndex(s => s.Id == speaker.Id)) : null;
            chip.BorderThickness = new Avalonia.Thickness(active ? 2 : 1);
            if (!chip.IsFocused)
            {
                chip.Text = speaker.DisplayName ?? string.Empty;
            }
        }
    }

    /// <summary>Merging only changes the speaker mapping, like renaming, so it is safe while recording and undoable.</summary>
    private void OpenMergeMenu(Button button)
    {
        if (this.host is null || button.Tag is not string id)
        {
            return;
        }

        var document = this.host.Document;
        var items = new List<MenuItem>();
        foreach (var other in document.VisibleSpeakers.Where(s => s.Id != id))
        {
            var item = new MenuItem { Header = $"Show {other.Name} as this speaker" };
            var otherId = other.Id;
            item.Click += (_, _) => this.ChangeSpeakers(h => h.MergeSpeaker(otherId, id));
            items.Add(item);
        }

        foreach (var merged in document.Speakers.Where(s => s.MergedIntoId == id))
        {
            var item = new MenuItem { Header = $"Split {merged.Name} back out" };
            var mergedId = merged.Id;
            item.Click += (_, _) => this.ChangeSpeakers(h => h.UnmergeSpeaker(mergedId));
            items.Add(item);
        }

        if (items.Count == 0)
        {
            items.Add(new MenuItem { Header = "No other speakers yet", IsEnabled = false });
        }

        new ContextMenu { ItemsSource = items }.Open(button);
    }

    private void ChangeSpeakers(Action<SessionDocumentHost> change)
    {
        if (this.host is null)
        {
            return;
        }

        change(this.host);
        _ = this.host.CheckpointAsync().AsTask();
    }

    private void CommitSpeakerName(TextBox box)
    {
        if (this.host is null || box.Tag is not string id)
        {
            return;
        }

        var current = this.host.Document.Speakers.FirstOrDefault(s => s.Id == id)?.DisplayName ?? string.Empty;
        var name = box.Text?.Trim() ?? string.Empty;
        if (name != current)
        {
            this.host.RenameSpeaker(id, name);
            _ = this.host.CheckpointAsync().AsTask();
        }
    }

    private void ScrollTranscriptToEnd()
    {
        if (this.rows.Count == 0)
        {
            return;
        }

        var last = this.rows[^1];
        Dispatcher.UIThread.Post(() =>
        {
            this.scrollingProgrammatically = true;
            try
            {
                this.TranscriptList.ScrollIntoView(last);
            }
            finally
            {
                this.scrollingProgrammatically = false;
            }
        });
    }

    private void UpdateJumpButton() =>
        this.JumpToLiveButton.IsVisible = this.live is not null && (!this.Timeline.IsFollowing || !this.stickToBottom);

    private void OnTimelineSegmentClicked(string segmentId)
    {
        if (this.rowsById.TryGetValue(segmentId, out var row) && this.rows.Contains(row))
        {
            this.TranscriptList.SelectedItem = row;
            this.TranscriptList.ScrollIntoView(row);
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
            var document = this.host?.Document;
            var shown = document?.Segments.Where(s => s.ResultVersion == document.ActiveResultVersion && row.SegmentIds.Contains(s.Id)).OrderBy(s => s.Start).ToList();
            if (shown is { Count: > 0 } && box.Text is { } text)
            {
                var current = string.Join(' ', shown.Select(s => s.DisplayText).Where(t => t.Length > 0));
                if (text != current)
                {
                    // A turn made of several lines is edited as one block: the text goes on its first line and the
                    // others are emptied (raw recognition text is kept, and exports skip empty lines).
                    this.host!.Edit(shown[0].Id, text);
                    foreach (var rest in shown.Skip(1))
                    {
                        this.host.Edit(rest.Id, string.Empty);
                    }

                    _ = this.host.CheckpointAsync().AsTask();
                }
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
        this.AutoGainBox.IsEnabled = !active;
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
            var mode = this.SourceBox.SelectedIndex switch { 1 => TranscriptSourceType.SystemAudio, 2 => TranscriptSourceType.Meeting, _ => TranscriptSourceType.Microphone };
            var label = mode switch
            {
                TranscriptSourceType.SystemAudio => "system audio",
                TranscriptSourceType.Meeting => "microphone and system audio",
                _ => "microphone"
            };
            this.live = await this.workspace.StartLiveAsync(model, null, AudioRetention.KeepAudio, $"{(mode == TranscriptSourceType.Microphone ? "Recording" : "Meeting")} {DateTime.Now:g}", CancellationToken.None, mode, null, this.AutoGainBox.IsChecked == true);
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
            this.stickToBottom = true;
            this.Timeline.FollowLive();
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

        var wasRecording = this.workspace.IsRecording(this.host.Document.SessionId);
        var result = await this.workspace.DeleteSessionAsync(this.host.Document.SessionId, CancellationToken.None);
        if (wasRecording)
        {
            this.live = null;
            this.EndJob();
        }

        this.Attach(null);
        await this.ReloadSessionsAsync();
        this.Say(result.FailedFiles.Count > 0
            ? $"Session deleted, but a file is still in use and was left behind: {result.FailedFiles[0]}"
            : result.KeptExternalFiles.Count > 0
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
