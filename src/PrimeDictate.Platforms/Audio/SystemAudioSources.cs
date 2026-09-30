using PrimeDictate.Core.Providers;

namespace PrimeDictate.Platforms.Audio;

/// <summary>Picks the system-audio adapter for the current platform.</summary>
public static class SystemAudioSources
{
    /// <summary>
    /// Returns the adapter for this platform, or null with a reason the UI can show. macOS returns
    /// the stub, which is reported unavailable rather than pretending to work.
    /// </summary>
    public static ISystemAudioSource? TryCreate(out string? unavailableReason)
    {
        unavailableReason = null;
        if (OperatingSystem.IsWindows())
        {
            return new WasapiLoopbackCaptureSource();
        }

        if (OperatingSystem.IsLinux())
        {
            if (PulseMonitorCaptureSource.IsAvailable())
            {
                return new PulseMonitorCaptureSource();
            }

            unavailableReason = "System audio capture needs PulseAudio or PipeWire's PulseAudio support (libpulse).";
            return null;
        }

        unavailableReason = OperatingSystem.IsMacOS() ? MacOsSystemAudioSource.Message : "System audio capture is not supported on this platform.";
        return null;
    }
}
