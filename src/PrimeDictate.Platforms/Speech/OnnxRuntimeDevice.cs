using System.ComponentModel;
using System.Runtime.InteropServices;

namespace PrimeDictate.Platforms.Speech;

/// <summary>Where the sherpa-onnx models (Whisper, Parakeet, Moonshine) run.</summary>
public enum OnnxDevicePreference
{
    Auto,
    Cpu,
    Cuda
}

/// <summary>
/// Chooses CPU or CUDA for the ONNX speech models, once per process. ONNX Runtime is one native library per process, so the
/// GPU build has to be loaded before the first model is created; a change of preference therefore applies at the next start.
/// CUDA needs a "GPU runtime pack" (a folder with the GPU build of onnxruntime.dll and its CUDA provider) and the CUDA and
/// cuDNN DLLs it depends on. Every requirement is checked here so a missing piece produces a plain note and CPU, never a
/// native failure inside a recognizer.
/// </summary>
public static class OnnxRuntimeDevice
{
    public const string EnvironmentVariable = "PRIMEDICTATE_ONNX_DEVICE";
    public const string PackEnvironmentVariable = "PRIMEDICTATE_ORT_GPU_DIR";

    /// <summary>ONNX Runtime 1.27 and later ship the CUDA 13 GPU build; 1.24 and earlier are CUDA 12. The sherpa-onnx package bundles 1.24.4 and works with a newer drop-in.</summary>
    public const string RecommendedRuntimeVersion = "1.30.0";

    /// <summary>DLLs the CUDA 13 build of ONNX Runtime loads. cuDNN 9 is not part of the CUDA Toolkit.</summary>
    public static readonly IReadOnlyList<string> RequiredRuntimeLibraries =
        ["cudart64_13.dll", "cublas64_13.dll", "cublasLt64_13.dll", "cufft64_12.dll", "cudnn64_9.dll"];

    public static readonly IReadOnlyList<string> RequiredPackFiles =
        ["onnxruntime.dll", "onnxruntime_providers_shared.dll", "onnxruntime_providers_cuda.dll"];

    private static readonly object Gate = new();
    private static bool configured;

    /// <summary>The sherpa-onnx provider string the models must use: <c>cpu</c> or <c>cuda</c>.</summary>
    public static string Provider { get; private set; } = "cpu";

    public static bool IsCuda => Provider == "cuda";

    /// <summary>One sentence for the user saying what is running and, when CUDA was wanted but is not used, why.</summary>
    public static string Summary { get; private set; } = "ONNX speech models run on the CPU.";

    /// <summary>True when the summary is a fallback the user should see (CUDA wanted, CPU used).</summary>
    public static bool FellBack { get; private set; }

    public static string DefaultPackDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PrimeDictate", "gpu", "onnxruntime-cuda13");

    public static OnnxDevicePreference ParsePreference(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "cpu" => OnnxDevicePreference.Cpu,
        "cuda" or "gpu" => OnnxDevicePreference.Cuda,
        _ => OnnxDevicePreference.Auto
    };

    /// <summary>The environment variable wins over the saved setting, as for Nemotron.</summary>
    public static OnnxDevicePreference Effective(string? savedSetting) =>
        ParsePreference(Environment.GetEnvironmentVariable(EnvironmentVariable) ?? savedSetting);

    /// <summary>Call once at startup before any sherpa-onnx model is created. Later calls do nothing.</summary>
    public static void Configure(OnnxDevicePreference preference, Action<string>? notice = null)
    {
        lock (Gate)
        {
            if (configured)
            {
                return;
            }

            configured = true;
            var result = Resolve(preference, new WindowsProbe());
            Provider = result.Provider;
            Summary = result.Summary;
            FellBack = result.FellBack;
            if (result.FellBack || result.Provider == "cuda")
            {
                notice?.Invoke(result.Summary);
            }
        }
    }

    public sealed record Resolution(string Provider, string Summary, bool FellBack);

    /// <summary>What the machine offers. Abstracted so the decision can be tested without a GPU or Windows.</summary>
    public interface IProbe
    {
        bool IsWindows { get; }

        string? FindPackDirectory();

        /// <summary>Directories searched for the CUDA and cuDNN DLLs, most specific first.</summary>
        IEnumerable<string> RuntimeDirectories(string packDirectory);

        bool FileExists(string path);

        /// <summary>True when an onnxruntime.dll is already loaded in this process (a CPU one cannot be swapped out).</summary>
        bool OnnxRuntimeAlreadyLoaded { get; }

        /// <summary>Prepends the directories to the DLL search path and loads the GPU runtime. Returns null on success, else the reason.</summary>
        string? Load(string packDirectory, IReadOnlyList<string> runtimeDirectories);
    }

