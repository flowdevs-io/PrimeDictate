namespace PrimeDictate.Core.Sessions;

public enum AudioRetention
{
    /// <summary>Keep session audio for playback and reruns.</summary>
    KeepAudio = 0,

    /// <summary>Delete owned audio once the transcript is complete. Playback and reruns become unavailable.</summary>
    TranscriptOnly = 1
}

public enum DownmixMode
{
    Average = 0,
    SingleChannel = 1
}

/// <summary>
/// Immutable options captured when a session or rerun starts. Settings changes after this
/// point never affect a running session.
/// </summary>
public sealed record TranscriptionSessionOptions(
    string AsrModelId,
    string? AsrModelRevision,
    string RequestedBackend,
    string? Language,
    string? DiarizerModelId,
    AudioRetention AudioRetention,
    DownmixMode Downmix,
    int? SourceChannel,
    int? SourceTrack,
    string? InputDeviceId);
