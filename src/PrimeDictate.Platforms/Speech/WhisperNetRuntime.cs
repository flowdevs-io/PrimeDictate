using PrimeDictate.Core.Dictation;
using Whisper.net.LibraryLoader;

namespace PrimeDictate.Platforms.Speech;

/// <summary>
/// Chooses the native whisper.cpp build Whisper.net loads. <see cref="RuntimeOptions"/> is process-global and the native library cannot be
/// swapped once loaded, so the order is applied once, just before the first model is created; a changed setting applies at the next start.
/// Same orders as the WPF app: CPU -> Cpu, CpuNoAvx; GPU -> Cuda, Vulkan, Cpu, CpuNoAvx; NPU -> OpenVINO first (on the NPU) only when the
/// model has its OpenVINO encoder files, else the CPU. Auto picks among those by what the machine supports (see <see cref="Resolve"/>).
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

    /// <summary>
    /// The library order for an <em>effective</em> choice (see <see cref="Resolve"/>), exactly as the WPF app set it: CPU, GPU, or the NPU
    /// (OpenVINO first only when the model has its encoder files; the CPU after it either way).
    /// </summary>
    public static IReadOnlyList<RuntimeLibrary> LibraryOrder(WhisperNetDevicePreference preference, bool hasOpenVinoEncoder) => preference switch
    {
        WhisperNetDevicePreference.Cpu => [RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx],
        WhisperNetDevicePreference.Gpu => [RuntimeLibrary.Cuda, RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx],
        WhisperNetDevicePreference.Npu when hasOpenVinoEncoder => [RuntimeLibrary.OpenVino, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx],
        _ => [RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx]
    };

    /// <summary>The OpenVINO device the encoder runs on. The WPF app used OpenVINO only for its NPU choice, so it is always the NPU.</summary>
    public const string OpenVinoDevice = "NPU";

    /// <summary>
    /// Turns the saved choice into the one that runs on this machine. Explicit choices are kept (CPU, GPU, NPU). Auto is the WPF app's "best
    /// available": the GPU when CUDA or Vulkan can run, else the NPU when OpenVINO can run and this model has its OpenVINO files, else the CPU.
    /// </summary>
    public static WhisperNetDevicePreference Resolve(WhisperNetDevicePreference preference, MachineSupport support, bool modelHasOpenVinoEncoder)
    {
        if (preference != WhisperNetDevicePreference.Auto)
        {
            return preference;
        }

        if (support.WhisperNetGpu)
        {
            return WhisperNetDevicePreference.Gpu;
        }

        return support.WhisperNetOpenVino && modelHasOpenVinoEncoder ? WhisperNetDevicePreference.Npu : WhisperNetDevicePreference.Cpu;
    }

    /// <summary>One sentence for Settings on what "Auto" does on this machine.</summary>
    public static string AutoLabel(MachineSupport support) =>
        support.WhisperNetGpu
            ? $"Auto ({support.WhisperNetGpuLabel}, CPU if it fails)"
            : support.WhisperNetOpenVino
                ? "Auto (the NPU when the model has its OpenVINO files, else the CPU)"
                : "Auto (the CPU; no GPU or NPU runtime was found)";

    /// <summary>The library Whisper.net actually loaded, or null before the first model.</summary>
    public static string? LoadedLibraryName => RuntimeOptions.LoadedLibrary?.ToString();

    /// <summary>True when the GPU was asked for and a GPU build is not what loaded.</summary>
    public static bool GpuWantedButNotLoaded(WhisperNetDevicePreference preference, RuntimeLibrary? loaded) =>
        preference == WhisperNetDevicePreference.Gpu && loaded is not (RuntimeLibrary.Cuda or RuntimeLibrary.Vulkan);

    /// <summary>Sets the library order the first time a model is created. Later calls keep the first order (the native library is already loaded).</summary>
    internal static void ApplyOnce(WhisperNetDevicePreference effective, bool hasOpenVinoEncoder)
    {
        lock (Gate)
        {
            if (applied)
            {
                return;
            }

            applied = true;
            RuntimeOptions.LoadedLibrary = null;
            RuntimeOptions.RuntimeLibraryOrder = [.. LibraryOrder(effective, hasOpenVinoEncoder)];
        }
    }
}
