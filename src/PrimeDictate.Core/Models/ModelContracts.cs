using System.Runtime.InteropServices;
using PrimeDictate.Core.Providers;

namespace PrimeDictate.Core.Models;

public enum ModelKind
{
    /// <summary>Produces words. Listed under "Speech model".</summary>
    SpeechRecognition = 0,

    /// <summary>Finds speakers. Listed under "Speaker detection", never as a speech model.</summary>
    SpeakerDiarization = 1
}

public enum ModelAvailability
{
    Missing = 0,
    Downloading = 1,
    /// <summary>Required files are present; not yet proven to load.</summary>
    Installed = 2,
    /// <summary>A load probe succeeded on this machine with the reported effective backend.</summary>
    Ready = 3,
    /// <summary>Cannot run on this OS, architecture, or runtime.</summary>
    Unsupported = 4,
    FailedValidation = 5
}

public sealed record ModelFile(string RelativePath, long? SizeBytes, string? Sha256);

public sealed record PlatformRequirement(OSPlatform? OperatingSystem, Architecture? ProcessArchitecture, string? Note);

public sealed record ModelDescriptor(
    string Id,
    ModelKind Kind,
    string Family,
    string DisplayName,
    string ArtifactFormat,
    string? ArtifactRevision,
    IReadOnlyList<ModelFile> ExpectedFiles,
    long? DownloadSizeBytes,
    IReadOnlyList<string> Languages,
    bool SupportsFiles,
    LiveRecognitionMode LiveMode,
    TimingCapabilities Timing,
    bool SupportsDiarization,
    IReadOnlyList<PlatformRequirement> SupportedPlatforms,
    string? License);

public sealed record ModelStatus(
    ModelDescriptor Descriptor,
    ModelAvailability Availability,
    string? InstalledPath,
    string? Reason,
    EffectiveRuntime? ProbedRuntime);

public interface IModelRegistry
{
    ValueTask<IReadOnlyList<ModelStatus>> ListAsync(ModelKind kind, CancellationToken cancellationToken);

    ValueTask<ModelStatus> GetStatusAsync(string modelId, CancellationToken cancellationToken);

    /// <summary>
    /// Loads the model and runs a short inference to prove it works, reporting the backend that
    /// actually ran. A loadable library is not proof of accelerator use.
    /// </summary>
    ValueTask<ModelStatus> ProbeAsync(string modelId, string requestedBackend, CancellationToken cancellationToken);
}

public static class PlatformRequirements
{
    public static bool IsSatisfied(IReadOnlyList<PlatformRequirement> requirements, OSPlatform os, Architecture architecture) =>
        requirements.Count == 0 ||
        requirements.Any(r =>
            (r.OperatingSystem is null || r.OperatingSystem.Value == os) &&
            (r.ProcessArchitecture is null || r.ProcessArchitecture.Value == architecture));

    public static OSPlatform Current() =>
        OperatingSystem.IsWindows() ? OSPlatform.Windows
        : OperatingSystem.IsMacOS() ? OSPlatform.OSX
        : OperatingSystem.IsLinux() ? OSPlatform.Linux
        : OSPlatform.Create("UNKNOWN");
}
