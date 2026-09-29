# Stage 7: dictation in the new app, status

Branch `claude/dictation-parity-4k0tl9`, following `stage-7-parity-plan.md`. "Ran" means executed on Linux in a container; "Compiled only" means it builds but has not been run on that OS.

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

## Rules kept from AGENTS.md

Final-only typing, no clipboard, no live retyping into the target, Enter only after a guarded successful commit, hook thread only raises an event and work is offloaded, one gate serializes toggle/commit/discard.

## Platform behavior

- **Windows**: full guard, restore-to-start-target, `SendInput` typing. Needs a real test.
- **macOS, Linux**: no foreground check exists yet, so dictation does **not** type unless the user turns on "Type even when the app cannot check which window is in front". This follows the parity plan. macOS also needs Accessibility permission for hotkeys; Wayland has no global hotkeys and the app says so.
- Closing the main window hides it to the tray on Windows and macOS; on Linux it quits (no guaranteed tray host).

## Not done yet

- Wake word and voice commands: waiting for Justin's committed `WakeWordListener.cs` / `VoiceCommandMatcher.cs`. The controller already takes an `IVoiceCommandProcessor`; the default matches nothing. Transcription mode has no such hook.
- Voice shell commands, Ollama post-processing, dictation history, stats, audio cues.
- The WPF app's focused-edit-control insertion (`WindowsFocusedTextControl`) and direct injection into the original target.
- Overlay visuals (Justin's particle overlay is uncommitted), macOS `NSPanel`, Linux X11 hints, saved overlay position.
- Parakeet and Moonshine backends: a WPF setting that selects one gets a notice and no model.
- First-run onboarding, launch at login, updater, packaging.
