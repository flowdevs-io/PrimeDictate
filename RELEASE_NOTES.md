# PrimeDictate 6.1.0

## Highlights

- One app. The installers now ship the new PrimeDictate that does both jobs in one process: hotkey dictation from the tray, and the transcription and meeting workspace (recordings, speaker timeline, sessions). It is still `PrimeDictate.exe`, so shortcuts, winget, Chocolatey and the updater keep working, and 6.1.0 upgrades 6.0.0 in place.
- Your setup carries over. The new app reads the 6.0.0 settings and history once (it never rewrites them), keeps the same model folders under `%LocalAppData%\PrimeDictate\models`, and keeps the lifetime stats.
- Launch at login is one thing again. The installer's Startup shortcut now starts PrimeDictate in the tray (`--background`), and the app's "Start PrimeDictate when I sign in" checkbox turns that same shortcut on or off for you without administrator rights. `LAUNCHATLOGIN=0` still leaves it out; the checkbox then uses a per-user startup entry instead. The 6.0.0 per-user startup entries are cleaned up on first start.
- Record a meeting from the tray. "Record meeting" starts a microphone and system-audio recording in the workspace; the same item stops it. The main window shows dictation status and opens history, stats and Settings, and there is one Settings window for dictation and for transcription and meetings.
- Same models everywhere. Dictation and transcription pick from the same installed models, including Whisper.net (ggml) models on the GPU (CUDA or Vulkan); one loaded copy of a model is shared by both.
- NPU models carry over. On Snapdragon (Windows ARM64) PCs the ARM64 installer includes the Qualcomm QNN runtime, and Settings and first-run setup offer the Qualcomm AI Hub Whisper Small download and Moonshine on the NPU, with a saved 6.0.0 Qualcomm choice picked up as it was. On Intel PCs the Whisper.net device list now has the NPU (OpenVINO) choice, the large-v3 download fetches Intel's OpenVINO bundle, and "Auto" means the GPU, else the NPU when the model has its OpenVINO files, else the CPU. Choices the PC cannot run are not listed, and a saved choice that does not fit the PC falls back to what does.
- Voice shell commands run again in dictation (never in transcription). Commands from 6.0.0 keep their on/off state; new ones start off.
- Text delivery as in 6.0.0 on Windows: the final text goes straight into the focused edit control when there is one (keystrokes otherwise, never the clipboard), "return to the starting window" inserts into that window without bringing it forward unless coding-mode Enter is on, the Windows Mouse Sonar pulse marks start and stop, and "Request exclusive microphone access while dictating" is back (shared if the microphone refuses).
- Updates: "Check for updates..." in the tray menu, plus an automatic check at most once a day that you can turn off in Settings (the 6.0.0 choice and last-check time carry over). Nothing installs without your OK, the MSI is verified against its published SHA-256 first, and PrimeDictate closes the normal way (an active meeting recording is saved) before Windows Installer starts.
- The overlay has its 6.0.0 controls back: pin (keep it on screen), copy (the last transcript, only when you click it), settings, collapse and expand, the elapsed time, the "Local only" badge and the model in its header, and the compact microphone has its ripple animation and a Settings option keeps it on screen while idle, as 6.0.0 did (by default it shows only while dictating).
- The tray icon: single or double click (or neither) opens PrimeDictate, as chosen in Settings; it shows "needs attention" for 10 seconds after an error and when the wake word could not start, and its tooltip names the model and the wake phrase.
- Settings: start at sign-in for just you or for everyone on this PC (asks for administrator permission, as in 6.0.0; the `--enable-launch-at-login` and `--disable-launch-at-login` command-line switches work again), the typing speed that time saved is compared with (also in the stats window), a color scheme (dark by default, as in 6.0.0, or light, or follow the system), and the tray click choice.
- History window: filters for typed or not typed, app and window, "Clear filters", and "Copy details"; the stats window shows the achievements and daily word counts as before.
- The wake word listens with a small model of the same kind as your dictation model (Whisper.net tiny or base, Moonshine tiny or base, or a small Whisper), as in 6.0.0, and falls back to the dictation model.

## Not in 6.1.0 yet

Nothing from the 6.0.0 app is left out.

The old WPF app stays in the repository and still builds; it is just not shipped.

# PrimeDictate 6.0.0

## Highlights

- Runs on .NET 10. The x64 and ARM64 installers stay self-contained and signed, so nothing extra needs installing.
- Wake word: "ok" and "okay" are treated the same, and "thanks" / "thank you" variants are recognized.
- Voice commands: more spoken variants of a command phrase are removed from the typed text.
- New particle visualizer in the overlay and a redrawn tray icon.
- `--show` and `--workspace` open the workspace window.
- Under the hood the app now builds on the portable PrimeDictate core, shared with the upcoming cross-platform app. That app is in the repository but not in these installers yet.

# PrimeDictate 5.1.0

## Highlights

- Added optional wake-word dictation start.
- Polished the dark UI shells.

# PrimeDictate 5.0.0

## Highlights

- Added the new onboarding flow for first-run setup.
- Hardened silence auto-commit so brief probe misses do not stop an active dictation session as easily.

# PrimeDictate 4.4.4

## Highlights

- Fixed update downloads so the temporary MSI file is closed before being moved into place on Windows.
- Made update handoff files unique per attempt so stale PowerShell or Windows Installer processes cannot lock the next update attempt.
- If an update install attempt fails, PrimeDictate clears the last-check timestamp so automatic update checks can retry after the next launch.

# PrimeDictate 4.4.3

## Highlights

- Fixed update handoff so major-upgrade MSIs install normally instead of using repair/reinstall flags that can leave self-contained runtime files unregistered.
- Added installer build validation for self-contained .NET runtime files in the publish output and MSI payload.
- Added a configurable start / stop dictation voice phrase. The default is `thank you`, and the phrase is removed before final text injection.

# PrimeDictate 4.4.0

## Highlights

- Added native ARM64 publish and online MSI packaging for Copilot+ PCs.
- Updated GitHub Actions release publishing to build x64 and ARM64 MSIs and attach both to tagged releases.
- Updated Chocolatey packaging to download the correct GitHub Release MSI for the machine architecture and verify it with SHA256 before install.

# PrimeDictate 4.1.0

## Highlights

- Added launch-at-login support. MSI installs enable it by default, silent installs can opt out with `LAUNCHATLOGIN=0`, Chocolatey supports `/NoLaunchAtLogin`, and the app exposes `--enable-launch-at-login` / `--disable-launch-at-login` switches.
- Added a local-first Impact tab in Settings with words typed, estimated net time saved, average speaking WPM, 14-day word bars, and milestone achievements.
- Added local achievement notifications for dictation word-count milestones.
- Reduced live-preview CPU work by using the recorder's RMS signal for silence timing instead of repeatedly resampling snapshots just to detect speech.
- Kept the refactor scoped: final-only text injection, overlay-only live preview, model discovery, and Whisper disposal behavior are unchanged.
