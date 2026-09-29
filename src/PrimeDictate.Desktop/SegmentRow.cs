using System.ComponentModel;
using PrimeDictate.Core.Transcripts;

namespace PrimeDictate.Desktop;

/// <summary>One transcript line as the list shows it. Text edits flow back to the session document.</summary>
public sealed class SegmentRow(string id) : INotifyPropertyChanged
{
    private string text = string.Empty;
    private string time = string.Empty;
    private bool provisional;
    private string speaker = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id { get; } = id;

    public bool IsEditing { get; set; }

    public string Text
    {
        get => this.text;
        set => this.Set(ref this.text, value, nameof(this.Text));
    }

    public string Time
    {
        get => this.time;
        set => this.Set(ref this.time, value, nameof(this.Time));
    }

    /// <summary>Speaker name, empty when the session has no speaker detection. Live labels end in "?" until final.</summary>
    public string Speaker
    {
        get => this.speaker;
        set
        {
            if (this.Set(ref this.speaker, value, nameof(this.Speaker)))
            {
                this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.HasSpeaker)));
            }
        }
    }

    public bool HasSpeaker => this.speaker.Length > 0;

    /// <summary>True while a live utterance may still change.</summary>
    public bool Provisional
    {
        get => this.provisional;
        set
        {
            if (this.Set(ref this.provisional, value, nameof(this.Provisional)))
            {
                this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.Opacity)));
            }
        }
    }

    public double Opacity => this.provisional ? 0.6 : 1.0;

    public void Update(TranscriptSegment segment, IReadOnlyList<TranscriptSpeaker> speakers)
    {
        var id = segment.Speakers.Count > 0 ? segment.Speakers[0].SpeakerId : null;
        var name = id is null ? string.Empty : speakers.FirstOrDefault(s => s.Id == id)?.Name ?? id;
        this.Speaker = name.Length > 0 && segment.State != SegmentState.Final ? name + "?" : name;
        this.Time = FormatTime(segment.Start);
        this.Provisional = segment.State != SegmentState.Final;
        if (!this.IsEditing)
        {
            this.Text = segment.DisplayText;
        }
    }

    private static string FormatTime(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";

    private bool Set<T>(ref T field, T value, string name)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}

public sealed record SessionItem(Guid Id, string Title, string Detail)
{
    public override string ToString() => this.Title;
}
