# Stage 7: dictation in the new app, status

Branch `claude/dictation-parity-4k0tl9`, following `stage-7-parity-plan.md`. "Ran" means executed on Linux in a container; "Compiled only" means it builds but has not been run on that OS.

**Status (2026-09-30):** merged into `main` (PR #3). Release 6.0.0 ships the WPF app; this app is not in the installers yet. Hotkey and wake-word dictation, the overlay and the settings window were first run on Windows the same day (see the Windows notes below). Whisper.net models (the WPF backend Justin uses) are now in this app (see the Whisper.net row). Before it can replace the WPF app it still needs an updater and its own installer.

## Done

| Piece | Where | Verified |
|---|---|---|
| Dictation loop (toggle, live preview to overlay only, silence auto-commit, emergency stop, one final transcript) | `PrimeDictate.Core/Dictation/DictationController.cs` | Ran: 16 unit tests with fake capture, model, guard and typing |
| Speech activity (adaptive noise floor, same constants as the WPF app) | `SpeechActivityTracker.cs` | Ran: unit tests |
| Final delivery: foreground guard, refuse when focus moved, optional restore, coding-mode Enter only after typing succeeded | `Delivery.cs` | Ran: unit tests |
| Post-processing: trailing "ok/okay" silence artifact, replacements | `TranscriptPostProcessor.cs` | Ran: unit tests |
| Long recordings split at a quiet point (Whisper takes 30 s) | `AudioChunker.cs` | Ran: unit test |
| Hotkeys: matching rules, key names read from the WPF `settings.json` | `Hotkeys.cs`, `SharpHookHotkeySource.cs` | Matching and Wayland detection ran; the SharpHook hook itself was not exercised |
| Settings: reads the WPF `settings.json` read-only, writes `dictation-settings.json` | `DictationSettings.cs` | Ran: unit tests (the WPF file is byte-identical after a save) |
| Host wiring and hotkey routing | `PrimeDictate.Platforms/Dictation/DictationHost.cs` | Ran: unit tests with a fake hotkey source |
| Typing: SharpHook `SimulateTextEntry` (macOS, Linux); `SendInput` port on Windows | `SharpHookTextInjector.cs`, `WindowsSendInput.cs` | Compiled only |
| Windows foreground guard and restore | `WindowsForegroundGuard.cs` | Compiled only |
| Avalonia tray (state-colored dot), overlay, settings window, hotkey capture | `PrimeDictate.Desktop/Dictation/` | Ran: app starts under Xvfb without errors for 20 s. Not visually checked, hotkeys and typing not exercised |

| Voice commands (commit / discard / history phrases, incl. "ok"/"okay" and "thanks"/"thank you" variants) | `VoiceCommandMatcher.cs`, `VoiceCommands.cs` | Ran: unit tests. Ported from Justin's committed version (b2e6dcc). Dictation only; transcription mode has no hook |
| Wake word (matching and idle-mic listener, yields the mic to dictation and to transcription sessions via `MicrophoneCoordinator`) | `WakeWord.cs`, `DictationHost.cs` | Ran: unit tests with a fake microphone. Not tried with a real microphone or model |
| Particle overlay (150 particles on a vector field, scrolling mirrored waveform) | `OverlayVisualizer.cs` (physics, tested), `VisualizerControl.cs` (drawing) | Physics ran; drawing seen in an offscreen render |
| Overlay behavior, after Justin's first Windows run: shown only while dictating (and briefly for a notice or the final words) unless "Keep the overlay on screen" is on; compact = microphone pill, full = fixed-size box with the latest four lines, so neither resizes as words arrive; drag anywhere, the spot is remembered (`OverlayAnchorX/Y`, new app only) and kept on a connected screen; ✕ hides it until the next dictation; not click-through, still never activated | `DictationOverlayWindow.cs`, `Win32Overlay.cs` | Rendered offscreen and looked at; dragging and ✕ not yet tried on Windows |
| Settings window fits the screen: never taller than the working area, the form scrolls, Save stays in view, long labels wrap | `DictationSettingsWindow.cs`, `ModelDownloadPanel.cs` | Rendered offscreen and looked at |

| Ollama rewrite (loopback only unless the user allows a remote endpoint; failures type the raw text) | `OllamaRewriter.cs` | Ran: unit tests with a fake HTTP handler |
| Dictation history (SQLite, search, delete, clear; imports WPF `history.json` once, read-only) and window | `DictationHistory.cs`, `DictationHistoryWindow.cs` | Store ran: unit tests. Window not looked at |
| Audio cues (synthesized WAV; Windows `PlaySound`, macOS `afplay`, Linux `paplay`/`aplay`) | `AudioCues.cs`, `ProcessAudioCuePlayer.cs` | WAV generation ran; playback not heard on any OS |
| Tray icon: the WPF "voice wire" icon redrawn in Avalonia, five states | `TrayIconRenderer.cs` | Rendered under Xvfb and viewed (`--render-tray-icons <dir>`); not seen in a real tray |
| `--show` / `--workspace` (accepted; the window is the default start) and new `--background` (tray only, Windows and macOS) | `App.axaml.cs` | Not run on Windows |
| Parakeet TDT v2/v3 and Moonshine v1/v2 (CPU) as dictation models, with download (same ids, folders, archives and file rules as the WPF catalogs) | `Platforms/Speech/SpeechModels.cs`, `SherpaWhisperProvider.cs` (Parakeet and Moonshine providers), `DictationHost.cs` | Ran, here on Linux with the real models: downloaded through the app's downloader, validated, and transcribed the sherpa test clips correctly (Moonshine tiny v2, Moonshine base v1, Parakeet v3), plus unit tests for discovery and settings. Not run: Windows, live dictation with these models, GPU. Moonshine QNN/NPU is not ported (Whisper.net is, next row). The wake listener still prefers Whisper tiny/base |
| Whisper.net (ggml) models for dictation and the transcription workspace: same ids (large-v3-turbo, large-v3, base.en, tiny.en), file names, `models/whisper.net/` folder, Hugging Face URLs and sizes as the WPF catalog, so an installed file is shared. Single-file download (temp file, size check, then move into place) in Settings and first run. `WhisperNetProvider` shares one loaded model per file (ref-counted), so dictation and the workspace load large-v3-turbo on the GPU once; one inference at a time per loaded model. English-only models are forced to "en"; other models use "en" unless a language or "auto" is given, as in WPF. Device: the WPF `TranscriptionComputeInterface` (Cpu/Gpu/Npu) is read from `settings.json`, plus a new Auto/CPU/GPU choice in Settings (`WhisperNetDevice`, applies after restart; `PRIMEDICTATE_WHISPERNET_DEVICE` overrides). Library order as WPF (CPU: Cpu, CpuNoAvx; GPU: Cuda, Vulkan, Cpu, CpuNoAvx; Auto and NPU: OpenVINO first only when the model has its OpenVINO files, else CPU), applied once before the first model loads; the loaded library is logged and a notice is shown when the GPU was wanted but not loaded. A WPF file with `TranscriptionBackend=WhisperNet` now selects `whisper-net:<id>` (before it looked for a Whisper ONNX model of that name). The workspace picker starts on the dictation model, else large-v3-turbo. Wake word still prefers small sherpa Whisper models | Ran, on Windows 11 with an RTX 5070 and the real `ggml-large-v3-turbo.bin`: a 6.2 s synthetic clip through the provider loaded the **Cuda** runtime, 2.5 s for the first call including model load, 0.24 s warm; a second provider on the same file reused the loaded model (one load, two leases). CPU for comparison: 16 s per call. Unit tests: catalog, locator, download, id mapping, settings migration (WPF file byte-identical), library order, cache ref-counting. Publish: `win-x64` and `win-arm64` self-contained both build; the win-x64 output has `runtimes/{cuda,vulkan,openvino}/win-x64` and `runtimes/win-x64` whisper natives (Whisper.net also copies its linux and macOS natives; 344 MB of output in total). Not run: Vulkan, OpenVINO/NPU, the download through the app UI, dictation by hotkey with this model, the new settings control looked at |
| ONNX models on CUDA (Auto/CPU/CUDA setting, ONNX Runtime GPU pack, checked fallback with a note, fp32 Whisper, fp16 Parakeet v2) | `Platforms/Speech/OnnxRuntimeDevice.cs`, `docs/architecture/onnx-cuda.md`, `scripts/Install-OnnxGpuRuntime.ps1` | Ran: unit tests with a fake GPU probe. Not run: any real GPU, the GPU DLLs, Windows |
| Clean exit (tray Exit, `--quit`, one running instance, recording saved and dictation dropped on exit) | `Platforms/Startup/SingleInstance.cs`, `App.axaml.cs`, `Program.cs` | Ran: unit tests; on Linux under Xvfb `--quit` shut the app down, a second launch exited without a second copy, `--quit` with nothing running exits 0. Not run: on Windows, with a real recording |
| Whisper model download (same 7 models, GitHub release URLs and install folders as WPF, so installs are shared; staged unpack, validated before it is moved into place; progress and cancel in Settings and first run) | `Platforms/Speech/WhisperModelDownloader.cs`, `ModelDownloadPanel.cs` | Ran: unit tests with a fake HTTP server and real `tar` (good archive, incomplete archive, HTTP error, broken archive, already installed). Confirmed from this container that the real tiny.en archive is reachable, 118,071,777 bytes as in the catalog, and holds both full and int8 files (the locator prefers int8). Not run: a full real download through the app, the progress UI, `tar` on Windows |
| Launch at login (current user only, tray-only `--background`; Windows Run key value `PrimeDictate.Desktop`, macOS LaunchAgent, Linux XDG autostart; separate from WPF's startup shortcut, so running both starts both; refuses under the `dotnet` host) | `Platforms/Startup/LaunchAtLogin.cs`, checkbox in Settings | Ran: unit tests for the file writers on Linux and the Windows branch against a fake Run key. Not run: the real registry, a real sign-in on any OS. No all-users scope (WPF's needed elevation) |
| Stats and milestones (words, time saved vs a typing baseline, speaking WPM, 14-day bars, 1k/10k/100k/1M word milestones with a tray notice; same `stats.json` and JSON shape as WPF, so lifetime totals carry over; built from history on first run) | `Core/Dictation/DictationStats.cs`, `DictationStatsWindow.cs`, tray menu | Ran: unit tests (counting, milestones once, WPF file read, corrupt file, day gaps). Window compiles and is not looked at. The baseline-WPM setting is read from the WPF file but has no editor in the new Settings yet. WPF and the new app writing the same file at once could lose an update |
| First-run check (mic, model, hotkey, typing guard, model choice, model download) | `DictationOnboardingWindow.cs` | Starts under Xvfb without errors; not looked at |

## Rules kept from AGENTS.md

Final-only typing, no clipboard, no live retyping into the target, Enter only after a guarded successful commit, hook thread only raises an event and work is offloaded, one gate serializes toggle/commit/discard.

## Platform behavior

- **Windows**: full guard, restore-to-start-target, `SendInput` typing. Needs a real test.
  - The microphone opens through miniaudio (WASAPI), which lists devices by name. A microphone saved by the WPF app is a
    Windows endpoint id, so it is looked up by the name Windows gives it; one that no longer exists falls back to the
    default microphone, as the WPF app does, with one notice and an `app.log` line. Before this, dictation and the wake
    word never opened a microphone for a WPF user whose saved device was gone (Justin's case). Checked on his PC: the
    wake listener opened the default microphone 3 s after launch.
  - `app.log` also records why dictation did not start, why a transcript was not typed, when the wake word stops
    listening, and hook failures. Never recognized text.
- **macOS, Linux**: no foreground check exists yet, so dictation does **not** type unless the user turns on "Type even when the app cannot check which window is in front". This follows the parity plan. macOS also needs Accessibility permission for hotkeys; Wayland has no global hotkeys and the app says so.
- Closing the main window hides it to the tray on Windows and macOS; on Linux it quits (no guaranteed tray host).

## Not done yet

- (Voice shell commands are now done, see the table.)
- Updater, installers.
- Wake word uses a Tiny/Base Whisper model when installed, else the dictation model (as WPF does). Moonshine/Whisper.net wake models are not ported.
- The WPF app's focused-edit-control insertion (`WindowsFocusedTextControl`) and direct injection into the original target.
- Compact-mode ripple animation and the copy/pin buttons of the WPF overlay, macOS `NSPanel`, Linux X11 hints.
- First-run onboarding, launch at login, updater, packaging.

## One app: tray, window and settings (6.1.0 work)

| Piece | Where | Verified |
|---|---|---|
| Voice shell commands run in dictation only: matched in the final transcript (never in the live preview, never in transcription), the command string comes only from settings, Stop/Continue as in WPF (Continue types the words that remain), history row "Voice command: phrase". Imported WPF commands keep their On state; commands added here start Off; the Settings editor (On / Say / After / Command) shows a warning | `DictationController.cs`, `VoiceShellCommandRunner.cs` (interface), `ProcessVoiceShellCommandRunner.cs` (cmd.exe /d /c, /bin/sh -c), `DictationSettingsWindow.cs` | Ran: unit tests with a fake runner. Real process start and the editor not exercised by hand |
| Tray menu: Start dictation, Record meeting (flips to Stop meeting recording; same code as the Record button, so either can stop the other; opens the window), Open PrimeDictate, Dictation history, Stats, Settings, Exit. Tray icon and tooltip show recording during a meeting | `DictationShell.cs`, `MainWindow.axaml.cs` | Compiled and started on Windows; menu clicks and a real recording not exercised |
| Main window header: dictation state, model, hotkey, buttons for history, stats, settings | `MainWindow.axaml` | Started on Windows and looked at |
| Transcription model defaults to the dictation model when none was chosen for transcription | `MainWindow.axaml.cs` | Not exercised (no model in the test data dir) |
| One Settings window with a "Transcription & meetings" section (source, live text, speaker labels, boost), kept in sync with the main window; stored in `transcription-settings.json`, the old `meeting-live-text.txt` is carried over once | `TranscriptionPrefsService.cs`, `TranscriptionPreferences.cs` | Defaults and load: unit tests. Sync not exercised by hand |
