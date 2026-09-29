using System.ComponentModel;
using Avalonia.Media;
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

    /// <summary>Color chip matching the speaker's timeline lane; transparent without speakers.</summary>
    public IBrush SpeakerBrush { get; private set; } = Brushes.Transparent;

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

    /// <summary>The segments this row shows. A turn is consecutive lines from one speaker; the first id is the row's own.</summary>
    public IReadOnlyList<string> SegmentIds { get; private set; } = [id];

    public void Update(TranscriptSegment segment, IReadOnlyList<TranscriptSpeaker> speakers) =>
        this.UpdateTurn([segment], speakers, null);

    /// <param name="guessedSpeakerId">
    /// For a live line that has no speaker yet: the speaker of the line just before it, shown with a question mark
    /// because a new commit may turn out to be someone else.
    /// </param>
    public void UpdateTurn(IReadOnlyList<TranscriptSegment> segments, IReadOnlyList<TranscriptSpeaker> speakers, string? guessedSpeakerId)
    {
        var first = segments[0];
        this.SegmentIds = segments.Select(s => s.Id).ToList();
        var known = first.Speakers.Count > 0 ? TranscriptDocument.ResolveSpeakerId(speakers, first.Speakers[0].SpeakerId) : null;
        var id = known ?? guessedSpeakerId;
        var name = id is null ? string.Empty : speakers.FirstOrDefault(s => s.Id == id)?.Name ?? id;
        var index = id is null ? -1 : speakers.ToList().FindIndex(s => s.Id == id);
        var brush = index < 0 ? Brushes.Transparent : SpeakerPalette.BrushFor(index);
        if (!ReferenceEquals(brush, this.SpeakerBrush) && index >= 0)
        {
            this.SpeakerBrush = brush;
            this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.SpeakerBrush)));
        }

        var provisional = segments.Any(s => s.State != SegmentState.Final);
        // Live labels arrive only when an utterance completes, so a live line either carries a guess ("Name?")
        // or, when there is nothing to guess from, reads "speaker pending".
        this.Speaker = name.Length > 0
            ? (provisional || known is null ? name + "?" : name)
            : provisional && speakers.Count > 0 ? "speaker pending" : string.Empty;
        var last = segments.OrderBy(s => s.End).Last();
        this.Time = last.End > first.Start + TimeSpan.FromSeconds(0.5) ? $"{FormatTime(first.Start)}–{FormatTime(last.End)}" : FormatTime(first.Start);
        this.Provisional = provisional;
        if (!this.IsEditing)
        {
            this.Text = string.Join(' ', segments.Select(s => s.DisplayText).Where(t => t.Length > 0));
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
