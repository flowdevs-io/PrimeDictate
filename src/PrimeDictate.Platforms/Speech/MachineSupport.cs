using System.Runtime.InteropServices;
using PrimeDictate.Platforms.Speech.Qualcomm;

namespace PrimeDictate.Platforms.Speech;

/// <summary>
/// What this machine can run: Whisper.net on CUDA, Vulkan or the OpenVINO NPU, and the Qualcomm NPU. Ported from the WPF app's
/// <c>PlatformSupport</c>. Options that need hardware the machine lacks are not shown, so nobody picks something that cannot run.
/// Pure data (see <see cref="Evaluate"/>), so the rules are tested without the hardware.
/// </summary>
public sealed record MachineSupport(bool WhisperNetOpenVino, bool WhisperNetCuda, bool WhisperNetVulkan, QnnAvailability Qnn)
{
    private static readonly Lazy<MachineSupport> CurrentSupport = new(() => Evaluate(
        OperatingSystem.IsWindows(),
        RuntimeInformation.ProcessArchitecture,
        File.Exists,
        Environment.SystemDirectory,
        HasWhisperNetRuntimeAsset,
        QnnRuntimeSupport.GetAvailability()));

    public static MachineSupport Current => CurrentSupport.Value;

    public bool WhisperNetGpu => this.WhisperNetCuda || this.WhisperNetVulkan;

    public bool QualcommQnn => this.Qnn.SupportsQnnHtp;

    public string WhisperNetGpuRuntimeLabel => (this.WhisperNetCuda, this.WhisperNetVulkan) switch
    {
        (true, true) => "CUDA/Vulkan",
        (true, false) => "CUDA",
        (false, true) => "Vulkan",
        _ => "GPU"
    };

    public string WhisperNetGpuLabel => this.WhisperNetGpuRuntimeLabel == "GPU" ? "GPU" : $"{this.WhisperNetGpuRuntimeLabel} GPU";

    /// <summary>The WPF Settings sentence about what Whisper.net can use here.</summary>
    public string WhisperNetRuntimeSummary(Architecture architecture, bool isWindows) => this.WhisperNetOpenVino
        ? this.WhisperNetGpu
            ? $"Whisper.net GGML can use CPU, {this.WhisperNetGpuLabel}, and OpenVINO NPU on this machine."
            : "Whisper.net GGML can use CPU and OpenVINO NPU on this machine."
        : this.WhisperNetGpu
            ? $"Whisper.net GGML can use CPU and {this.WhisperNetGpuLabel} on this machine."
            : isWindows && architecture == Architecture.Arm64
                ? "Whisper.net GGML runs natively on ARM64 with CPU. GPU/NPU acceleration currently requires an x64 build."
                : $"Whisper.net GGML can use CPU, but no supported GPU/NPU runtime was detected for {architecture}.";

    /// <summary>Whisper.net runs CUDA, Vulkan and OpenVINO only in a Windows x64 process, and each needs its driver library and its runtime build next to the app.</summary>
    public static MachineSupport Evaluate(
        bool isWindows,
        Architecture architecture,
        Func<string, bool> fileExists,
        string systemDirectory,
        Func<string, bool> hasRuntimeAsset,
        QnnAvailability qnn)
    {
        var x64 = isWindows && architecture == Architecture.X64;
        return new MachineSupport(
            WhisperNetOpenVino: x64 && hasRuntimeAsset("openvino"),
            WhisperNetCuda: x64 && fileExists(Path.Combine(systemDirectory, "nvcuda.dll")) && hasRuntimeAsset("cuda"),
            WhisperNetVulkan: x64 && fileExists(Path.Combine(systemDirectory, "vulkan-1.dll")) && hasRuntimeAsset("vulkan"),
            qnn);
    }

    /// <summary>A machine with no accelerator at all (CPU only). The baseline for tests.</summary>
    public static MachineSupport None { get; } = new(false, false, false, QnnRuntimeSupport.Evaluate(false, Architecture.X64, _ => null));

    /// <summary>
    /// The runtime build's <c>whisper.dll</c> is next to the app, and for OpenVINO so is the OpenVINO runtime it imports. Files are looked for,
    /// never loaded: the WPF app loaded each variant and freed it again to prove it worked, but whisper.cpp cannot be unloaded and a different
    /// variant loaded afterwards in the same process aborts it (ggml asserts), which would have crashed the app when Whisper.net loads the
    /// variant it really wants. Whisper.net still falls through its library order if a variant turns out not to load.
    /// </summary>
    private static bool HasWhisperNetRuntimeAsset(string runtimeName)
    {
        var nativeFileName = OperatingSystem.IsWindows() ? "whisper.dll" : "libwhisper.so";
        var found = EnumerateWhisperNetRuntimeDirectories(runtimeName).Any(directory => File.Exists(Path.Combine(directory, nativeFileName)));
        return found && (runtimeName != "openvino" || FindOnSearchPath(OpenVinoRuntimeLibrary, OpenVinoSearchDirectories(), File.Exists) is not null);
    }

    /// <summary>The OpenVINO runtime library the OpenVINO build of whisper.dll imports. It comes from Intel's OpenVINO install, not from the Whisper.net package.</summary>
    public const string OpenVinoRuntimeLibrary = "openvino.dll";

    /// <summary>The first directory in <paramref name="directories"/> that holds <paramref name="fileName"/>, or null. Pure, so the search is tested without OpenVINO installed.</summary>
    public static string? FindOnSearchPath(string fileName, IEnumerable<string> directories, Func<string, bool> fileExists)
    {
        foreach (var directory in directories)
        {
            if (!string.IsNullOrWhiteSpace(directory) && fileExists(Path.Combine(directory.Trim(), fileName)))
            {
                return directory;
            }
        }

        return null;
    }

    /// <summary>Where Windows looks for a DLL that <c>whisper.dll</c> imports: the app folder, the system folder, then PATH.</summary>
    private static IEnumerable<string> OpenVinoSearchDirectories()
    {
        yield return AppContext.BaseDirectory;
        yield return Environment.SystemDirectory;
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            yield return directory;
        }
    }

    private static IEnumerable<string> EnumerateWhisperNetRuntimeDirectories(string runtimeName)
    {
        if (!OperatingSystem.IsWindows())
        {
            yield break;
        }

        yield return Path.Combine(AppContext.BaseDirectory, "runtimes", runtimeName, "win-x64");
        var assemblyDirectory = Path.GetDirectoryName(typeof(MachineSupport).Assembly.Location);
        if (!string.IsNullOrWhiteSpace(assemblyDirectory))
        {
            yield return Path.Combine(assemblyDirectory, "runtimes", runtimeName, "win-x64");
        }

        yield return Path.Combine(Directory.GetCurrentDirectory(), "runtimes", runtimeName, "win-x64");
    }
}
