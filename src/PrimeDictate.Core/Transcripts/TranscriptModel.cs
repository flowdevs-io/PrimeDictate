namespace PrimeDictate.Core.Transcripts;

/// <summary>Where a segment's or word's timing came from.</summary>
public enum TimingProvenance
{
    /// <summary>Timing emitted by the recognition model itself.</summary>
    Model = 0,

    /// <summary>Timing produced by a separate forced-alignment step.</summary>
    Alignment = 1,

    /// <summary>
    /// Only the boundaries of the audio window that was recognized are known. The words fall
    /// somewhere inside; exports must present this timing as approximate.
    /// </summary>
    ApproximateChunk = 2
}

public enum SegmentState
{
    Provisional = 0,
    Final = 1
}

public enum TranscriptSourceType
{
    ImportedFile = 0,
    Microphone = 1,

    /// <summary>Audio the computer is playing (loopback), for example the remote side of a call.</summary>
    SystemAudio = 2,

    /// <summary>Microphone and system audio together. Stereo working audio: left is local, right is remote.</summary>
    Meeting = 3
}

public enum TranscriptSessionStatus
{
    Created = 0,
    Running = 1,
    Paused = 2,
    Finalizing = 3,
    Completed = 4,
    Failed = 5,
    Canceled = 6,
    /// <summary>The app stopped while the session was running; finalized content is intact.</summary>
    Interrupted = 7
}

public sealed record WordTiming(
    string Text,
    TimeSpan Start,
    TimeSpan End,
    double? Confidence,
    TimingProvenance Provenance,
    /// <summary>Speaker the recognizer gave this word, when it tags words. Sessions saved before this field have none.</summary>
    string? SpeakerId = null,
    /// <summary>
    /// The word is not shown: the microphone picked it up from the speakers. The recognized text and the word stay stored,
    /// so a wrong call can be undone.
    /// </summary>
    bool Hidden = false);

/// <summary>A speaker's share of a segment; several entries represent overlap or uncertainty.</summary>
public sealed record SpeakerAttribution(string SpeakerId, TimeSpan Start, TimeSpan End, double? Confidence);

public sealed record TranscriptSegment
{
    public required string Id { get; init; }

    public required TimeSpan Start { get; init; }

    public required TimeSpan End { get; init; }

    /// <summary>Recognizer output exactly as produced, never modified by edits.</summary>
    public required string RawText { get; init; }

    /// <summary>User edit, if any. Null means the raw text is displayed.</summary>
    public string? EditedText { get; init; }

    public SegmentState State { get; init; }

    /// <summary>Monotonic per-segment revision; an update with a lower or equal revision is ignored.</summary>
    public long Revision { get; init; }

    public IReadOnlyList<WordTiming>? Words { get; init; }

    /// <summary>Null when the provider does not report confidence.</summary>
    public double? Confidence { get; init; }

    public IReadOnlyList<SpeakerAttribution> Speakers { get; init; } = [];

    public required TimingProvenance TimingProvenance { get; init; }

    /// <summary>Which recognition run produced this segment.</summary>
    public int ResultVersion { get; init; } = 1;

    /// <summary>What the row shows: the user's edit, else the recognized text without any words hidden as microphone echo.</summary>
    public string DisplayText => this.EditedText ?? (this.HasHiddenWords ? string.Join(' ', this.Words!.Where(w => !w.Hidden).Select(w => w.Text)) : this.RawText);

    public bool HasHiddenWords => this.Words?.Any(w => w.Hidden) == true;

    /// <summary>Start of the first word that is shown; the segment start when nothing is hidden.</summary>
    public TimeSpan DisplayStart => this.EditedText is null && this.HasHiddenWords ? this.Words!.FirstOrDefault(w => !w.Hidden)?.Start ?? this.Start : this.Start;

    public TimeSpan DisplayEnd => this.EditedText is null && this.HasHiddenWords ? this.Words!.LastOrDefault(w => !w.Hidden)?.End ?? this.End : this.End;

    public bool IsEdited => this.EditedText is not null && !string.Equals(this.EditedText, this.RawText, StringComparison.Ordinal);
}

