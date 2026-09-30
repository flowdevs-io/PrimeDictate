using PrimeDictate.Core.Dictation;
using Whisper.net.LibraryLoader;

namespace PrimeDictate.Platforms.Speech;

/// <summary>
/// Chooses the native whisper.cpp build Whisper.net loads. <see cref="RuntimeOptions"/> is process-global and the native library cannot be
/// swapped once loaded, so the order is applied once, just before the first model is created; a changed setting applies at the next start.
/// Same orders as the WPF app: CPU -> Cpu, CpuNoAvx; GPU -> Cuda, Vulkan, Cpu, CpuNoAvx; Auto and NPU use OpenVINO first only when the
/// model has its OpenVINO encoder files, else the CPU.
/// </summary>
public static class WhisperNetRuntime
{
    public const string EnvironmentVariable = "PRIMEDICTATE_WHISPERNET_DEVICE";

    private static readonly object Gate = new();
    private static bool applied;

    public static WhisperNetDevicePreference Preference { get; private set; } = WhisperNetDevicePreference.Auto;

    /// <summary>Told about a GPU that was wanted but not loaded. Never carries recognized text.</summary>
    public static Action<string>? Notice { get; set; }

    /// <summary>The environment variable wins over the saved setting, as for the ONNX device.</summary>
    public static WhisperNetDevicePreference Effective(DictationSettings settings) =>
        Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } fromEnvironment
            ? WhisperNetDevicePreferences.Parse(fromEnvironment)
            : settings.ResolveWhisperNetDevice();

    /// <summary>Call once at startup before any Whisper.net model is created. Ignored once a model has been loaded.</summary>
    public static void Configure(WhisperNetDevicePreference preference, Action<string>? notice = null)
    {
        lock (Gate)
        {
            if (applied)
            {
                return;
            }

            Preference = preference;
            Notice = notice;
        }
    }

    public static IReadOnlyList<RuntimeLibrary> LibraryOrder(WhisperNetDevicePreference preference, bool hasOpenVinoEncoder) => preference switch
    {
        WhisperNetDevicePreference.Cpu => [RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx],
        WhisperNetDevicePreference.Gpu => [RuntimeLibrary.Cuda, RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx],
        _ when hasOpenVinoEncoder => [RuntimeLibrary.OpenVino, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx],
        _ => [RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx]
    };

    /// <summary>The OpenVINO device name for the encoder: the NPU when asked for, else the CPU.</summary>
    public static string OpenVinoDevice(WhisperNetDevicePreference preference) =>
        preference == WhisperNetDevicePreference.Npu ? "NPU" : "CPU";

    /// <summary>The library Whisper.net actually loaded, or null before the first model.</summary>
    public static string? LoadedLibraryName => RuntimeOptions.LoadedLibrary?.ToString();

    /// <summary>True when the GPU was asked for and a GPU build is not what loaded.</summary>
    public static bool GpuWantedButNotLoaded(WhisperNetDevicePreference preference, RuntimeLibrary? loaded) =>
        preference == WhisperNetDevicePreference.Gpu && loaded is not (RuntimeLibrary.Cuda or RuntimeLibrary.Vulkan);

    /// <summary>Sets the library order the first time a model is created. Later calls keep the first order (the native library is already loaded).</summary>
    internal static void ApplyOnce(bool hasOpenVinoEncoder)
    {
        lock (Gate)
        {
            if (applied)
            {
                return;
            }

            applied = true;
            RuntimeOptions.LoadedLibrary = null;
            RuntimeOptions.RuntimeLibraryOrder = [.. LibraryOrder(Preference, hasOpenVinoEncoder)];
        }
    }
}
