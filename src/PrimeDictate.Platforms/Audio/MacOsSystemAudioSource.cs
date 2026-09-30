using PrimeDictate.Core.Providers;

namespace PrimeDictate.Platforms.Audio;

/// <summary>
/// Placeholder for macOS system-audio capture. It is not implemented and has not been run on a Mac.
/// Opening it always throws <see cref="AudioSourceErrorKind.NotSupported"/> with a message the UI can show.
/// </summary>
/// <remarks>
/// Researched options (from Apple documentation, not verified on hardware) are in
/// docs/architecture/system-audio-capture.md: Core Audio process taps (macOS 14.2+, needs the "System
/// Audio Recording Only" privacy permission and an NSAudioCaptureUsageDescription string), ScreenCaptureKit
/// (macOS 13+, needs Screen Recording permission), or a user-installed loopback device such as BlackHole,
/// which already appears as an ordinary microphone.
/// </remarks>
public sealed class MacOsSystemAudioSource : ISystemAudioSource
{
    public const string Message =
        "System audio capture is not available on macOS yet. To record a call today, route its output through a " +
        "loopback device such as BlackHole and pick that device as the microphone.";

    public ValueTask<IReadOnlyList<AudioInputDevice>> ListDevicesAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<AudioInputDevice>>([]);

    public ValueTask<IAudioCaptureLease> OpenAsync(string? deviceId, CancellationToken cancellationToken) =>
        throw new AudioSourceException(AudioSourceErrorKind.NotSupported, Message);
}
