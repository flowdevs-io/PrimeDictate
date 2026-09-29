# Stage 3: risk spikes and decisions

Date: 2026-09-29. Environment: Linux x64 container (Ubuntu 24.04, .NET SDK 10.0.112), no GPU,
no physical audio device, and outbound access to Hugging Face blocked by the environment's network
policy. Everything below says where it ran; nothing here was run on Windows or macOS.

## Decisions

| # | Decision | Status | Basis |
|---|----------|--------|-------|
| D1 | Cross-platform shell: **Avalonia 12.1.3**. WPF app stays the shipping Windows shell until parity. | Adopted | Builds and renders on Linux x64 (X11); self-contained publish produces correct natives for win-x64, win-arm64, osx-arm64, linux-x64. |
| D2 | macOS/Linux capture and playback: **SoundFlow 1.4.1 (MIT) over miniaudio**. Windows dictation keeps NAudio/WASAPI. | Adopted for macOS/Linux; not yet for Windows transcription | Captures real audio through PulseAudio here; ships natives for all four targets. PortAudioSharp2 rejected: no win-arm64 native. |
| D3 | **Pause closes the capture device; Resume reopens it.** | Adopted | Measured: Stop/Start leaks paused audio on PulseAudio (see A3). |
| D4 | Existing sherpa-onnx engines are portable candidates for macOS/Linux. | Verified on Linux x64 only | Whisper ONNX (tiny.en) transcribes correctly on Linux x64 CPU with the app's settings. |
| D5 | Nemotron via **NeMo-Speech.cpp `nemo-speech serve`** as an app-managed child process, pinned to a reviewed commit and pinned GGUF artifacts. | Provisional: protocol verified from source, inference not yet run | Builds on Linux x64 CPU; model files could not be downloaded here. |
| D6 | The app passes **only absolute, checksum-verified local GGUF paths** to the worker, never indexed model names. | Adopted | Indexed names trigger a download through `curl` (see N3). |
| D7 | Live Nemotron segments get **client-assigned utterance IDs**; partial text is display-only and the final `transcript` is authoritative. | Adopted | Realtime events carry no item ID, and a delta can be a suffix or a full replacement that the client cannot tell apart (see N5). |

## A. Audio capture (SoundFlow/miniaudio), Linux x64, PulseAudio 16.1

Setup: PulseAudio in the container with a null sink. A 30 s "clock" WAV plays into the sink; its
tone is 300 + 100 × (second mod 15) Hz, so captured content can be matched to wall-clock time.
Capture goes through `MiniAudioCaptureSource` (`src/PrimeDictate.Platforms`) at 48 kHz mono float.

- **A1. Real-time capture works.** 4.02 s captured in 4.0 s wall; no sequence gaps; a 440 Hz
  source measured 440.5 Hz after the 48 to 16 kHz resampler.
- **A2. Open latency is about 2 s** before the first frame in this container, mostly miniaudio
  probing JACK and ALSA. Requesting a 20 ms period did not change it. This needs measuring on real
  macOS and desktop Linux before any latency claim.
- **A3. Stop/Start leaks paused audio.** With the device stopped for 3 s, about 3 s of audio from
  the paused period (the 700 to 976 Hz tones) arrived in one burst on Resume. That violates "Pause
  records nothing".
- **A4. Close/reopen does not leak.** No frames arrived while paused, and the first frames after
  Resume carried the current tone. The cost is about 1.2 s after Resume before audio flows, so the
  UI must show "Resuming…" until the first frame arrives instead of implying it is recording.
- **A5. Device naming.** With the PulseAudio backend, enumeration returned 11 ALSA-style PCM names
  ("Playback/recording through the PulseAudio sound server", "Discard all samples…"). miniaudio's
  native device IDs are pointers, not stable IDs. The adapter uses names as IDs for now; the picker
  needs filtering and a stable-ID strategy before release.
