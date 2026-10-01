using PrimeDictate.Core.Dictation;

namespace PrimeDictate.Platforms.Speech;

/// <summary>The Whisper.net device choices Settings offers on this machine. A choice the hardware cannot run is not listed (the WPF app's <c>GetComputeChoices</c>).</summary>
public static class WhisperNetDeviceChoices
{
    public sealed record Choice(WhisperNetDevicePreference Device, string Label);

    public static IReadOnlyList<Choice> For(MachineSupport support)
    {
        var choices = new List<Choice>
        {
            new(WhisperNetDevicePreference.Auto, WhisperNetRuntime.AutoLabel(support)),
            new(WhisperNetDevicePreference.Cpu, "CPU (most compatible)")
        };

        if (support.WhisperNetGpu)
        {
            choices.Add(new Choice(WhisperNetDevicePreference.Gpu, $"GPU ({support.WhisperNetGpuRuntimeLabel}, CPU if it fails)"));
        }

        if (support.WhisperNetOpenVino)
        {
            choices.Add(new Choice(WhisperNetDevicePreference.Npu, "NPU (OpenVINO; needs the model's OpenVINO files, else CPU)"));
        }

        return choices;
    }

    /// <summary>The list position to show for a saved choice. A choice this machine cannot run shows as Auto (position 0).</summary>
    public static int IndexOf(IReadOnlyList<Choice> choices, WhisperNetDevicePreference saved)
    {
        for (var i = 0; i < choices.Count; i++)
        {
            if (choices[i].Device == saved)
            {
                return i;
            }
        }

        return 0;
    }

    /// <summary>
    /// What to write when Settings is saved. Null leaves the saved value alone: when the saved choice is one this machine does not
    /// offer (an NPU or GPU choice copied from another PC) and the user left the list on Auto, it is not silently replaced.
    /// </summary>
    public static string? ValueToSave(IReadOnlyList<Choice> choices, int selectedIndex, DictationSettings settings)
    {
        var shown = selectedIndex >= 0 && selectedIndex < choices.Count ? choices[selectedIndex].Device : WhisperNetDevicePreference.Auto;
        var saved = settings.ResolveWhisperNetDevice();
        var offered = choices.Any(c => c.Device == saved);
        if (settings.WhisperNetDevice is null && !offered && shown == WhisperNetDevicePreference.Auto)
        {
            return null;
        }

        return shown.ToString().ToLowerInvariant();
    }
}
