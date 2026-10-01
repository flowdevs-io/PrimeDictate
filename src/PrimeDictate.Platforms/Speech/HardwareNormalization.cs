using PrimeDictate.Core.Dictation;
using PrimeDictate.Platforms.Speech.Qualcomm;

namespace PrimeDictate.Platforms.Speech;

/// <summary>
/// Makes saved settings fit the machine they are opened on, as the WPF app's <c>NormalizeSettingsForCurrentMachine</c> did at startup: a
/// <c>settings.json</c> carried over from a Snapdragon laptop must not leave an x64 PC pointing at a Qualcomm model, and a GPU or NPU
/// choice the hardware cannot honour moves to the best hardware configuration that is installed, else back to the CPU. In memory only:
/// nothing is written until the user saves Settings, and the WPF file is never touched.
/// </summary>
public static class HardwareNormalization
{
    /// <summary>The compute choices that make sense for a backend and model here. The CPU is always one (the WPF <c>GetComputeChoices</c>).</summary>
    public static IReadOnlyList<LegacyComputeInterface> ComputeChoices(LegacyBackend backend, string? selectedModelId, MachineSupport support)
    {
        var choices = new List<LegacyComputeInterface> { LegacyComputeInterface.Cpu };
        if (backend == LegacyBackend.WhisperNet)
        {
            if (support.WhisperNetGpu)
            {
                choices.Add(LegacyComputeInterface.Gpu);
            }

            if (CanUseWhisperNetNpu(selectedModelId, support))
            {
                choices.Add(LegacyComputeInterface.Npu);
            }
        }
        else if (backend == LegacyBackend.QualcommQnn && support.QualcommQnn)
        {
            choices.Add(LegacyComputeInterface.Npu);
        }

        return choices;
    }

    public static bool IsBackendSupported(LegacyBackend backend, MachineSupport support) =>
        backend != LegacyBackend.QualcommQnn || support.QualcommQnn;

    /// <summary>GPU first, else NPU, else CPU (the WPF <c>GetBestComputeInterface</c>).</summary>
    public static LegacyComputeInterface BestCompute(LegacyBackend backend, string? selectedModelId, MachineSupport support)
    {
        var choices = ComputeChoices(backend, selectedModelId, support);
        return choices.Contains(LegacyComputeInterface.Gpu) ? LegacyComputeInterface.Gpu
            : choices.Contains(LegacyComputeInterface.Npu) ? LegacyComputeInterface.Npu
            : LegacyComputeInterface.Cpu;
    }

    /// <summary>The first installed model that can use the machine's accelerator: Whisper.net on the GPU, then on the NPU (OpenVINO files present), then the Qualcomm package on the NPU.</summary>
    public static bool TryFindBestInstalledHardwareConfiguration(MachineSupport support, string modelsRoot, out (LegacyBackend Backend, LegacyComputeInterface Compute, string ModelId) selection)
    {
        if (support.WhisperNetGpu)
        {
            foreach (var option in SpeechModelCatalog.Options.Where(o => o.Backend == LegacyBackend.WhisperNet))
            {
                if (SpeechModelLocator.IsValid(option, SpeechModelLocator.InstallPath(modelsRoot, option)))
                {
                    selection = (LegacyBackend.WhisperNet, LegacyComputeInterface.Gpu, option.Id);
                    return true;
                }
            }
        }

        if (support.WhisperNetOpenVino)
        {
            foreach (var option in SpeechModelCatalog.Options.Where(o => o.Backend == LegacyBackend.WhisperNet))
            {
                var path = SpeechModelLocator.InstallPath(modelsRoot, option);
                if (SpeechModelLocator.IsValid(option, path) && SpeechModelLocator.WhisperNetOpenVinoEncoder(path) is not null)
                {
                    selection = (LegacyBackend.WhisperNet, LegacyComputeInterface.Npu, option.Id);
                    return true;
                }
            }
        }

        if (support.QualcommQnn)
        {
            foreach (var option in QualcommAihubWhisperCatalog.Options)
            {
                if (QualcommAihubWhisperCatalog.TryResolveInstalledPath(modelsRoot, option, out _))
                {
                    selection = (LegacyBackend.QualcommQnn, LegacyComputeInterface.Npu, option.Id);
                    return true;
                }
            }
        }

        selection = default;
        return false;
    }

