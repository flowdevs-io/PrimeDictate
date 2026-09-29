# Stage 2: shared foundations

Adds `src/PrimeDictate.Core` (`net10.0`, no WPF, Windows Forms, WASAPI, or registry
dependencies) and wires the existing Windows app to it without changing dictation behavior.

## What is in Core

| Area | Types | Notes |
|------|-------|-------|
| Audio | `AudioFormat`, `AudioFrame`, `AudioConversion`, `StreamingResampler`, `RecordedAudioTimeline` | Frames always own their samples (copied out of callback/pooled memory). Offsets are 64-bit per-channel sample counts, never UI time. The resampler is a windowed-sinc polyphase filter whose output does not depend on chunk boundaries and maps offsets back to the source exactly. The timeline maps the continuous recorded-audio clock to wall-clock capture intervals, so Pause records nothing and Resume continues the offset. |
| Transcript model | `TranscriptDocument`, `TranscriptSegment`, `WordTiming`, `SpeakerAttribution`, `TranscriptSpeaker`, `RecognitionRunInfo`, `MediaMetadata`, `AudioReference` | Schema-versioned. Raw recognizer text and user edits are separate fields. Timing carries provenance (`Model`, `Alignment`, `ApproximateChunk`). Confidence and word timings are nullable. Each rerun gets a new `ResultVersion`; older results and their edits are kept. Speakers are an ID-to-name map. |
| Events | `SessionStarted`, `SegmentUpserted`, `SegmentFinalized`, `SpeakerUpdated`, `ProgressChanged`, `SessionCompleted`, `SessionFailed` | App-owned names. `TranscriptDocumentReducer` applies them idempotently by segment ID and revision; stale or duplicate updates are ignored, and provider updates never overwrite user edits or speaker names. |
| Sessions | `TranscriptionSessionStateMachine`, `TranscriptionSessionOptions`, `ITranscriptionSessionStore` | Options are an immutable snapshot taken at session start. Pause, Finalizing, Interrupted, and rerun-after-Completed are explicit states. |
| Provider contracts | `IAudioSource`/`IAudioCaptureLease`, `IAudioDecoder`, `IAudioPlayback`, `ITranscriptionProvider`, `IStreamingRecognitionSession`, `IDiarizationProvider` | Capabilities distinguish native streaming from buffered windows, report timing support, and report the effective runtime/backend separately from the requested one. |
| Models | `ModelDescriptor`, `ModelStatus`, `ModelAvailability`, `IModelRegistry` | Speech models and speaker-detection models are different `ModelKind`s. Availability separates Installed (files present) from Ready (a probe actually ran). |
| Coordination | `MicrophoneCoordinator`, `ModelLeaseScheduler` | One microphone owner at a time. Acquiring suspends active background consumers (wake word, idle dictation) and releasing resumes exactly those; a busy dictation blocks acquisition instead of being cut off. Model leases serialize non-thread-safe engines per model and grant live work ahead of queued file windows. |
| Persistence | `SqliteTranscriptionSessionStore`, `AppDataPaths`, `TranscriptionPreferencesStore` | See below. |

## Persistence decisions

- **SQLite metadata, media on disk.** `%LocalAppData%\PrimeDictate\transcription\sessions.db`
  (Windows; `~/Library/Application Support/PrimeDictate` on macOS, `~/.local/share/PrimeDictate`
  on Linux). WAL journal, `synchronous=FULL`, schema version in `PRAGMA user_version`. A database
  from a newer schema is refused and left untouched.
- **Checkpoints hold final content only.** Provisional segments are never written; after a crash
  they are recomputed from retained audio. On startup, sessions left Running/Paused/Finalizing are
  marked Interrupted and offered for recovery.
- **Deletion boundaries.** Deleting a session removes its rows, its media directory, and owned audio
  that lies under the media root. Referenced originals are never deleted, and an "owned" path
  outside the media root is refused rather than deleted. `DeleteOwnedAudioAsync` implements
  transcript-only retention.
- **Permissions.** On macOS/Linux the data directories are created `0700` and the database and
  settings files `0600`. On Windows they inherit the per-user `LocalAppData` ACL. No encryption at
  rest is claimed.
- **Separate transcription settings.** `transcription-settings.json` sits next to dictation's
  `settings.json`, which transcription mode only reads (to seed first-run defaults via
  `TranscriptionPreferencesSeed`) and never rewrites. Migrations are versioned and idempotent, back
  up the file before rewriting it, keep unreadable files as a backup, and load files from a newer
  version read-only.
- Dictation history (`TranscriptionHistoryStore`) is unchanged and remains separate from sessions.

## Changes to the Windows app

- `PrimeDictate.csproj` references Core and excludes `src/**` and `tests/**` from its default globs
  (the project sits at the repository root).
- `IStructuredTranscriptionEngine` and `TranscriptionEngineHost.TranscribeSegmentsAsync` add timed
  results beside the unchanged string path. Whisper.net returns its native segment timestamps
  (`TimingProvenance.Model`); its token probabilities are not exposed as confidence. Other engines
  are wrapped as one `ApproximateChunk` segment. Dictation still calls `TranscribeAsync`, whose
  output is built exactly as before.
- `LegacyModelIds` gives the existing backends stable IDs (`whisper-onnx:<id>`, `parakeet-onnx:`,
  `moonshine:`, `whisper-net:`, `qualcomm-qnn:`) for the model registry to adopt.

## Verification

Run on Linux x64, .NET SDK 10.0.112:

- `dotnet test tests/PrimeDictate.Core.Tests`: 51 tests pass. They cover resampler length,
  chunk-invariance, passband gain, alias rejection, and impulse timing; frame ownership; PCM16 round
  trip; pause/resume offsets and wall-clock mapping; reducer idempotency, stale revisions, edit
  preservation, speaker renaming, rerun versions, repeated legitimate phrases, and invalid
  intervals; state transitions; microphone suspend/restore/rollback; model-lease priority and
  cancellation; SQLite round trip (Unicode, words, speakers, media), partials excluded, stale
  revisions, interruption marking, deletion boundaries, transcript-only retention, paging, newer
  schema refusal; and preferences seeding, migration and backup, newer-version read-only, and
  corrupt-file recovery.
- Solution build (`EnableWindowsTargeting`): 0 warnings. Self-contained `win-x64`/`win-arm64`
  publish includes `PrimeDictate.Core.dll` and the matching native `e_sqlite3.dll`.

Not verified: anything executing on Windows, macOS, or on a Linux desktop. The structured Whisper.net
path compiles but has not run. No UI uses Core yet, and neither the model registry implementation
nor capture, decoder, or playback adapters exist yet (stages 3 to 5).
