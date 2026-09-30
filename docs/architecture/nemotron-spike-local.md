# Nemotron spike: steps to run on a machine that can reach Hugging Face

Stage 3 could not download the Nemotron models (see `stage-3-spikes.md`, section N). These steps
finish the spike. Record results by adding a section to `stage-3-spikes.md`.

Pinned runtime: NVIDIA/NeMo-Speech.cpp commit `0f706e43cf1fbc031bad1423e05460d3acaeaa1c`.

## 1. Build the runtime at the pinned commit

```bash
git clone https://github.com/NVIDIA/NeMo-Speech.cpp && cd NeMo-Speech.cpp
git checkout 0f706e43cf1fbc031bad1423e05460d3acaeaa1c
git submodule update --init ggml third_party/cpp-httplib llama.cpp
scripts/configure.sh cpu-server          # or metal-server / vulkan-server / cuda-server
cmake --build --preset cpu-server
```

On Windows use the build driver described in the runtime's `docs/build.md`.

## 2. Download the pinned GGUF files and verify them

Sizes and SHA-256 come from the runtime's `models/index.json` at the pinned commit:

| File | Size (bytes) | SHA-256 |
|------|--------------|---------|
| `nemotron-3.5-asr-streaming-0.6b.q8_0.gguf` | 741548352 | `a5c435f294eea8f88ce68dd27b8c3bfea7f777cb2fbba04fcd30eaa555f429ae` |
| `Nemotron-3-Diarization.q8_0.gguf` | 107012128 | `08456d9e22cd9a323c0364d98375f3746d6e68507ebb705cd46438c534c7a3a1` |
| `nemotron-speech-streaming-en-0.6b.q8_0.gguf` (optional) | 699872960 | `d9a01898d2a611c8764e23a1c2f45e70bbd5a425dc4de93692ac951dd603812d` |

`nemo-speech pull nemotron-3.5` downloads and verifies them. Afterwards always pass local file
paths to `serve`, never model names, because names trigger downloads.

## 3. Start the worker the way the app will

```bash
export NEMO_SPEECH_HTTP_API_KEY="$(openssl rand -hex 24)"
nemo-speech serve --no-ui --host 127.0.0.1 --port 18080 \
  --asr-model /path/nemotron-3.5-asr-streaming-0.6b.q8_0.gguf \
  --diar-model /path/Nemotron-3-Diarization.q8_0.gguf
```

## 4. Checks to run and record

1. `GET /ready` and `GET /v1/models` show the ASR and diarization models and the effective device.
2. File: POST a 16 kHz mono WAV with known wording to `/v1/audio/transcriptions` with
   `response_format=verbose_json`; record word timings and the last word.
3. File with speakers: same, with `diarization=true`, on a two-speaker clip; record speaker tags.
4. Live: connect to `/v1/audio/transcriptions/realtime` with the bearer key, send `session.update`
   (`sample_rate: 16000`, `word_timestamps: true`, `speaker_diarization: true`), stream PCM16 in
   about 100 ms frames, then `input_audio_buffer.commit`. Record the event sequence, whether
   `delta` is a suffix or a full replacement after a revision, whether the final word is present,
   and whether word times restart at 0 after commit.
5. Pause: stop sending audio for 60 s and then for more than 300 s. Record whether the socket
   survives and whether audio can resume without reconnecting.
6. Model identity: restart with the English-only model and confirm `session.model` changes.
7. Long input: a stream near the 512 MiB cumulative limit is not needed; record the limit message
   from a small `--max-upload-mb 1` run instead.
8. Report the effective backend (CPU, Metal, CUDA, Vulkan), cold load time, first partial latency,
   real-time factor, and peak memory, with the hardware named.
