using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;

namespace PrimeDictate.Platforms.Speech.Qualcomm;

/// <summary>Which provider a QNN-capable model runs on. <see cref="Cpu"/> is the non-strict fallback of the Moonshine path.</summary>
public enum QnnActiveRuntime
{
    Cpu = 0,
    QnnHtp = 1
}

public sealed record QnnRuntimeOptions(
    bool StrictValidation,
    bool EnableContextCache,
    bool EmbedContextCache,
    string? ProfilingLevel,
    string? ProfilingOutputDirectory,
    string BackendLibraryPath,
    string ProvidersLibraryPath,
    string SystemLibraryPath)
{
    public bool EnableProfiling => !string.IsNullOrWhiteSpace(this.ProfilingLevel) &&
        !string.Equals(this.ProfilingLevel, "off", StringComparison.OrdinalIgnoreCase);
}

/// <summary>What this process can do with the Qualcomm NPU. Pure data, so the gating can be tested without Snapdragon hardware.</summary>
public sealed record QnnAvailability(
    bool IsArm64Process,
    string Summary,
    string? BackendLibraryPath,
    string? ProvidersLibraryPath,
    string? SystemLibraryPath)
{
    /// <summary>A native Windows ARM64 process with every QNN native present. This is the one gate for offering any Qualcomm option.</summary>
    public bool SupportsQnnHtp =>
        this.IsArm64Process &&
        !string.IsNullOrWhiteSpace(this.BackendLibraryPath) &&
        !string.IsNullOrWhiteSpace(this.ProvidersLibraryPath) &&
        !string.IsNullOrWhiteSpace(this.SystemLibraryPath);
}

/// <summary>
/// Qualcomm QNN HTP (NPU) support through ONNX Runtime, ported from the WPF app. It runs only in a native Windows ARM64 process that has
/// the ONNX Runtime QNN natives next to it; the app project copies them over sherpa-onnx's onnxruntime.dll at build and publish, because
/// one process can load only one onnxruntime.dll. Every decision here is checked before ONNX Runtime is touched, so a machine without the
/// NPU never reaches native code.
/// </summary>
public static class QnnRuntimeSupport
{
    public const string BackendLibrary = "QnnHtp.dll";
    public const string ProvidersLibrary = "onnxruntime_providers_qnn.dll";
    public const string SystemLibrary = "QnnSystem.dll";

    /// <summary>The ONNX Runtime managed API version in use (the QNN natives are the same version).</summary>
    public static string OnnxRuntimeVersion { get; } = typeof(SessionOptions).Assembly.GetName().Version?.ToString(3) ?? "unknown";

    private const string QnnProviderName = "QNN";
    private const string DisableCpuFallbackKey = "session.disable_cpu_ep_fallback";
    private const string ContextEnableKey = "ep.context_enable";
    private const string ContextFilePathKey = "ep.context_file_path";
    private const string ContextEmbedModeKey = "ep.context_embed_mode";
    private const string StrictValidationEnvVar = "PRIMEDICTATE_QNN_STRICT";
    private const string ContextCacheEnvVar = "PRIMEDICTATE_QNN_CONTEXT_CACHE";
    private const string ContextEmbedEnvVar = "PRIMEDICTATE_QNN_CONTEXT_EMBED";
    private const string ProfilingLevelEnvVar = "PRIMEDICTATE_QNN_PROFILE";
    private const string ProfilingDirectoryEnvVar = "PRIMEDICTATE_QNN_PROFILE_DIR";

    private static readonly Lazy<QnnAvailability> Current = new(() =>
        Evaluate(OperatingSystem.IsWindows(), RuntimeInformation.ProcessArchitecture, TryResolveRuntimeAsset));

    /// <summary>Availability for this process, worked out once (the natives do not appear while the app runs).</summary>
    public static QnnAvailability GetAvailability() => Current.Value;

    /// <summary>The decision itself, with the machine abstracted away so tests can cover Windows x64, ARM64 with and without natives, and other systems.</summary>
    public static QnnAvailability Evaluate(bool isWindows, Architecture processArchitecture, Func<string, string?> resolveAsset)
    {
        var isArm64Process = isWindows && processArchitecture == Architecture.Arm64;
        var backend = resolveAsset(BackendLibrary);
        var providers = resolveAsset(ProvidersLibrary);
        var system = resolveAsset(SystemLibrary);

        string summary;
        if (!isWindows)
        {
            summary = "Qualcomm QNN HTP requires Windows.";
        }
        else if (!isArm64Process)
        {
            summary = $"Qualcomm QNN HTP requires a native Windows ARM64 process. Current process architecture: {processArchitecture}.";
        }
        else if (backend is null || providers is null || system is null)
        {
            summary = "ONNX Runtime QNN native assets are not present in this build. Publish or run the native win-arm64 build to enable Qualcomm QNN HTP.";
        }
        else
        {
            summary = "Qualcomm QNN HTP runtime assets are present. PrimeDictate can attempt a strict NPU path with CPU fallback disabled, and will fall back to CPU only when strict validation is off.";
        }

        return new QnnAvailability(isArm64Process, summary, backend, providers, system);
    }

    /// <summary>The text shown next to Qualcomm models in Settings.</summary>
    public static string RuntimeSummary(QnnAvailability availability) =>
        $"{availability.Summary} Qualcomm AI Hub Whisper packages use precompiled ONNX Runtime EPContext wrappers around QNN context binaries.";