    /// <summary>
    /// Adjusts <paramref name="settings"/> to <paramref name="support"/>. Returns a sentence for the user when something changed, else null.
    /// The compute interface is only judged while the new app's own Whisper.net device choice is unset (an explicit choice here wins).
    /// </summary>
    public static string? Normalize(DictationSettings settings, MachineSupport support, string modelsRoot)
    {
        var before = Describe(settings);
        var requested = settings.TranscriptionComputeInterface;
        var judgeCompute = settings.WhisperNetDevice is null && requested is not null;

        if (!IsBackendSupported(settings.TranscriptionBackend, support))
        {
            if (judgeCompute && requested != LegacyComputeInterface.Cpu && TryFindBestInstalledHardwareConfiguration(support, modelsRoot, out var found))
            {
                Apply(settings, found.Backend, found.Compute, found.ModelId);
                return Changed(before, settings);
            }

            if (QualcommAihubWhisperCatalog.TryGetById(settings.SelectedModelId, out _))
            {
                // The package only runs on the NPU; back to Whisper on the CPU with no model chosen (the model list will offer what is installed).
                settings.TranscriptionBackend = LegacyBackend.Whisper;
                settings.SelectedModelId = null;
            }
            else
            {
                // A Moonshine model selected under the Qualcomm backend keeps its id and runs on the CPU.
                settings.TranscriptionBackend = LegacyBackend.Moonshine;
            }

            if (settings.TranscriptionComputeInterface is not null)
            {
                settings.TranscriptionComputeInterface = LegacyComputeInterface.Cpu;
            }

            return Changed(before, settings);
        }

        if (!judgeCompute || ComputeChoices(settings.TranscriptionBackend, settings.SelectedModelId, support).Contains(requested!.Value))
        {
            return null;
        }

        if (requested != LegacyComputeInterface.Cpu && TryFindBestInstalledHardwareConfiguration(support, modelsRoot, out var best))
        {
            Apply(settings, best.Backend, best.Compute, best.ModelId);
        }
        else
        {
            settings.TranscriptionComputeInterface = BestCompute(settings.TranscriptionBackend, settings.SelectedModelId, support);
        }

        return Changed(before, settings);
    }

    private static bool CanUseWhisperNetNpu(string? selectedModelId, MachineSupport support)
    {
        if (!support.WhisperNetOpenVino)
        {
            return false;
        }

        var selected = SpeechModelLocator.FindWhisperNet(selectedModelId);
        return selected is not null
            ? selected.SupportsOpenVinoBundle
            : SpeechModelCatalog.Options.Any(o => o.Backend == LegacyBackend.WhisperNet && o.Recommended && o.SupportsOpenVinoBundle);
    }

    private static void Apply(DictationSettings settings, LegacyBackend backend, LegacyComputeInterface compute, string modelId)
    {
        settings.TranscriptionBackend = backend;
        settings.TranscriptionComputeInterface = compute;
        settings.SelectedModelId = modelId;
    }

    private static string Describe(DictationSettings settings) =>
        $"{settings.TranscriptionBackend}/{settings.SelectedModelId ?? "-"}/{settings.TranscriptionComputeInterface?.ToString() ?? "-"}";

    private static string? Changed(string before, DictationSettings settings) =>
        Describe(settings) == before
            ? null
            : $"The saved speech model setup ({before.Replace('/', ' ')}) cannot run on this PC, so {settings.TranscriptionBackend} ({settings.TranscriptionComputeInterface?.ToString() ?? "default device"}) is used instead.";
}
