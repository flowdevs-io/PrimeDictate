using System.Runtime.Versioning;
using SharpHook;
using SharpHook.Data;

namespace PrimeDictate.Platforms.Input;

/// <summary>
/// The WPF app's cue when dictation starts or stops: if the user turned on Windows "Show location of pointer when I
/// press the CTRL key" (Mouse Sonar), tap Ctrl so the pointer pulses. Does nothing when that setting is off.
/// </summary>
public static class WindowsMousePointerIndicator
{
    private const uint SpiGetMouseSonar = 0x101C;
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(150);
    private static readonly EventSimulator Simulator = new();

    /// <summary>Pulses shortly after the call, off the caller's thread. Never throws.</summary>
    public static void PulseSoon(Action<string>? log = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(Delay).ConfigureAwait(false);
            try
            {
                PulseIfMouseSonarEnabled();
            }
            catch (Exception ex)
            {
                log?.Invoke($"Windows Mouse Sonar cue unavailable: {ex.Message}");
            }
        });
    }

    public static void PulseIfMouseSonarEnabled()
    {
        if (!OperatingSystem.IsWindows() || !IsMouseSonarEnabled())
        {
            return;
        }

        _ = Simulator.SimulateKeyStroke([KeyCode.VcLeftControl]);
    }

    [SupportedOSPlatform("windows")]
    private static bool IsMouseSonarEnabled()
    {
        var enabled = false;
        return Win32.SystemParametersInfo(SpiGetMouseSonar, 0, ref enabled, 0) && enabled;
    }
}