- **A6. No audio backend** (ALSA only, no device) throws from the engine constructor; the adapter
  wraps it as `AudioSourceException` so the UI can say the microphone is unavailable while import
  keeps working.
- Not tested: macOS CoreAudio and microphone permission prompts, PipeWire, Wayland sessions,
  device unplug and default-device change, Windows through miniaudio.

## B. Existing engines on Linux x64

`sherpa-onnx-whisper-tiny.en` (from sherpa-onnx GitHub releases), CPU, with the same recognizer
settings as `WhisperOnnxTranscriptionEngine`:

| Path | Audio | Result |
|------|-------|--------|
| File: `jfk.wav` (16 kHz PCM16) through `AudioFrame` | 11.0 s | Correct text; decode 1.00 s (RTF 0.09); model load 0.5 s |
| Live: same WAV played into the null sink, captured at 48 kHz, resampled to 16 kHz | 16.5 s captured | Correct text; decode 1.26 s |

sherpa-onnx Whisper returned no timestamps, so these segments map to `ApproximateChunk` timing, as
designed. `org.k2fsa.sherpa.onnx` 1.13.0 declares runtime packages for win-x64, win-arm64,
osx-arm64, osx-x64, linux-x64, and linux-arm64. Parakeet, Moonshine, and Whisper.net were not run
here.

## C. Avalonia shell

`src/PrimeDictate.Desktop` is a minimal shell with Transcription and Dictation tabs. It states the
mode boundary, says microphone capture does not include remote call participants, disables global
dictation where it isn't implemented, and probes the audio backend.

- Rendered under Xvfb (X11) on Linux x64, both from `dotnet run` and from the self-contained
  `linux-x64` publish.
- Self-contained publishes: win-x64 210 MB, win-arm64 219 MB, osx-arm64 117 MB, linux-x64 107 MB.
  About 105 MB of the Windows size is SkiaSharp and HarfBuzz native `.pdb` files, which packaging
  should exclude. Each output contains matching-architecture `miniaudio`, `SkiaSharp`, `HarfBuzz`,
  and `e_sqlite3` natives (checked with `file`).
- Not tested: Wayland, macOS app bundle, notarization, Windows rendering, tray integration.

## N. NeMo-Speech.cpp (native Nemotron runtime)

Pinned commit: `0f706e43cf1fbc031bad1423e05460d3acaeaa1c` (VERSION 0.1.0), Apache-2.0 runtime code
with third-party notices. Built with `scripts/configure.sh cpu-server` and
`cmake --build --preset cpu-server` on Linux x64 (Ubuntu CMake 3.28, `libsentencepiece-dev`); the
binary reports `nemo-speech 0.1.0`.

Artifacts pinned by the runtime's `models/index.json` at that commit (not downloaded here):

| Model | HF revision | File | Size | SHA-256 |
|-------|-------------|------|------|---------|
| nvidia/nemotron-3.5-asr-streaming-0.6b | `1c8deaecc64b91f034d73e08dd8b64625eb3395d` | `nemotron-3.5-asr-streaming-0.6b.q8_0.gguf` | 741,548,352 | `a5c435f2…f429ae` |
| nvidia/nemotron-speech-streaming-en-0.6b | `ebe59e5a817142986528bbbee5dba8db7b38ed50` | `nemotron-speech-streaming-en-0.6b.q8_0.gguf` | 699,872,960 | `d9a01898…3812d` |
| nvidia/Nemotron-3-Diarization | `f667ed73aee57d40cc39428eb768b4fd87a0a29e` | `Nemotron-3-Diarization.q8_0.gguf` | 107,012,128 | `08456d9e…c7a3a1` |

Findings from running the binary and reading the pinned source (`server/http/http_server.cpp`):

- **N1. Startup failures are fast and distinct.** No model: exit 1. Missing file: exit 3 with a
  validation message. Corrupt GGUF: exit 1 ("invalid magic"). Download failure: exit 2.