    public static QnnRuntimeOptions GetRuntimeOptions(string modelDirectory, bool? strictValidationOverride = null)
    {
        var availability = GetAvailability();
        return new QnnRuntimeOptions(
            StrictValidation: strictValidationOverride ?? GetBooleanEnvironmentVariable(StrictValidationEnvVar, defaultValue: false),
            EnableContextCache: GetBooleanEnvironmentVariable(ContextCacheEnvVar, defaultValue: true),
            EmbedContextCache: GetBooleanEnvironmentVariable(ContextEmbedEnvVar, defaultValue: true),
            ProfilingLevel: GetProfilingLevel(),
            ProfilingOutputDirectory: GetProfilingOutputDirectory(Path.Combine(modelDirectory, ".qnn-diagnostics")),
            BackendLibraryPath: availability.BackendLibraryPath ?? BackendLibrary,
            ProvidersLibraryPath: availability.ProvidersLibraryPath ?? ProvidersLibrary,
            SystemLibraryPath: availability.SystemLibraryPath ?? SystemLibrary);
    }

    public static SessionOptions CreateSessionOptions(
        QnnActiveRuntime runtime,
        QnnRuntimeOptions runtimeOptions,
        string sessionTag,
        string? contextFilePath = null)
    {
        var sessionOptions = new SessionOptions
        {
            LogId = sessionTag,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            InterOpNumThreads = 1,
            IntraOpNumThreads = 1,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL
        };

        if (runtime == QnnActiveRuntime.QnnHtp)
        {
            sessionOptions.AddSessionConfigEntry(DisableCpuFallbackKey, "1");

            if (runtimeOptions.EnableContextCache && !string.IsNullOrWhiteSpace(contextFilePath))
            {
                sessionOptions.AddSessionConfigEntry(ContextEnableKey, "1");
                sessionOptions.AddSessionConfigEntry(ContextFilePathKey, contextFilePath);
                sessionOptions.AddSessionConfigEntry(ContextEmbedModeKey, runtimeOptions.EmbedContextCache ? "1" : "0");
            }

            sessionOptions.AppendExecutionProvider(QnnProviderName, BuildProviderOptions(runtimeOptions, contextFilePath));
        }
        else
        {
            sessionOptions.AppendExecutionProvider_CPU();
        }

        return sessionOptions;
    }

    public static string DescribeRuntimePlan(QnnActiveRuntime runtime, QnnRuntimeOptions runtimeOptions, string? contextFilePath)
    {
        var provider = runtime == QnnActiveRuntime.QnnHtp ? "QNN HTP" : "CPU";
        var strictValidation = runtime == QnnActiveRuntime.QnnHtp && runtimeOptions.StrictValidation;
        var profiling = runtimeOptions.EnableProfiling ? runtimeOptions.ProfilingLevel : "off";
        return $"provider={provider}, backendPath={runtimeOptions.BackendLibraryPath}, cpuFallbackDisabled={runtime == QnnActiveRuntime.QnnHtp}, strictValidation={strictValidation}, contextCache={runtimeOptions.EnableContextCache}, contextFilePath={contextFilePath ?? "<none>"}, contextEmbed={runtimeOptions.EmbedContextCache}, profiling={profiling}";
    }

    private static Dictionary<string, string> BuildProviderOptions(QnnRuntimeOptions runtimeOptions, string? contextFilePath)
    {
        var providerOptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["backend_path"] = runtimeOptions.BackendLibraryPath,
            ["htp_performance_mode"] = "default"
        };

        if (runtimeOptions.EnableProfiling)
        {
            providerOptions["profiling_level"] = runtimeOptions.ProfilingLevel!;
            if (!string.IsNullOrWhiteSpace(runtimeOptions.ProfilingOutputDirectory))
            {
                Directory.CreateDirectory(runtimeOptions.ProfilingOutputDirectory);
                var fileName = $"{Path.GetFileNameWithoutExtension(contextFilePath ?? "session")}.csv";
                providerOptions["profiling_file_path"] = Path.Combine(runtimeOptions.ProfilingOutputDirectory, fileName);
            }
        }

        return providerOptions;
    }

    private static string? TryResolveRuntimeAsset(string fileName)
    {
        foreach (var candidateDirectory in EnumerateRuntimeAssetDirectories())
        {
            var candidate = Path.Combine(candidateDirectory, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateRuntimeAssetDirectories()
    {
        yield return AppContext.BaseDirectory;
        yield return Path.Combine(AppContext.BaseDirectory, "runtimes", "win-arm64", "native");
        yield return Path.Combine(AppContext.BaseDirectory, "win-arm64");
        yield return Path.Combine(Directory.GetCurrentDirectory(), "runtimes", "win-arm64", "native");
    }

    private static bool GetBooleanEnvironmentVariable(string name, bool defaultValue)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        return raw.Trim() switch
        {
            "1" => true,
            "0" => false,
            _ => bool.TryParse(raw, out var parsed) ? parsed : defaultValue
        };
    }

    private static string? GetProfilingLevel()
    {
        var raw = Environment.GetEnvironmentVariable(ProfilingLevelEnvVar)?.Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return raw.ToLowerInvariant() switch
        {
            "basic" => "basic",
            "detailed" => "detailed",
            "optrace" => "optrace",
            _ => null
        };
    }

    private static string GetProfilingOutputDirectory(string defaultDirectory)
    {
        var raw = Environment.GetEnvironmentVariable(ProfilingDirectoryEnvVar);
        return string.IsNullOrWhiteSpace(raw) ? defaultDirectory : Path.GetFullPath(raw);
    }
}
