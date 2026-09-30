using System.Diagnostics;
using System.Runtime.InteropServices;
using PrimeDictate.Core.Dictation;

namespace PrimeDictate.Platforms.Input;

/// <summary>
/// Plays the cues with what the OS already has: <c>PlaySound</c> on Windows, <c>afplay</c> on macOS, <c>paplay</c> or
/// <c>aplay</c> on Linux. The commands and arguments are fixed and the WAV files are ours, so nothing user-supplied
/// reaches a process. If nothing can play, cues are silently skipped.
/// </summary>
public sealed class ProcessAudioCuePlayer : IAudioCuePlayer
{
    private const uint SndMemory = 0x0004;
    private const uint SndAsync = 0x0001;
    private const uint SndNoDefault = 0x0002;

    private readonly Dictionary<DictationAudioCue, string> files = [];
    private readonly Dictionary<DictationAudioCue, GCHandle> pinned = [];
    private readonly object sync = new();
    private string? linuxPlayer;
    private bool linuxProbed;

    public void Play(DictationAudioCue cue)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                _ = PlaySound(this.PinnedWave(cue), IntPtr.Zero, SndMemory | SndAsync | SndNoDefault);
                return;
            }

            var player = OperatingSystem.IsMacOS() ? "afplay" : this.FindLinuxPlayer();
            if (player is null)
            {
                return;
            }

            var start = new ProcessStartInfo(player, [this.FileFor(cue)]) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
            var process = Process.Start(start);
            if (process is not null)
            {
                _ = Task.Run(async () =>
                {
                    await process.WaitForExitAsync().ConfigureAwait(false);
                    process.Dispose();
                });
            }
        }
        catch (Exception)
        {
            // A missing sound device or player must never affect dictation.
        }
    }

    /// <summary>SND_ASYNC keeps reading the buffer after PlaySound returns, so the two cue arrays are pinned for the process lifetime.</summary>
    private IntPtr PinnedWave(DictationAudioCue cue)
    {
        lock (this.sync)
        {
            if (!this.pinned.TryGetValue(cue, out var handle))
            {
                handle = GCHandle.Alloc(AudioCues.Wave(cue), GCHandleType.Pinned);
                this.pinned[cue] = handle;
            }

            return handle.AddrOfPinnedObject();
        }
    }

    private string FileFor(DictationAudioCue cue)
    {
        lock (this.sync)
        {
            if (!this.files.TryGetValue(cue, out var path) || !File.Exists(path))
            {
                path = Path.Combine(Path.GetTempPath(), $"primedictate-cue-{cue.ToString().ToLowerInvariant()}-{Environment.ProcessId}.wav");
                File.WriteAllBytes(path, AudioCues.Wave(cue));
                this.files[cue] = path;
            }

            return path;
        }
    }

    private string? FindLinuxPlayer()
    {
        lock (this.sync)
        {
            if (!this.linuxProbed)
            {
                this.linuxProbed = true;
                var dirs = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
                this.linuxPlayer = new[] { "paplay", "aplay" }.FirstOrDefault(name => dirs.Any(d => File.Exists(Path.Combine(d, name))));
            }

            return this.linuxPlayer;
        }
    }

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern bool PlaySound(IntPtr sound, IntPtr module, uint flags);
}