- **N2. Security settings exist and match the brief.** Default bind is `127.0.0.1`; `--no-ui`
  disables the playground; `NEMO_SPEECH_HTTP_API_KEY` supplies the bearer key without a command-line
  argument; CORS is off unless `--cors-origin` is set. `/health`, `/ready`, and `/version` are
  unauthenticated by design, and the `/v1` routes and the realtime socket require the key.
- **N3. Indexed model names download silently.** `--asr-model nemotron-3.5` started a 707 MiB
  download through `curl` into `~/.cache`. PrimeDictate must pass only local paths it has verified
  (D6), so an offline session never triggers a pull.
- **N4. One loaded ASR model per process.** The HTTP `model` field is ignored, as documented.
  Switching models means restarting the worker with another path and checking `session.created`'s
  `session.model` and `/v1/models`.
- **N5. The realtime protocol differs from what the docs imply.**
  - Transcription events have no `item_id`; only `type`, `delta` or `transcript`,
    `audio_processed` (seconds), optional `words`, and `event_id`.
  - `delta` is the new suffix when the partial extends the previous one, but the **whole partial**
    when the recognizer revised it. Clients cannot tell which, and appending deltas (as the bundled
    playground does) can duplicate text. PrimeDictate will treat partials as display-only and the
    final `transcript` as authoritative (D7). Upstream issue to file: include the full partial on
    delta events.
  - `input_audio_buffer.commit` finalizes and **resets the stream**, so word times after a commit
    restart at 0 unless the server offsets them. Needs a model to confirm; the client must add the
    session offset either way.
  - `session.update` is rejected once audio has started, and `max_speaker_count` is parsed and
    validated despite being documented as ignored.
- **N6. Limits.** The 512 MiB upload limit also caps cumulative audio per realtime stream (about
  4.6 h of 16 kHz PCM16) and resets on commit. WebSocket reads time out after 300 s (cpp-httplib
  default; the 30 s `--read-timeout` applies to HTTP), with server pings every 30 s. A Pause that
  commits and stops sending audio needs a test to see whether pongs keep the socket open past 300 s.
  Otherwise reconnect on Resume with an explicit offset.
- **N7. Diarizer capacity.** The runtime docs list Nemotron 3 Diarization as Sortformer V3 with up to
  8 speakers (Streaming Sortformer v2: 4).
- **N8. Languages.** The runtime docs say "40+ language-locales" for Nemotron 3.5 and allow `auto`.
  The model card separates supported locales from adaptation-ready ones, so the picker must use the
  card's supported list, not the runtime's.

## N-local. Nemotron spike run on Windows (CPU)

Run on 2026-09-29 on the maintainer's PC: AMD Ryzen 7 9800X3D (8C/16T), 61 GB RAM, Windows 11 Pro
10.0.26200. Runtime built at the pinned commit `0f706e43` with
`scripts\windows\build.ps1 -Backend cpu -Http -AsrOnly` (MSVC from VS 18, Ninja, vcpkg bootstrapped
under `%LOCALAPPDATA%\NeMoSpeech`). `nemo-speech --version` reports 0.1.0. Models were fetched
directly from the pinned Hugging Face revisions; all three files matched the sizes and SHA-256 values
in the table above.

**Backend that ran: CPU only** (`[asr] ... backend=CPU`; `/ready` reports `device: auto`). CUDA was
not attempted: the RTX 5070 (Blackwell) needs CUDA 12.8 or newer and 12.3 is installed.

**Vulkan failed on both GPUs.** With the Vulkan SDK 1.4.357.0 installed, the same runtime commit
built (`build.ps1 -Backend vulkan -Http -AsrOnly`, 409 build steps). `serve --device vulkan:0` (RTX 5070)
and `vulkan:1` (AMD Radeon iGPU) both print `backend=Vulkan0/1`, then the process aborts during model
load with `ggml-cpu.c:1270: GGML_ASSERT(ne3 == ne13) failed`, with or without the diarization model.
`--device cpu` from the same Vulkan binary loads and transcribes jfk correctly, so the fault is in
the Vulkan path (an op falling back to the CPU backend with an unexpected shape), not the build. Upstream
report needed; until then treat GPU acceleration on Windows as unavailable for this pinned commit.

