using System.Diagnostics;
using System.Text.RegularExpressions;

/// <summary>Detects a default output that is a headset or headphones, where a test signal would go straight into someone's ears.</summary>
internal static partial class OutputGuard
{
    // Windows PKEY_AudioEndpoint_FormFactor: 1 speakers, 2 line level, 3 headphones, 5 headset, 7 hands-free/handset.
    private static readonly int[] HeadFormFactors = [3, 5, 6, 7];

    public static bool LooksLikeHeadphones(string? name, int? windowsFormFactor, string? pulseFormFactor)
    {
        if (windowsFormFactor is { } f && HeadFormFactors.Contains(f))
        {
            return true;
        }

        if (pulseFormFactor is "headphone" or "headset" or "handsfree" or "hands-free" or "hifi" or "portable")
        {
            return true;
        }

        return name is not null && HeadNames().IsMatch(name);
    }

    /// <summary>Returns a description of the default output when it looks like headphones, otherwise null.</summary>
    public static string? DetectHeadphones()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
                using var device = enumerator.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia);
                int? form = null;
                var key = new NAudio.CoreAudioApi.PropertyKey(new Guid("1da5d803-d492-4edd-8c23-e0c0ffee7f0e"), 0);
                if (device.Properties.Contains(key) && device.Properties[key].Value is uint u)
                {
                    form = (int)u;
                }

                return LooksLikeHeadphones(device.FriendlyName, form, null) ? $"{device.FriendlyName} (form factor {form?.ToString() ?? "unknown"})" : null;
            }

            if (OperatingSystem.IsLinux())
            {
                var name = Run("pactl", "get-default-sink")?.Trim();
                var info = Run("pactl", "list sinks") ?? string.Empty;
                var block = info.Split("Sink #", StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(b => name is not null && b.Contains($"Name: {name}"));
                var form = block is null ? null : PulseForm().Match(block) is { Success: true } m ? m.Groups[1].Value : null;
                var desc = block is null ? null : PulseDescription().Match(block) is { Success: true } d ? d.Groups[1].Value : null;
                return LooksLikeHeadphones($"{desc} {name}", null, form) ? $"{desc ?? name} (form factor {form ?? "unknown"})" : null;
            }
        }
        catch (Exception)
        {
            // Could not tell. The level is low regardless, so carry on.
        }

        return null;
    }

    private static string? Run(string file, string arguments)
    {
        using var p = Process.Start(new ProcessStartInfo(file, arguments) { RedirectStandardOutput = true, UseShellExecute = false, Environment = { ["LC_ALL"] = "C" } });
        var text = p?.StandardOutput.ReadToEnd();
        p?.WaitForExit();
        return text;
    }

    [GeneratedRegex(@"headphone|headset|arctis|airpod|earbud|earphone|\bbuds\b|hands-?free|bluetooth|\bbt\b|wh-1000|quietcomfort|hyperx cloud|corsair void", RegexOptions.IgnoreCase)]
    private static partial Regex HeadNames();

    [GeneratedRegex(@"device\.form_factor = ""([^""]+)""")]
    private static partial Regex PulseForm();

    [GeneratedRegex(@"Description: (.+)")]
    private static partial Regex PulseDescription();
}