    public static Resolution Resolve(OnnxDevicePreference preference, IProbe probe)
    {
        if (preference == OnnxDevicePreference.Cpu)
        {
            return new Resolution("cpu", "ONNX speech models run on the CPU (chosen).", false);
        }

        var wanted = preference == OnnxDevicePreference.Cuda ? "CUDA was requested" : "CUDA is available on Auto";
        string? reason = null;
        var pack = probe.FindPackDirectory();
        if (!probe.IsWindows)
        {
            reason = "GPU acceleration for these models is only set up on Windows so far";
        }
        else if (pack is null)
        {
            reason = $"the GPU runtime pack is not installed (ONNX Runtime {RecommendedRuntimeVersion} GPU, CUDA 13, in {DefaultPackDirectory})";
        }
        else
        {
            var missingPack = RequiredPackFiles.Where(f => !probe.FileExists(Path.Combine(pack, f))).ToList();
            if (missingPack.Count > 0)
            {
                reason = $"the GPU runtime pack in {pack} is missing {string.Join(", ", missingPack)}";
            }
            else if (probe.OnnxRuntimeAlreadyLoaded)
            {
                reason = "the CPU ONNX Runtime was already loaded before CUDA could be prepared";
            }
            else
            {
                var dirs = probe.RuntimeDirectories(pack).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                // An explicit CUDA request skips the name check (the list could be stale for a newer ONNX Runtime); loading the CUDA provider still verifies its direct dependencies.
                var missingLibs = preference == OnnxDevicePreference.Cuda ? [] : RequiredRuntimeLibraries.Where(l => !dirs.Any(d => probe.FileExists(Path.Combine(d, l)))).ToList();
                if (missingLibs.Count > 0)
                {
                    reason = $"these CUDA libraries were not found: {string.Join(", ", missingLibs)}" +
                        (missingLibs.Contains("cudnn64_9.dll") ? " (cuDNN 9 for CUDA 13 is a separate NVIDIA download)" : string.Empty);
                }
                else
                {
                    reason = probe.Load(pack, dirs);
                }
            }
        }

        if (reason is null)
        {
            return new Resolution("cuda", $"ONNX speech models run on CUDA (ONNX Runtime GPU from {pack}).", false);
        }

        // An explicit request that cannot be met is reported loudly; Auto falls back with the same note so the user still learns why.
        return new Resolution("cpu", $"{wanted}, but {reason}. Using the CPU.", true);
    }

    private sealed class WindowsProbe : IProbe
    {
        public bool IsWindows => OperatingSystem.IsWindows();

        public bool FileExists(string path) => File.Exists(path);

        public bool OnnxRuntimeAlreadyLoaded => OperatingSystem.IsWindows() && Native.GetModuleHandleW("onnxruntime.dll") != IntPtr.Zero;

        public string? FindPackDirectory()
        {
            var candidates = new List<string>();
            if (Environment.GetEnvironmentVariable(PackEnvironmentVariable) is { Length: > 0 } fromEnv)
            {
                candidates.Add(fromEnv);
            }

            candidates.Add(DefaultPackDirectory);
            candidates.Add(Path.Combine(AppContext.BaseDirectory, "gpu"));
            return candidates.FirstOrDefault(d => Directory.Exists(d) && File.Exists(Path.Combine(d, "onnxruntime.dll"))) ??
                candidates.FirstOrDefault(Directory.Exists);
        }

        public IEnumerable<string> RuntimeDirectories(string packDirectory)
        {
            yield return packDirectory;
            if (Environment.GetEnvironmentVariable("PRIMEDICTATE_CUDA_BIN") is { Length: > 0 } explicitBin)
            {
                yield return explicitBin;
            }

            foreach (var variable in Environment.GetEnvironmentVariables().Keys.Cast<string>()
                         .Where(k => k.StartsWith("CUDA_PATH_V13", StringComparison.OrdinalIgnoreCase) || k.Equals("CUDA_PATH", StringComparison.OrdinalIgnoreCase))
                         .OrderByDescending(k => k, StringComparer.OrdinalIgnoreCase))
            {
                var root = Environment.GetEnvironmentVariable(variable);
                if (!string.IsNullOrWhiteSpace(root))
                {
                    yield return Path.Combine(root, "bin", "x64");
                    yield return Path.Combine(root, "bin");
                }
            }

            foreach (var dir in CudnnDirectories())
            {
                yield return dir;
            }

            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                yield return dir.Trim();
            }
        }

        /// <summary>The cuDNN installer puts DLLs in <c>CUDNN\v9.x\bin\13.x</c>; CUDNN_PATH points at the version folder.</summary>
        private static IEnumerable<string> CudnnDirectories()
        {
            var roots = new List<string>();
            if (Environment.GetEnvironmentVariable("CUDNN_PATH") is { Length: > 0 } cudnn)
            {
                roots.Add(cudnn);
            }

            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var installRoot = Path.Combine(programFiles, "NVIDIA", "CUDNN");
            if (Directory.Exists(installRoot))
            {
                roots.AddRange(Directory.GetDirectories(installRoot).OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase));
            }

            foreach (var root in roots)
            {
                var bin = Path.Combine(root, "bin");
                if (!Directory.Exists(bin))
                {
                    continue;
                }

                foreach (var sub in Directory.GetDirectories(bin, "13*").OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
                {
                    yield return sub;
                }

                yield return bin;
            }
        }

        public string? Load(string packDirectory, IReadOnlyList<string> runtimeDirectories)
        {
            if (!OperatingSystem.IsWindows())
            {
                return "not Windows";
            }

            var extra = string.Join(Path.PathSeparator, runtimeDirectories.Where(Directory.Exists));
            Environment.SetEnvironmentVariable("PATH", extra + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"));
            foreach (var dll in RequiredPackFiles)
            {
                var handle = Native.LoadLibraryExW(Path.Combine(packDirectory, dll), IntPtr.Zero, Native.LoadWithAlteredSearchPath);
                if (handle == IntPtr.Zero)
                {
                    var error = Marshal.GetLastWin32Error();
                    return $"{dll} could not be loaded ({new Win32Exception(error).Message}; error {error})";
                }
            }

            return null;
        }
    }

    private static class Native
    {
        public const uint LoadWithAlteredSearchPath = 0x8;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr LoadLibraryExW(string fileName, IntPtr file, uint flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandleW(string moduleName);
    }
}
