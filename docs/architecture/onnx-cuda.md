# ONNX speech models on CUDA (Whisper, Parakeet)

The sherpa-onnx models run on the CPU by default. On an NVIDIA GPU they can run on CUDA. Nemotron has its own worker and its own
`PRIMEDICTATE_NEMO_DEVICE` setting; this page is about the ONNX models used for dictation and the meeting live draft.

## Setting

`Settings > Speech model device`: **Auto** (CUDA when everything is ready, else CPU), **CPU**, **CUDA**. Environment override:
`PRIMEDICTATE_ONNX_DEVICE=auto|cpu|cuda`. It applies at the next start because ONNX Runtime is one native library per process.
If CUDA is wanted but cannot be used, the app runs on the CPU and shows one notice naming what is missing.
No wrong setup can crash a recognizer: every requirement is checked before CUDA is selected.

## What is needed on the PC

1. **ONNX Runtime GPU build, CUDA 13** (`Microsoft.ML.OnnxRuntime.Gpu.Windows` 1.30.0, from nuget.org). The sherpa-onnx package bundles
   a CPU-only ONNX Runtime 1.24.4; the GPU build is a drop-in for it. Three DLLs go in
   `%LocalAppData%\PrimeDictate\gpu\onnxruntime-cuda13`. `scripts/Install-OnnxGpuRuntime.ps1` does this (download plus SHA-256 check, about 170 MB).
   The sherpa-onnx project's own `win-x64-cuda` archive is not usable: it ships ONNX Runtime 1.17.1 for CUDA 11.8 and cuDNN 8, which predates Blackwell.
2. **CUDA 13 runtime**: already present as CUDA Toolkit 13.4 (the folder `...\CUDA\v13.4\bin\x64` with `cudart64_13.dll`, `cublas64_13.dll`,
   `cublasLt64_13.dll`, `cufft64_12.dll`). CUDA 12.3 is not used: ONNX Runtime 1.27+ is built for CUDA 13, and 12.3 cannot drive an RTX 5070 (sm_120) anyway.
3. **cuDNN 9 for CUDA 13**: NOT part of the CUDA Toolkit, and the only new NVIDIA install. Installer or zip from NVIDIA's cuDNN downloads
   (Windows x86_64, cuDNN 9.x, CUDA 13 build). The app finds it in `C:\Program Files\NVIDIA\CUDNN\v9.x\bin\13.x`, via `CUDNN_PATH`, on `PATH`, or next to the GPU DLLs.

Search order for the CUDA and cuDNN DLLs: the GPU pack folder, `PRIMEDICTATE_CUDA_BIN`, `CUDA_PATH_V13_*` and `CUDA_PATH` (`bin\x64`, `bin`), cuDNN folders, `PATH`.
The pack folder can be moved with `PRIMEDICTATE_ORT_GPU_DIR`.

## What runs on the GPU

| Model | On CUDA |
|---|---|
| Whisper | Yes, with the full-precision `*-encoder.onnx` / `*-decoder.onnx` from the same download (CUDA has no kernels for most int8 operators, so the int8 files would run mostly on the CPU with copies in between). CPU keeps using int8. Models without the full-precision pair stay on the CPU. |
| Parakeet | Only the new **Parakeet TDT 0.6B v2 (fp16, for the GPU)** download (1.1 GB, English). The int8 Parakeet v2/v3 stay on the CPU because no fp16/fp32 v3 archive exists on the sherpa-onnx release. |
| Moonshine | CPU only (int8 and ORT-format files). It is small and fast there. |

## Not verified

Everything here is checked on Linux only: the decision logic (fake GPU probe), file discovery and the fallback notes have unit tests.
Not run anywhere yet: loading the GPU DLLs, a recognizer on CUDA, Blackwell kernels, speed. ONNX Runtime 1.30 replacing sherpa-onnx's 1.24.4 build is a supported drop-in in
principle (ONNX Runtime keeps its C API backward compatible) but is untested with this sherpa-onnx build.
