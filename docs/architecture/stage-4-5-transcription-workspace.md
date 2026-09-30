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

## Stage 6 (first part): Nemotron worker and speakers on file import

Built against the real protocol samples in `docs/architecture/nemotron-samples/`. **Not run against the real worker from this environment** (no model download here); tests use those captured responses and a fake local HTTP server.

- `NemotronModelFiles` accepts only the pinned GGUF file names and exact byte sizes, so the worker never receives a model name and can never start a download.
- `NemotronWorker` starts `nemo-speech serve` with `--no-ui`, `127.0.0.1`, a random port, `--device cpu`, and a random key in the environment (not the command line). Vulkan aborts at the pinned commit, so CPU is the only backend offered.
- `NemotronProvider` sends each window (at most 30 s, under the 1 MB upload limit) to the file endpoint with `verbose_json`. It asks for diarization only when the worker was started with the diarizer, because the worker answers HTTP 400 for file requests and breaks the realtime stream without one.
- Words become segments per speaker turn with model word timings. Detected speakers are registered as "Speaker 1", "Speaker 2" in order of appearance and can be renamed in the workspace.
- The model picker lists Nemotron when `nemo-speech` (in `models/nemotron`, next to the app, or `PRIMEDICTATE_NEMO_SPEECH`) and a pinned speech model are present in `%LocalAppData%\PrimeDictate\models\nemotron`. Add `Nemotron-3-Diarization.q8_0.gguf` there for speakers.

Limits:
- Speaker numbers are consistent within one 30 s window. The worker does not give speaker identity across windows, so a long file can relabel the same person. Renaming maps one id at a time.
- Live speakers need native streaming over the realtime socket (word times restart at 0 after each commit, and speaker labels appear only on final events). Buffered Live with Nemotron transcribes but does not label speakers yet.

## Stage 6 (second part): live Nemotron streaming with speakers

Live sessions whose provider reports native streaming (Nemotron) use the worker's realtime socket instead of re-recognizing windows. Written against the captured message samples; tested with a fake socket that replays those shapes. **Not yet run against the real worker.**

- Audio goes out as binary PCM16 (about 100 ms blocks). Silence of 1.2 s ends an utterance and sends a commit; a 30 s cap, Pause and Stop also commit.
- The worker returns final text, word times and speaker numbers only after a commit, and word times restart at 0 after each commit. The session adds the offset of the audio each commit covered, so lanes and lines sit on the recording timeline.
- Deltas (partial text) are shown as a gray provisional line with no speaker ("speaker pending"). They can be a suffix or a whole revised partial, so they are reconciled by prefix and always replaced by the final text. An utterance whose final is empty removes its provisional line.
- One speaker turn becomes one segment (`u3.0`, `u3.1`, ...), registered as "Speaker N" and drawn in the timeline.
- The speaker request is sent only when the diarizer was loaded (the worker otherwise errors on every audio frame).
- One model lease covers the whole live session, so file imports wait until it stops.
- If the socket drops, what was transcribed is kept and the session ends as failed-recoverable.

**Open question for the real worker:** whether speaker numbers stay the same across commits on one connection. If they restart at each commit, two different people can both be "speaker 1" in different utterances. The test steps ask for this to be checked; the fallback is committing much less often (only on long pauses and Pause/Stop).

## Meetings: one stream per channel (unverified against the real worker)

A stereo meeting (left = microphone, right = system audio) on a native-streaming provider opens two realtime
streams on the same worker: the microphone stream is undiarized and every line is speaker `local` ("You"); the
system-audio stream runs speaker detection for the remote people (or is labelled `remote` when no diarizer is
installed). Each stream has its own utterance detection and commits, both use the shared session clock, so a
line from each side can overlap in time and keeps its own row. If the second stream cannot be opened the session
falls back to one mixed stream and records a note. `LiveSessionOptions.SeparateMeetingChannels = false` forces the
mixed stream. Not yet checked: whether the real worker serves two concurrent realtime sockets, and how much GPU
memory a second session adds.

## Meetings: two passes (draft, then final)

With a fast model live (today the Whisper ONNX models; Parakeet and Moonshine are not ported yet) and Nemotron plus its
diarizer installed, a Meeting is recorded in two passes. This is a design note; it has been run only against a stand-in
recognizer and a stand-in diarizer on Linux, not on Windows with the real worker.

1. **Live draft.** The chosen model transcribes mixed audio in windows as before. The recording is saved as stereo
   (left microphone, right system audio). The status line and the recording indicator say the text is a draft.
2. **Final pass after Stop** (`MeetingFinalPass`, checkbox "Meetings: speaker labels after Stop"):
   - `nemo-speech diarize` runs over the system channel (mono `.wav`, `--model`, `--device`, `--format json`), giving
     speaker segments that are consistent across the whole meeting and may overlap.
   - When the diarizer returned turns, the system channel is cut at those turns, not at silences: each speaker's talking
     (gaps under 1 s joined, 0.25 s padding, at most one recognizer window) is sent on its own, so its words carry that
     speaker and time. Where two turns overlap they hear the same audio; a duplicate word is hidden on the speaker the
     diarizer covers less. Without diarizer output the channel falls back to speech chunks as below.
   - Each channel is cut into speech chunks and sent to Nemotron's file API without its own diarization (per-window
     diarization numbers speakers from 1 in every request, so it cannot keep a person the same across windows).
   - Microphone lines are "You". Each system word takes the diarizer speaker covering most of it; one-word flickers are
     smoothed and lines are cut where the speaker changes. Echo removal is the same as in the live view.
   - Nothing is written until everything succeeded. The rows become a new result version and replace the draft; the draft
     stays stored as the earlier result. The overlap segments are saved as `system-diarization.json` and drive the timeline.
   - The session notes say when the pass started and what it produced or why it failed.

Live Nemotron (two streams) stays available; it is chosen by picking a Nemotron model, and it keeps the existing
after-Stop timeline redraw.
