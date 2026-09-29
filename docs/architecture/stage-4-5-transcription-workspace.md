# Stages 4 and 5: file and live transcription workspace

The Avalonia shell (`src/PrimeDictate.Desktop`) now has a working Transcription workspace on top of the Core pipeline. It uses an installed Whisper ONNX model (the same folders the dictation app uses) through sherpa-onnx.

## What it does

- **Import**: file picker or drag and drop. `.wav` files are read by a built-in decoder. Everything else (mp3, m4a, mp4, mkv, flac, ogg, and so on) needs `ffmpeg` and `ffprobe`. They are found in `PRIMEDICTATE_FFMPEG_DIR`, next to the app, or on `PATH`. Paths are passed as an argument list (never a shell) with a `file:` prefix and a file/pipe protocol whitelist.
- **Record**: microphone capture (miniaudio through SoundFlow), Pause, Resume, Stop, Discard. Recording captures your voice only, not other people on a call.
- **Buffered Live**: silence ends an utterance, never the session. The current utterance is re-recognized about every 1.5 s and shown grayed, then replaced in place by the final text. Audio is written to a recoverable WAV as it arrives.
- **Edit, search, copy, export**: edits are stored apart from the recognizer text. Export formats are text, Markdown, JSON, SRT and WebVTT. Copy puts text on the clipboard and never types it anywhere.
- **Sessions**: SQLite under `%LocalAppData%\PrimeDictate\transcription` (or the platform equivalent). Rerun with another model adds a new result version and keeps earlier results and edits. Deleting a session removes audio the app owns and never the original file.
- Transcription never types into other apps, matches voice commands, runs commands from transcript text, or logs transcript text.

## Verified on Linux (not on Windows)

- Core tests: 65 pass.
- mp3 (with a silent gap), mp4 and 8 kHz wav imports produced the expected transcripts, and edit, SRT export and reload worked.
- Live recording through a PulseAudio null sink: provisional then final lines, Pause/Resume, Stop reaching Completed, transcript persisted.
- The Avalonia window renders the workspace under X11 (screenshot from a saved session).
- `dotnet publish -r win-x64 --self-contained` succeeds and includes the sherpa-onnx, onnxruntime and miniaudio Windows natives. The result was not run on Windows.

## Known limits

- Segments are one per audio window (up to 28 s) with `ApproximateChunk` timing, so long speech comes out as long segments. Sentence splitting with real timestamps needs a model or aligner that emits them.
- No audio playback or click-to-seek yet.
- Whisper only. Nemotron, speaker detection and native streaming are stage 6.
- ffmpeg is not bundled.
- Recording level and elapsed time are shown as text, not a meter widget.
- No settings screen: the first installed model is preselected, audio is kept, and the default microphone is used.
