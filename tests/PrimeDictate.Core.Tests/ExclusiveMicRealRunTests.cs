using PrimeDictate.Core.Providers;
using PrimeDictate.Platforms.Audio;

namespace PrimeDictate.Core.Tests;

/// <summary>
/// Real run on Windows: opens the default microphone asking for exclusive access, reads about a second of frames and
/// reports the granted mode (Exclusive, or Shared if the device refused). Skipped unless PRIMEDICTATE_REAL_MIC=1.
/// </summary>
public sealed class ExclusiveMicRealRunTests
{
    [Fact]
    public async Task Default_microphone_opens_with_an_exclusive_request_and_delivers_frames()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("PRIMEDICTATE_REAL_MIC") != "1")
        {
            return;
        }

        using var source = new MiniAudioCaptureSource();
        await using var lease = await source.OpenAsync(null, MicAccessMode.Exclusive, CancellationToken.None);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var frames = 0;
        try
        {
            await foreach (var _ in lease.ReadFramesAsync(cts.Token))
            {
                if (++frames >= 20)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }

        var devices = await source.ListDevicesAsync(CancellationToken.None);
        File.WriteAllText(
            Path.Combine(Path.GetTempPath(), "pd-exclusive-mic-result.txt"),
            $"granted={lease.AccessMode} frames={frames} backend={source.ActiveBackend} devices={string.Join(" | ", devices.Select(d => (d.IsDefault ? "*" : string.Empty) + d.Name))}");
        Assert.True(frames >= 20, "no audio frames arrived");
    }
}
