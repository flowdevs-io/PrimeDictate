using PrimeDictate.Core.Audio;

namespace PrimeDictate.Core.Providers;

public enum AudioSourceErrorKind
{
    PermissionDenied = 0,
    DeviceRemoved = 1,
    DeviceChanged = 2,
    FormatUnsupported = 3,
    Unknown = 4
}

public sealed class AudioSourceException(AudioSourceErrorKind kind, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public AudioSourceErrorKind Kind { get; } = kind;
}

/// <summary>
/// An exclusive lease on a capture device. Disposing the lease stops capture and releases
/// the device. <see cref="ReadFramesAsync"/> completes when capture stops and throws
/// <see cref="AudioSourceException"/> on device errors.
/// </summary>
public interface IAudioCaptureLease : IAsyncDisposable
{
    AudioFormat Format { get; }

    string DeviceId { get; }

    string DeviceName { get; }

    IAsyncEnumerable<AudioFrame> ReadFramesAsync(CancellationToken cancellationToken);

    /// <summary>Stops delivery without releasing the device. Frames after resume continue the sequence.</summary>
    ValueTask PauseAsync(CancellationToken cancellationToken);

    ValueTask ResumeAsync(CancellationToken cancellationToken);
}

public sealed record AudioInputDevice(string Id, string Name, bool IsDefault);

public interface IAudioSource
{
    ValueTask<IReadOnlyList<AudioInputDevice>> ListDevicesAsync(CancellationToken cancellationToken);

    /// <summary>Opens a device. Null selects the system default.</summary>
    ValueTask<IAudioCaptureLease> OpenAsync(string? deviceId, CancellationToken cancellationToken);
}

public sealed record MediaStreamInfo(int Index, string Codec, int SampleRate, int Channels, string? Language, string? Title);

public sealed record MediaProbeResult(
    string ContainerFormat,
    TimeSpan? Duration,
    IReadOnlyList<MediaStreamInfo> AudioStreams);

public sealed class MediaDecodeException(string errorCode, string message, Exception? inner = null)
    : Exception(message, inner)
{
    /// <summary>Stable code for UI and diagnostics, for example "unsupported-format" or "corrupt".</summary>
    public string ErrorCode { get; } = errorCode;
}

/// <summary>
/// Decodes local media incrementally. Implementations treat input as untrusted, never build
/// shell commands from paths, and never reach the network.
/// </summary>
public interface IAudioDecoder
{
    ValueTask<MediaProbeResult> ProbeAsync(string path, CancellationToken cancellationToken);

    /// <summary>
    /// Streams the selected audio stream as frames in the source's native rate and channel
    /// layout. Offsets are counted from the start of the stream.
    /// </summary>
    IAsyncEnumerable<AudioFrame> DecodeAsync(string path, int streamIndex, CancellationToken cancellationToken);
}

/// <summary>Plays session audio. Positions are on the recorded-audio timeline.</summary>
public interface IAudioPlayback : IAsyncDisposable
{
    ValueTask OpenAsync(string path, CancellationToken cancellationToken);

    ValueTask PlayAsync(TimeSpan from, TimeSpan? until, CancellationToken cancellationToken);

    ValueTask PauseAsync(CancellationToken cancellationToken);

    TimeSpan Position { get; }
}
