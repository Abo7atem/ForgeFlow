# ForgeFlow TensorRT runtime

ForgeFlow detects a real NVIDIA TensorRT execution provider at startup. The
TensorRT profile is enabled only when both `onnxruntime_providers_tensorrt.dll`
and a matching `nvinfer_11.dll`, `nvinfer_10.dll`, or `nvinfer.dll` (plus CUDA
dependencies) are available.

Place the matching NVIDIA TensorRT Windows x64 runtime under `Tools/tensorrt`
or set `FORGEFLOW_TENSORRT_PATH` to its installation directory. The application
then uses the dynamic NanoVSR model with TensorRT FP16, engine cache and timing
cache. If the provider is unavailable or incompatible, it falls back to
DirectML/CPU and records the reason in the processing log.

The current ForgeFlow build uses ONNX Runtime 1.24.4. Use the TensorRT/CUDA
combination supported by that ONNX Runtime build; the provider and native ORT
core must be built for the same ABI. Cached engines are device/model specific
and are regenerated automatically when the model or GPU changes.

NVIDIA TensorRT/CUDA DLLs are not copied from a developer machine into a public
customer release. Redistribution must follow NVIDIA's license and the exact
TensorRT, CUDA, cuDNN and ONNX Runtime compatibility matrix for the target GPU.
