# Agent notes for PrimeDictate

This file orients coding agents and future maintainers. It is not an end-user manual; see [README.md](README.md) for that.

## Purpose

**PrimeDictate** is one .NET 10 app for hotkey dictation and for transcription and meetings. From 6.1.0 the installers ship the Avalonia app in `src/PrimeDictate.Desktop` as `PrimeDictate.exe`. The WPF app at the repository root (what 6.0.0 shipped) is legacy: it still builds, and it is not packaged. The Qualcomm QNN/AI Hub models are in both apps (the new app's code is under `src/PrimeDictate.Platforms/Speech/Qualcomm`; see `docs/architecture/qualcomm-npu.md`). The dictation behavior below is shared by both; the WPF file names in the Layout table are the legacy implementation. The behavior:

1. Listens for a **global** hotkey (`Ctrl+Shift+Space` / SharpHook) to start and stop capture.
2. Records from the **default** Windows input device using **WASAPI** (NAudio `WasapiCapture`), normalizing to **16 kHz, 16-bit, mono** PCM.
3. Runs local **ONNX** speech models through **sherpa-onnx** for live preview and final transcription.
4. Shows live transcript hypotheses in a non-activating WPF overlay, then injects the final result with **SharpHook** `EventSimulator.SimulateTextEntry` (unicode text simulation), not clipboard + synthetic paste. Optional coding mode sends `Enter` after a successful final commit.

## Layout

| File / folder | Role |
|------|------|
| `Program.cs` | `Main`, hotkey listener, `DictationController` toggle, `DefaultMicrophoneRecorder`, `PcmAudioBuffer`. |
| `ModelStorage.cs` | Shared managed model root under `%LocalAppData%\PrimeDictate\models`. |
| `WhisperModelCatalog.cs` | Whisper ONNX catalog, folder validation, download, and archive extraction. |
| `ParakeetModelCatalog.cs` | Parakeet ONNX catalog, folder validation, download, and archive extraction. |
| `MoonshineModelCatalog.cs` | Moonshine ONNX catalog, folder validation, download, and archive extraction. |
| `TranscriptionEngines.cs` | Shared transcription abstraction; Whisper, Parakeet, and Moonshine ONNX engines with lazy runtime/model load. |
| `WhisperTextInjectionPipeline.cs` | Transcription orchestration, logging, and final-only text injection. |
| `WindowsInputHelpers.cs` | Foreground-window guard for final injection and optional Windows Mouse Sonar pulse. |
| `TranscriptionOverlayWindow.xaml` | Non-activating live transcript overlay; placement is user-configurable. |
| `PrimeDictate.csproj` | The legacy WPF app (6.0.0; not packaged since 6.1.0). Target `net10.0-windows` (SDK pinned in `global.json`); NAudio, SharpHook, sherpa-onnx; references `src/PrimeDictate.Core`. |
| `Directory.Build.props` | Shared assembly/file `Version` (installers read this too). |
| `scripts/Publish-Windows.ps1` | Self-contained publish of `src/PrimeDictate.Desktop` (PrimeDictate.exe) for one runtime (`win-x64` or `win-arm64`) to `artifacts/<rid>/publish`. |
| `scripts/Build-Installers.ps1` | Publishes then builds the online WiX `.wixproj` (NuGet `WixToolset.Sdk`) to x64 and ARM64 MSIs in `artifacts/installer`. |
| `installer/wix/` | WiX: `online/` is the shipped MSI (app payload only; models are downloaded in the app), with Start Menu and optional launch-at-login shortcuts; `offline/` is not built. `Branding.wxs` + `PrimeDictate.ico` for ARP/exe icon. |
| `src/PrimeDictate.Core` | Portable core used by both apps: transcript model, SQLite session store, pipeline, dictation controller, audio and provider contracts. |
| `src/PrimeDictate.Platforms` | Platform adapters: microphone capture (miniaudio; WASAPI loopback for system audio), speech providers (sherpa-onnx, Nemotron), hotkeys, typing, launch at login (`Startup/`), the GitHub updater (`Updates/`), the meeting final pass. |
| `src/PrimeDictate.Desktop` | The shipped app (6.1.0+), assembly name `PrimeDictate`: dictation, tray, overlay, transcription workspace and meetings, updater (`Updates/`). Avalonia; runs on Windows, macOS and Linux, installers are Windows only. |
| `tests/PrimeDictate.Core.Tests` | xUnit tests for Core and Platforms: `dotnet test tests/PrimeDictate.Core.Tests`. |
| `docs/architecture/` | Design notes and status per stage; `meeting-v1-acceptance.md` is the Meeting v1 test. |

## Conventions to preserve

- **ONNX model folders**: Whisper folders contain encoder ONNX, decoder ONNX, and tokens; Parakeet/Moonshine have their own required ONNX file sets in their catalogs.
- **Native / unmanaged**: Prefer `await using` and explicit disposal paths; do not add redundant `try`/`catch` unless there is a clear recovery story.
- **Hotkey handler**: The hook runs on SharpHook's thread; work is offloaded with `Task.Run` and `await` the dictation path carefully to avoid re-entrancy issues. `DictationController` uses a `SemaphoreSlim` for toggle mutual exclusion.
- **Text injection**: **Do not** reintroduce "set clipboard + simulate paste + immediately restore old clipboard" without solving async paste delivery (delay, flush, or full clipboard snapshot/restore). The vetted baseline is final-only target `SimulateTextEntry` (see product README for rationale).
- **Editor stability**: Live updates belong in the overlay, not in the target editor. Do not reintroduce live backspace/re-type correction into the focused app without a robust target/caret/completion strategy.
- **Coding mode Enter**: The optional Enter key is sent only after final text injection succeeds and the foreground-window guard passes.
- **Model path**: Keep model-folder validation and download layout in the model catalog classes; do not scatter model filename assumptions through UI or engine code.

## Dependencies (NuGet)

- **org.k2fsa.sherpa.onnx** for ONNX speech recognition runtimes and managed bindings.
- **NAudio** for capture and resampling.
- **SharpHook** for the global hook and `EventSimulator`.
- Under `src/`: **Avalonia** (the new app), **SoundFlow** (miniaudio capture) and **Microsoft.Data.Sqlite** (session store and dictation history).

**TextCopy** is not used; do not add it back unless you implement a clipboard strategy that is demonstrably free of the paste/restore race.

## Extension points (expected evolution)

- **Hotkey**: Change `IsDictationHotkey` in `GlobalHotkeyListener` (`Program.cs`); key codes in `SharpHook.Data.KeyCode`.
- **Transcription engines**: Add new local model runtimes behind `ITranscriptionEngine` in `TranscriptionEngines.cs`; keep text injection out of engine implementations.
- **Whisper options**: Whisper uses sherpa-onnx `OfflineRecognizerConfig.ModelConfig.Whisper`; add provider/thread/language controls there when needed.
- **Non-Windows audio**: `DefaultMicrophoneRecorder` is Windows-centric (`WasapiCapture`); a cross-platform build would need an abstraction and platform-specific capture.

## Shipped app (6.1.0+)

- **Output**: `src/PrimeDictate.Desktop` builds `PrimeDictate.exe` (AssemblyName `PrimeDictate`, RootNamespace stays `PrimeDictate.Desktop`). The version comes from `Directory.Build.props`.
- **Launch at login is one mechanism** (`Platforms/Startup/LaunchAtLogin.cs`): the MSI installs the all-users Startup shortcut `PrimeDictate.lnk` running `--background`, and the Settings checkbox turns that shortcut on or off per user through the Explorer StartupApproved switch. Only when the shortcut is absent (`LAUNCHATLOGIN=0`, portable copy) does it use the per-user Run value `PrimeDictate`. Never both. `--from-login` (the 6.0.0 shortcut argument) is treated as `--background`. The first start removes the 6.0.0 per-user shortcut and stale Run values.
- **Updater** (`Platforms/Updates`, `Desktop/Updates`): GitHub Releases, asset `PrimeDictate-Setup-vX.Y.Z-<x64|arm64>.msi` plus its `.sha256`, msiexec handoff after the clean exit path (`App.ExitAsync`). Windows only; installing is always user-initiated.
- **Single instance** keeps the name `PrimeDictate.Desktop.<user>`; it does not detect the legacy WPF app.

## Legacy WPF shell + onboarding notes (6.0.0)

The WPF tray/onboarding milestone (legacy, not packaged since 6.1.0):

1. **Host process**: WPF tray host (`App.xaml`) with notification icon, Settings/Exit menu, live transcript overlay, and Ready/Listening/Processing tooltip state.
2. **User settings**: Persisted under `%LocalAppData%\PrimeDictate\settings.json`; loaded at startup and applied to `GlobalHotkeyListener`.
3. **First run**: Missing/incomplete settings show `SettingsWindow` before normal tray-only behavior.
4. **Installers**: online MSIs only (x64, ARM64). They do not launch the app from the finish page; launch at login is on by default and `LAUNCHATLOGIN=0` turns it off.
5. **Preserved invariants**: hook-thread offload, foreground-window guard, overlay-only live preview, ONNX model-folder validation, and final-only target `SimulateTextEntry` baseline remain intact.

## Build and test hints

- **Windows installers**: See [installer/README.md](installer/README.md). `scripts/Build-Installers.ps1` publishes `src/PrimeDictate.Desktop` and builds the MSIs locally (no signing inputs needed); CI builds the online x64 and ARM64 MSIs; users download a model in the app's first-run setup or Settings.
- The legacy WPF app (`PrimeDictate.csproj`) must keep building (`dotnet build PrimeDictate.sln`). Run from the **repository root** so `./models/whisper/<model folder>` can be discovered during development when models are staged in the repo-local `models` tree.
- A running `PrimeDictate.exe` from `dotnet run` can **lock the apphost**; stop the process if `MSB3021` / copy-to-output fails.
- Linter: project should build with **0 warnings** under default SDK analysis when possible; platform-specific API use should stay behind `OperatingSystem` checks or documented trade-offs.

## Releases

- Merging into `main` publishes nothing. A release is a "Release X.Y.Z" commit on `main` that bumps `Directory.Build.props` (`Version`, `AssemblyVersion`, `FileVersion`), then a pushed `vX.Y.Z` tag.
- The tag run of `.github/workflows/build.yml` builds and signs `PrimeDictate.exe` (the Avalonia app), publishes the GitHub Release (x64 and ARM64 MSIs with checksums), pushes the Chocolatey package (then Chocolatey moderation) and opens a winget-pkgs PR (then winget validation and moderation). Details and fixes: [installer/README.md](installer/README.md).
- Never merge to `main`, tag a release or change signing without the owner's explicit go-ahead.

## Out of scope (unless explicitly requested)

- Cloud APIs, always-on online STT, or shipping large model blobs inside the repository; document/download them instead.
