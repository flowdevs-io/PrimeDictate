# Nemotron worker protocol samples (real captures)

Captured 2026-09-29 from `nemo-speech` 0.1.0 at commit `0f706e43cf1fbc031bad1423e05460d3acaeaa1c`,
built for CPU on Windows (Ryzen 7 9800X3D). Audio: first 12 s of the AMI EN2002d fixture
(`test_files/diar/ami_en2002d_2132.wav`), 16 kHz mono. The API key is never written to these files.

## Commands that worked

```powershell
$env:NEMO_SPEECH_HTTP_API_KEY = "<API_KEY>"     # random hex, sent as "Authorization: Bearer <API_KEY>"
nemo-speech serve --no-ui --host 127.0.0.1 --port 18080 --device cpu `
  --asr-model  <dir>\nemotron-3.5-asr-streaming-0.6b.q8_0.gguf `
  --diar-model <dir>\Nemotron-3-Diarization.q8_0.gguf

# without diarizer (samples 4):
nemo-speech serve --no-ui --host 127.0.0.1 --port 18081 --device cpu `
  --asr-model <dir>\nemotron-3.5-asr-streaming-0.6b.q8_0.gguf
```

Ready in about 7 s. Poll `GET /ready` (no auth). `--device vulkan:N` aborts on load at this commit
(see `stage-3-spikes.md`); use `cpu`. `--asr-model` and `--diar-model` are local file paths, never model names.

File request: `POST /v1/audio/transcriptions` multipart with `file`, `response_format=verbose_json`, `diarization=true`.
Realtime: `ws://127.0.0.1:PORT/v1/audio/transcriptions/realtime` with the bearer header; audio is sent as
binary PCM16 frames of about 100 ms.

## Files

- `file-verbose-json-diarization.json`: full file response; words carry `speaker` (integers starting at 1).
- `realtime-diarization-session.json`: `session.created`, the `session.update` sent, `session.updated`,
  a few deltas, and both `completed` events (one before and one after a commit). Speaker labels appear only
  on `words[]` inside `completed` events; delta events carry no speaker. Word times restart at 0 after the commit.
- `file-diarizer-not-loaded.txt`: HTTP 400 with a clear message.
- `realtime-diarizer-not-loaded.json`: `session.updated` accepts the request, then an `error` event follows every
  audio frame and nothing is transcribed. Clients should not request speakers unless the worker reported a diarizer.
