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

| Voice commands (commit / discard / history phrases, incl. "ok"/"okay" and "thanks"/"thank you" variants) | `VoiceCommandMatcher.cs`, `VoiceCommands.cs` | Ran: unit tests. Ported from Justin's committed version (b2e6dcc). Dictation only; transcription mode has no hook |
| Wake word (matching and idle-mic listener, yields the mic to dictation and to transcription sessions via `MicrophoneCoordinator`) | `WakeWord.cs`, `DictationHost.cs` | Ran: unit tests with a fake microphone. Not tried with a real microphone or model |
| Particle overlay (150 particles on a vector field, scrolling mirrored waveform) | `OverlayVisualizer.cs` (physics, tested), `VisualizerControl.cs` (drawing) | Physics ran; drawing not looked at |

| Ollama rewrite (loopback only unless the user allows a remote endpoint; failures type the raw text) | `OllamaRewriter.cs` | Ran: unit tests with a fake HTTP handler |
| Dictation history (SQLite, search, delete, clear; imports WPF `history.json` once, read-only) and window | `DictationHistory.cs`, `DictationHistoryWindow.cs` | Store ran: unit tests. Window not looked at |
| Audio cues (synthesized WAV; Windows `PlaySound`, macOS `afplay`, Linux `paplay`/`aplay`) | `AudioCues.cs`, `ProcessAudioCuePlayer.cs` | WAV generation ran; playback not heard on any OS |
| Tray icon: the WPF "voice wire" icon redrawn in Avalonia, five states | `TrayIconRenderer.cs` | Rendered under Xvfb and viewed (`--render-tray-icons <dir>`); not seen in a real tray |
| `--show` / `--workspace` (accepted; the window is the default start) and new `--background` (tray only, Windows and macOS) | `App.axaml.cs` | Not run on Windows |
| First-run check (mic, model, hotkey, typing guard, model choice) | `DictationOnboardingWindow.cs` | Starts under Xvfb without errors; not looked at |

## Rules kept from AGENTS.md

Final-only typing, no clipboard, no live retyping into the target, Enter only after a guarded successful commit, hook thread only raises an event and work is offloaded, one gate serializes toggle/commit/discard.

## Platform behavior

- **Windows**: full guard, restore-to-start-target, `SendInput` typing. Needs a real test.
- **macOS, Linux**: no foreground check exists yet, so dictation does **not** type unless the user turns on "Type even when the app cannot check which window is in front". This follows the parity plan. macOS also needs Accessibility permission for hotkeys; Wayland has no global hotkeys and the app says so.
- Closing the main window hides it to the tray on Windows and macOS; on Linux it quits (no guaranteed tray host).

## Not done yet

- Voice shell commands: matching is ported, running is not. Off until an explicit opt-in with a visible warning exists. Never in transcription mode.
- Stats and achievements, launch at login, updater, model download (the new app reads the same models folder but cannot fetch models yet), installers.
- Wake word uses a Tiny/Base Whisper model when installed, else the dictation model (as WPF does). Moonshine/Whisper.net wake models are not ported.
- The WPF app's focused-edit-control insertion (`WindowsFocusedTextControl`) and direct injection into the original target.
- Compact-mode ripple animation and the copy/pin buttons of the WPF overlay, macOS `NSPanel`, Linux X11 hints, saved overlay position.
- Parakeet and Moonshine backends: a WPF setting that selects one gets a notice and no model.
- First-run onboarding, launch at login, updater, packaging.
