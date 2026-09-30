# Stage 1: baseline audit and .NET 10 upgrade

Scope: behavior-preserving retarget of the existing Windows app from `net8.0-windows` to
`net10.0-windows`. No application code, dependency versions, installer identity, or settings
format changed.

## Baseline

- Audited `main` at `87e0aaba0837206a8e30407f37922bc3b2208d81` (the brief's planning snapshot).
  `main` had not moved when this stage started; `origin` had no other branches or open PRs.
- Verified in source (not by running the app):
  - Backend IDs are the numeric enum `TranscriptionBackendKind` (`Whisper = 0`, `Parakeet = 1`,
    `Moonshine = 2`, `WhisperNet = 3`, `QualcommQnn = 4`) and are persisted in `settings.json`.
  - `ITranscriptionEngine.TranscribeAsync` returns `ValueTask<string>`; structured segments are
    discarded.
  - The live preview loop re-transcribes a rolling snapshot capped at 12 s
    (`LivePreviewMaxAudio` in `Program.cs`).
  - `DictationController` routes final text through voice command matching and can run configured
    shell commands (`VoiceShellCommandRunner`), which is why transcription mode needs its own
    controller.
  - The WiX payload harvests the publish folder with a wildcard (`installer/wix/shared/AppPayload.wxs`)
    and uses `MajorUpgrade` with the unchanged `UpgradeCode`, so runtime file renames between .NET 8
    and .NET 10 do not require installer edits.
- `AGENTS.md` still describes the pre-5.x layout (no Whisper.net, Qualcomm, wake word, history, or
  Ollama). It is being reconciled in a separate thread, so it is intentionally untouched here.

## Changes

| File | Change |
|------|--------|
| `PrimeDictate.csproj` | `net8.0-windows` to `net10.0-windows`. |
| `global.json` | New. Pins SDK `10.0.100` with `rollForward: latestFeature` (any stable 10.0.x SDK ≥ 10.0.100). |
| `.github/workflows/build.yml` | `setup-dotnet` reads `global.json` instead of `8.0.x`. |
| `README.md`, `installer/README.md` | Build prerequisite is the .NET 10 SDK. |

## What was verified, and where

All checks ran on Linux x64 with the .NET SDK 10.0.112 (Ubuntu package) and
`-p:EnableWindowsTargeting=true`. Windows-only code was compiled, never executed.

| Check | Result |
|-------|--------|
| `dotnet build -c Release` on `main` (net8.0-windows) | 0 warnings, 0 errors |
| `dotnet build -c Release` after retarget (net10.0-windows) | 0 warnings, 0 errors |
| `dotnet publish -r win-x64` and `-r win-arm64`, self-contained, both TFMs | Succeeded |
| Publish-folder diff, .NET 8 vs .NET 10 | Same file list apart from framework files (new WPF/runtime assemblies, versioned `mscordaccore_*`, localized WPF resources). All native payloads (`onnxruntime.dll`, `sherpa-onnx-c-api.dll`, QNN `Qnn*.dll`/`libQnnHtp*`, Whisper.net native runtimes) are present; `onnxruntime.dll` and `sherpa-onnx-c-api.dll` hashes match for both RIDs, so the ARM64 QNN copy target still wins. Three managed package assemblies change content at the same package version because their packages ship a `net10.0` build that NuGet now selects instead of the `net8.0` one: `SharpHook.dll` (7.1.1), `Whisper.net.dll` (1.9.0), and `sherpa-onnx.dll` (1.13.0). These are the main runtime risk in this stage and need a smoke test on Windows (hotkey, text entry, Whisper.net and sherpa transcription). |
| Source scan for .NET 9/10 breaking-change hotspots (BinaryFormatter, clipboard `DataObject` payloads, obsoletion warnings) | None found; only `Clipboard.SetText` is used. |

Not verified (needs a Windows machine or the Windows CI runner):

- Launching the app, hotkey, WASAPI capture, overlay, text injection, tray, settings migration.
- Any model inference, including QNN on ARM64 hardware.
- WiX MSI build and an upgrade install over 5.1.0 (WiX build runs only on Windows). The PR's CI run
  exercises build, publish, and MSI creation on `windows-latest`.

Windows behavior tests are owned by the "Add a first test project" thread; they should target
`net10.0-windows` once this lands.
