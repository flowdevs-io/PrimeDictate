using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using NAudio.CoreAudioApi;

namespace PrimeDictate.Platforms.Audio;

/// <summary>
/// Windows endpoint ids ("{0.0.1.00000000}.{guid}") are what the WPF app saved as the chosen microphone; miniaudio lists
/// devices by name. This finds the name Windows gives such an id, so an imported choice still selects its device.
/// </summary>
internal static partial class WindowsAudioEndpoints
{
    public static bool IsEndpointId(string id) => EndpointId().IsMatch(id);

    /// <summary>The device's friendly name (the name miniaudio lists), or null when Windows no longer knows the id.</summary>
    [SupportedOSPlatform("windows")]
    public static string? FriendlyName(string id)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDevice(id);
            return device.FriendlyName;
        }
        catch (COMException)
        {
            return null;
        }
    }

    /// <summary>The friendly name of the Windows default recording device, or null when there is none.</summary>
    [SupportedOSPlatform("windows")]
    public static string? DefaultCaptureName()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
            return device.FriendlyName;
        }
        catch (COMException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"^\{0\.0\.[01]\.00000000\}\.\{[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}\}$")]
    private static partial Regex EndpointId();
}
