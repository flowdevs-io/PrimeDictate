using PrimeDictate.Core.Audio;

namespace PrimeDictate.Core.Providers;

public enum AudioSourceErrorKind
{
    PermissionDenied = 0,
    DeviceRemoved = 1,
    DeviceChanged = 2,
    FormatUnsupported = 3,
    Unknown = 4,

    /// <summary>This platform has no implementation of the requested source (for example system audio on macOS).</summary>
    NotSupported = 5
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

    /// <summary>The access the device was actually opened with (an exclusive request can fall back to shared).</summary>
    MicAccessMode AccessMode => MicAccessMode.Shared;

    IAsyncEnumerable<AudioFrame> ReadFramesAsync(CancellationToken cancellationToken);

    /// <summary>Stops delivery without releasing the device. Frames after resume continue the sequence.</summary>
    ValueTask PauseAsync(CancellationToken cancellationToken);

    ValueTask ResumeAsync(CancellationToken cancellationToken);
}

public enum AudioDeviceKind
{
    Microphone = 0,

    /// <summary>An output endpoint whose playback is captured (WASAPI loopback, PulseAudio monitor).</summary>
    SystemAudio = 1
}

public sealed record AudioInputDevice(string Id, string Name, bool IsDefault, AudioDeviceKind Kind = AudioDeviceKind.Microphone);

/// <summary>
/// Captures what the computer is playing (speakers or headphones), so remote participants of a call
/// can be transcribed. Devices are output endpoints. Sources must only start on an explicit user
/// action, must never persist capture across sessions, and never send audio off the device.
/// </summary>
public interface ISystemAudioSource : IAudioSource;

public interface IAudioSource
{
    ValueTask<IReadOnlyList<AudioInputDevice>> ListDevicesAsync(CancellationToken cancellationToken);

    /// <summary>Opens a device. Null selects the system default.</summary>
    ValueTask<IAudioCaptureLease> OpenAsync(string? deviceId, CancellationToken cancellationToken);

    /// <summary>
    /// Opens a device asking for <paramref name="access"/>. Exclusive is a request: a source that cannot honour it
    /// (not WASAPI, or the device refuses) opens shared, and the lease's <see cref="IAudioCaptureLease.AccessMode"/> says which.
    /// </summary>
    ValueTask<IAudioCaptureLease> OpenAsync(string? deviceId, MicAccessMode access, CancellationToken cancellationToken) =>
        this.OpenAsync(deviceId, cancellationToken);
}

/// <summary>How a capture device is opened. Exclusive keeps other apps off the microphone while it is open (WASAPI).</summary>
public enum MicAccessMode
{
    Shared = 0,
    Exclusive = 1
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
