# System-audio capture (speakers) for meetings

Records what the computer plays, so remote participants of a call can be transcribed alongside the
microphone. Capture starts only on an explicit Record press, shows a red "Recording ..." line in the
UI, is never always-on, never persists across sessions, and stays local.

## Design

| Piece | Where | Notes |
|---|---|---|
| Contracts | `Core/Providers/AudioContracts.cs` | `ISystemAudioSource`, `AudioDeviceKind`, `AudioSourceErrorKind.NotSupported`. Devices of a system source are **output** endpoints. |
| Mic + system on one clock | `Core/Audio/CombinedAudioSource.cs` | One `IAudioCaptureLease`; existing session code needs no change. |
| Windows | `Platforms/Audio/WasapiLoopbackCaptureSource.cs` | NAudio `WasapiLoopbackCapture`, shared mode. **Compiles; never run.** |
| Linux | `Platforms/Audio/PulseMonitorCaptureSource.cs` | libpulse-simple records a sink's `.monitor`. Covers PipeWire through pipewire-pulse. **Run against a virtual sink.** |
| macOS | `Platforms/Audio/MacOsSystemAudioSource.cs` | Stub, throws `NotSupported`. **Not implemented.** |
| UI | `Desktop/MainWindow` | Source dropdown (microphone / system audio / both) and the recording line. |

### Channels vs mixed: stereo (mic left, system right)

`CombinedAudioSource` defaults to **two channels**, mic on 0 and system audio on 1, sample-aligned,
16 kHz. Reasons: the existing session already downmixes multichannel frames to mono for ASR, so it
works today; keeping the channels lets diarization use "local vs remote" as a free first split and
lets a later step cancel speaker bleed into the mic; a mixed stream cannot be un-mixed. `Mixed` (sum,
clamped) is available via `CombinedAudioOptions.Layout` if a provider wants one channel. Note that
without headphones the mic also hears the speakers, so both channels contain remote speech.

### Sync and no silent loss

- Each side is resampled to the common rate and counted in output samples. Alignment is by arrival
  time: a side that starts late or stalls more than `MaxSkew` (200 ms) is padded with silence to
  match the other. Accuracy is limited by device buffering.
- Samples a device reports lost (sequence gap) are padded, and the combined sequence number skips so
  `LiveTranscriptionSession` records a `Gap`. Padding is counted in `ICombinedAudioCaptureLease`.
- Buffers are bounded (5 s per side, 500 output chunks, plus each device's 30 s queue). Full buffers
  push back on the device queue; the audio thread never blocks and never drops without a gap.
- Windows loopback sends no packets during silence, so the lease inserts zeros for any stretch
  with no data (50 ms threshold) to keep its clock running.
- Pause pauses both devices, waits 100 ms for frames in flight, then flushes the shorter side.

## What was and was not run

Run (Linux container, PulseAudio with two null sinks, .NET 10):
- 5 new unit tests for the combiner (`CombinedAudioSourceTests`, fake leases): channel placement,
  48 kHz to 16 kHz, mixed layout, stalled side, lost samples, pause. Whole Core suite: 74 pass, 5 runs.
- Real capture: 440 Hz into one sink and 880 Hz into another landed on the right channels, no
  sequence gaps; close/reopen on Pause recorded nothing while paused; bad device gave
  `DeviceRemoved`; no server gave `Unknown: Connection refused`.
- Measured: the first frame from a null sink's monitor arrives about 2 s after open, in this
  container, with `parec` too, so it is PulseAudio not this code. Real hardware is untested, so no
  latency claim. The UI shows Resuming semantics from the earlier spike only for the mic path.

Not run: everything on Windows (WASAPI loopback, silence fill, device removal, default-device
change), real PipeWire, Wayland, any macOS, the Avalonia picker on screen, the WPF app.

Known limits: default-output changes mid-session are not followed on any platform; the Linux device
list needs `pactl` (falls back to just "Default output" without it); on Linux the stream appears in
pavucontrol as "PrimeDictate system audio".

## Windows test steps (for the Remote Control session)

1. `dotnet build src/PrimeDictate.Desktop` on the branch, run it, pick a model.
2. Play a YouTube video, choose "System audio (speakers)", Record 20 s, Stop. Expect the video's
   speech as text; export and check the timeline has no `Gap`.
3. Pause the video for 5 s mid-recording (silence): recording must continue, timestamps must not
   drift when speech resumes.
4. "Microphone + system audio": talk while the video plays; both should transcribe. Also try with
   headphones plugged in and with a Bluetooth headset (format changes are the likely failure).
5. Unplug/disable the output device mid-recording: expect an error message, transcript kept.
6. Pause/Resume, and confirm the OS recording indicator behavior.

## macOS research (not verified on hardware)

- Core Audio process taps (`AudioHardwareCreateProcessTap`, macOS 14.2+): tap all or selected
  processes' output; needs the "System Audio Recording Only" privacy permission and an
  `NSAudioCaptureUsageDescription` in Info.plist. Needs native interop (Swift/ObjC shim or P/Invoke
  through libobjc); Apple's sample plus the community AudioCap project show the flow.
- ScreenCaptureKit (macOS 13+) audio: works but requires Screen Recording permission, which is a
  scarier prompt. Fallback for macOS 13.
- No-code option: the user installs a loopback driver (BlackHole) and routes output to it; it then
  shows up as an ordinary input for the existing microphone source. The stub's message says this.
- Permission denial should map to `AudioSourceErrorKind.PermissionDenied`.

## Session wiring

The picker maps to `TranscriptSourceType` (Microphone, SystemAudio, Meeting) and
`StartLiveAsync(..., source, systemDeviceId)` builds the matching source: Meeting uses the combined
lease. `LiveTranscriptionSession` (main thread's change) keeps a stereo file for meetings and reads
channel 0 as the microphone and channel 1 as system audio. `CombinedMeetingTests` proves that order
end to end. Not built: an output-device picker, per-app capture, and following default-output changes.

## Boost quiet audio (auto gain)

`AutoGain` (Core) lifts quiet audio toward about -20 dBFS RMS before recognition, in system-only and
meeting sessions, on by default (checkbox "Boost quiet audio for recognition"). It runs per channel:
the system channel can gain up to 30x, the meeting microphone only 4x so room noise is not amplified
into "speech". Blocks below about -48 dBFS never raise the gain, gain is never below 1, and a soft
limiter keeps peaks under full scale. It feeds recognition only: the saved recording stays as captured
(so playback is honest and gain can be retuned), which also means a rerun of a saved quiet meeting
does not get the boost yet. Unrelated to Windows communications ducking, which lowers what you hear
and may lower what loopback captures; it is set in Windows Sound settings, Communications tab.
