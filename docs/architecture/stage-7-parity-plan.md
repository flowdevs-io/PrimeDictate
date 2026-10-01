# Stage 7: rebuild the good parts of the WPF app in the new desktop app

Direction from Justin: the new app becomes the whole product, carrying everything good from the old WPF app. The old app stays buildable and shippable until the new one reaches parity.

Inventory is from the code on this branch. Justin has uncommitted edits (including `WakeWordListener.cs` and `VoiceCommandMatcher.cs`) that are not here, so those two are ported last, from his committed version.

## Inventory and plan

| Feature (old file) | What it does | Plan in the new app |
|---|---|---|
| Global hotkeys (`GlobalHotkeyListener` in Program.cs) | SharpHook start/stop, emergency stop, history hotkey | **Port.** SharpHook is cross-platform. Move to `Platforms`, behind `IHotkeySource`. Hotkey capture UI redone in Avalonia. macOS needs Accessibility permission; Wayland has no global hook (X11 and XWayland only), shown honestly in Settings. |
| Dictation controller (`DictationController`) | Toggle gate, live preview, silence auto-commit, voice commands, post-processing, delivery | **Redo in Core** as small parts, following `split-large-files.md`: `SpeechActivityDetector` (my `UtteranceDetector` already covers most of it), `TranscriptPostProcessor`, `TranscriptDelivery`, `DictationOptions`. Behaviour must match the old app, tested against the old logic. |
| Recorder (`DefaultMicrophoneRecorder`) | WASAPI capture, gain, trim, resample | **Redo.** Use the shared capture source (`MiniAudioCaptureSource`) plus gain. Keep WASAPI in the WPF app only. |
| Overlay (`TranscriptionOverlayWindow`) | Non-activating live transcript overlay, compact mic mode | **Redo in Avalonia.** Topmost, no-activate window: Win32 flags on Windows, `NSPanel` on macOS, X11 hints on Linux. Compact mic and full modes, sticky, placement. |
| Final-only typing (`WindowsUnicodeInput`, SharpHook) | One `SimulateTextEntry` on commit | **Port the rule** (final-only, never clipboard paste, never live retyping). SharpHook typing works on all three platforms. The AGENTS.md invariants carry over unchanged. |
| Foreground guard and return-to-start target (`WindowsInputHelpers`) | Refuses to type if focus moved; can restore the start window | **Windows-only port** behind `IForegroundTargetGuard`. macOS and Linux get a best-effort guard (frontmost app id); where none exists the app says so and does not type. |
| Coding mode Enter | Enter only after a successful, guarded injection | **Port** (pure logic, tested). |
| Wake word (`WakeWordListener`, `WakeWordModelResolver`) | Idle mic watcher for a wake phrase | **Port late,** from Justin's committed version, onto `MicrophoneCoordinator` (already designed for it). |
| Voice commands (`VoiceCommandMatcher`) | Commit, discard, history, custom commands | **Port late** (same reason). Stays dictation-only. Transcription mode never matches commands. |
| Voice shell commands (`VoiceShellCommandRunner`) | Runs user-configured commands from a phrase | **Keep as an explicit opt-in in dictation only.** Commands are user-defined, off by default, with a visible warning. Never in transcription mode. |
| Replacements (`TranscriptReplacement`) | Ordered find/replace before typing | **Port first** (pure logic). Done in this commit. |
| Ollama modes (`OllamaPostProcessor`) | Optional local rewrite | **Port.** It's a plain HTTP client to a local endpoint. Loopback default, and failure falls back to raw text. |
| History (`TranscriptionHistory`) | Committed-transcript log with delivery status | **Redo on the new SQLite store** as a "Dictation" history separate from transcription sessions, with import from the old file. |
| Stats and achievements (`DictationStats`) | WPM, daily bars, milestones | **Port** the logic; Avalonia UI later. |
| Settings (`AppSettings`, `SettingsWindow`) | ~30 settings, 5 backend tabs, stats | **Redo.** Read the old `settings.json` unchanged (migration, never rewrite it), then own settings. Avalonia settings window built from a per-backend adapter, not five copies. |
| Model catalogs (Whisper, Parakeet, Moonshine, Whisper.net) | Folder rules and downloads | **Move to Core/Platforms** as one catalog behind `IModelCatalog`, keeping the same folders so installed models keep working. |
| Engines: Whisper ONNX, Parakeet, Moonshine (sherpa) | Local ONNX recognition | **Port.** Whisper is done; Parakeet and Moonshine come next as providers. |
| Engines: Whisper.net, Qualcomm QNN/AI Hub | Windows-specific runtimes, ARM64 NPU | **Ported** as Windows-only providers: Whisper.net (CPU, CUDA, Vulkan, OpenVINO NPU) and the Qualcomm NPU models (AI Hub Whisper Small, Moonshine) behind machine gating. See `qualcomm-npu.md` and the status doc. |
| Tray (`App.xaml.cs`) | Notification icon, Settings/Exit, state tooltip | **Redo** with Avalonia `TrayIcon` (Windows, macOS, most Linux desktops). |
| Launch at login (`LaunchAtLoginManager`) | Registry / scope handling | **Redo per OS:** registry on Windows, LaunchAgent on macOS, autostart `.desktop` on Linux. |
| Updater (`GitHubUpdateService`) | Checks Releases, downloads MSI, verifies checksum | **Redo per OS, last.** Windows keeps the MSI flow. Checks are read-only; installing is always user-initiated. No signing or release changes without Justin. |
| First-run onboarding | Model choice and setup | **Redo** on the new settings and model catalog. |
| Audio cues | Start/stop sounds | **Port** through the shared playback. |
| Installers (WiX MSI, winget, Chocolatey) | Windows distribution | **Keep.** New app gets its own packaging in stage 7b; nothing here changes existing installers. |

## Order

1. Pure logic with tests, no OS calls: replacements, Enter/commit decision, options, post-processing, stats. **Replacements start now.**
2. Provider and catalog: Parakeet and Moonshine through sherpa, one catalog.
3. Dictation loop in Core on the shared capture source, with fakes in tests.
4. Hotkeys, typing and the foreground guard behind interfaces, Windows first.
5. Overlay, tray, settings, first-run in Avalonia.
6. Wake word and voice commands, from Justin's committed versions.
7. Launch at login, updater, packaging per OS.
8. Parity checklist against this table; only then does the old app stop being the shipping one.

## What I will not do

Ship, publish or sign anything, change installers, or touch the old app's behaviour. I can only compile and unit-test the Windows-only parts on Linux, so each Windows-specific step is marked "not verified on Windows" until Justin's session runs it.