/// <summary>Anonymous speaker found by diarization. Renaming changes only <see cref="DisplayName"/>.</summary>
public sealed record TranscriptSpeaker(string Id, string DefaultLabel, string? DisplayName)
{
    /// <summary>
    /// When set, this speaker is shown as another one. Segments keep their original speaker id; merging only
    /// changes how they are grouped, named and colored, and can be undone.
    /// </summary>
    public string? MergedIntoId { get; init; }

    public string Name => string.IsNullOrWhiteSpace(this.DisplayName) ? this.DefaultLabel : this.DisplayName;
}

public sealed record MediaMetadata(
    string? OriginalFileName,
    string? ContainerFormat,
    string? Codec,
    int? SampleRate,
    int? Channels,
    int? SelectedTrack,
    string? ChannelMapping,
    TimeSpan? Duration);

/// <summary>How the session relates to audio it can play back.</summary>
public enum AudioReferenceKind
{
    /// <summary>Audio recorded or copied into app storage, owned and deleted with the session.</summary>
    Owned = 0,

    /// <summary>User's original file, referenced in place. Never deleted by the app.</summary>
    ExternalReference = 1
}

public sealed record AudioReference(AudioReferenceKind Kind, string Path, string? Sha256);

/// <summary>Everything that determines a run's result, captured when the run starts.</summary>
public sealed record RecognitionRunInfo(
    int ResultVersion,
    string AsrModelId,
    string? AsrModelRevision,
    string? DiarizerModelId,
    string? DiarizerModelRevision,
    string? Language,
    string RuntimeName,
    string? RuntimeVersion,
    string RequestedBackend,
    string EffectiveBackend,
    DateTimeOffset StartedAt,
    /// <summary>
    /// The run already cut its segments where rows end (the after-Stop meeting pass: at speaker changes, pauses and where the
    /// other side starts talking), so each segment is shown as its own row, as exports list them. Older runs read false.
    /// </summary>
    bool SegmentsAreRows = false);

public sealed record TranscriptDocument
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public required Guid SessionId { get; init; }

    public required string Title { get; init; }

    public required TranscriptSourceType SourceType { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    public MediaMetadata? Media { get; init; }

    public TimeSpan? Duration { get; init; }

    public string? Language { get; init; }

    public TranscriptSessionStatus Status { get; init; }

    public IReadOnlyList<AudioReference> Audio { get; init; } = [];

    public IReadOnlyList<RecognitionRunInfo> Runs { get; init; } = [];

    /// <summary>The run whose segments are shown; older runs' segments are kept, not overwritten.</summary>
    public int ActiveResultVersion { get; init; } = 1;

    public IReadOnlyList<TranscriptSpeaker> Speakers { get; init; } = [];

    /// <summary>Why the session is in the Failed state. Null otherwise.</summary>
    public string? FailureReason { get; init; }

    /// <summary>Things that happened during the session the reader should know, such as a recognizer restart.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>Speakers that are not merged into another one.</summary>
    public IEnumerable<TranscriptSpeaker> VisibleSpeakers => this.Speakers.Where(s => s.MergedIntoId is null);

    /// <summary>The speaker a segment's speaker id is shown as, following merges.</summary>
    public string ResolveSpeakerId(string id) => ResolveSpeakerId(this.Speakers, id);

    public static string ResolveSpeakerId(IReadOnlyList<TranscriptSpeaker> speakers, string id)
    {
        for (var hops = 0; hops <= speakers.Count; hops++)
        {
            var next = speakers.FirstOrDefault(s => s.Id == id)?.MergedIntoId;
            if (next is null)
            {
                return id;
            }

            id = next;
        }

        return id;
    }

    public IReadOnlyList<TranscriptSegment> Segments { get; init; } = [];

    public IEnumerable<TranscriptSegment> ActiveSegments =>
        this.Segments.Where(s => s.ResultVersion == this.ActiveResultVersion).OrderBy(s => s.Start).ThenBy(s => s.Id, StringComparer.Ordinal);
}