The worker was started with local paths only, `--no-ui`, `127.0.0.1:18080`, and the key from
`NEMO_SPEECH_HTTP_API_KEY`.

| Check | Result |
|-------|--------|
| Cold start to `/ready` | 7.1 s (ASR + diarization); 3.1 s for the English-only model |
| `/v1/models` | ASR id is `.nemotron-3.5-asr-streaming-0.6b.q8_0.gguf` (note the leading dot) plus a `diarization` entry; `device: auto` |
| File, `jfk.wav` (11 s), `verbose_json` | Exact wording ("...ask what you can do for your country."), 22 words with start, end and confidence, last word "country." at 10.24 to 10.4 s; 1.7 s wall |
| File with speakers, AMI EN2002d 60 s | Speaker tags 1, 2, 3 (63, 48 and 15 words), matching the fixture README's three main speakers; 33.3 s wall (RTF about 0.55 with diarization) |
| Live socket, jfk at real time | Order: `session.created`, `session.updated`, many `delta`, then `completed` (transcript plus words) and `input_audio_buffer.committed`. First partial about 0.3 s after the word's onset; final about 1 s after `commit`. Last word is present in the final. |
| Empty deltas | Most `delta` events carry `""` (one per processed frame). Clients must drop them. |
| Word times after commit | Restart at 0 for the next utterance (confirmed with a second 1 s send). |
| Live vs offline text | The live final had no trailing period ("...your country"); the file endpoint added it. Treat final punctuation as unreliable. |
| Delta revisions | None observed in these clips; every delta was a suffix. Revision behavior is still unconfirmed. |
| Pause | Socket stayed open through 60 s of silence, and audio resumed on the same socket after a commit (also with a 6 s gap). It also survived 330 s (past the 300 s read timeout): the client received nothing, sent 2 s of audio and got deltas and a `completed` within about 1.2 s on the same socket, so server pings keep it alive and Pause does not need a reconnect. |
| Model identity | Restarting with `nemotron-speech-streaming-en-0.6b` changed the `/v1/models` id; its jfk text was "And so, my fellow Americans..." |
| Upload limit | `--max-upload-mb 1` with a 1.9 MB wav returns HTTP 413 with a generic body (`"HTTP request failed"`), no size in the message |
| Peak memory | 2.56 GB working set for the ASR + diarization worker |

Caveat: one early script variant saw no events after resuming from a pause. It did not reproduce
with a corrected script across three configurations (first 11 s or 1 s, gap 6 s, second 1 s or 5 s), so it is treated as
a test-script error; a resumed send of the full 11 s clip was not re-run.

### Nemotron blockers

- **Model files.** `huggingface.co` is denied by this environment's network policy, so neither
  GGUF could be downloaded and no Nemotron inference, streaming, flush, timing, or speaker tagging
  has run. It can proceed either by allowing `huggingface.co` and `cdn-lfs.huggingface.co` in the
  environment's network settings, or by running the spike on a machine that can download them.
- **Accelerated builds.** Only the CPU preset was built. CUDA, Metal, and Vulkan builds and the
  Windows build driver are untested.
- **Release binaries.** GitHub release metadata for NeMo-Speech.cpp could not be read from this
  environment, so it is unknown whether signed release archives exist for every target. The app will
  not run upstream install scripts on users' machines.

## Next

Stage 4 (file transcription) can start on the verified path: Core, the resampler, the existing
sherpa engines, and SoundFlow playback. Nemotron work (stage 6) waits on the model-file blocker
above.
