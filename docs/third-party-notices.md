# Third-party notices

The MIT license in the repository root covers original ForgeFlow code and owned
assets only. It does not replace the terms for bundled third-party software,
models, or runtime files. Keep the applicable notices when redistributing the
installer or its contents.

## Bundled components

| Component | Use in ForgeFlow | License and upstream |
| --- | --- | --- |
| FFmpeg and FFprobe 7.1.1, Gyan full static build | Video processing and export | GPLv3 build; [Gyan build/release](https://github.com/GyanD/codexffmpeg/releases/tag/7.1.1), [FFmpeg source tag n7.1.1](https://github.com/FFmpeg/FFmpeg/tree/n7.1.1). See `docs/licenses/GPL-3.0.txt` and `docs/licenses/FFmpeg-build-info.txt`. |
| RIFE ncnn Vulkan executable and RIFE v4.26 model | Frame interpolation | MIT; [rife-ncnn-vulkan](https://github.com/nihui/rife-ncnn-vulkan). |
| Real-CUGAN ncnn Vulkan executable and models | Video upscaling | MIT; [realcugan-ncnn-vulkan](https://github.com/nihui/realcugan-ncnn-vulkan). |
| Real-ESRGAN ncnn Vulkan executable | Image/frame upscaling | MIT; [Real-ESRGAN-ncnn-vulkan](https://github.com/xinntao/Real-ESRGAN-ncnn-vulkan). |
| Real-ESRGAN model weights | Image/frame upscaling | BSD 3-Clause; see the [Real-ESRGAN license](https://github.com/xinntao/Real-ESRGAN/blob/master/LICENSE). |
| ncnn | Inference runtime used by the Vulkan tools | BSD 3-Clause; [Tencent/ncnn](https://github.com/Tencent/ncnn). |
| MODNet model | Subject focus and matting | Apache-2.0; [MODNet](https://github.com/ZHKKKe/MODNet). A copy of the model notice is in `Tools/subject-focus/NOTICE.txt`. |
| NanoVSR models | Video upscaling | See the model-specific terms in `Tools/nanovsr/LICENSE-NanoVSR.txt`. |
| Microsoft Visual C++ runtime (`vcomp140.dll`) | Runtime support for bundled tools | Microsoft redistribution terms apply. |
| ONNX Runtime DirectML and .NET dependencies | Inference and application runtime | Each package retains its upstream license; see the package metadata and notices in the .NET package cache. |

## FFmpeg build and source

The bundled FFmpeg reports `7.1.1-full_build-www.gyan.dev` and is configured
with `--enable-gpl --enable-version3`, so it is a GPLv3 build. Its exact version
and configure arguments are recorded in `docs/licenses/FFmpeg-build-info.txt`.
The FFmpeg GPL text is included at `docs/licenses/GPL-3.0.txt`. FFmpeg is a
separate executable launched by ForgeFlow; it is not relicensed under MIT.

Source for the FFmpeg release is available from the upstream `n7.1.1` source
tag. Gyan's release/build page and release assets are linked above. The Gyan
build embeds additional codec and library components, which retain their own
licenses and source terms. Before redistributing this FFmpeg build, review the
build's complete source and notices from Gyan and the included libraries.

## Optional NVIDIA TensorRT runtime

The default customer build excludes `Tools/tensorrt`. The optional TensorRT
path can use NVIDIA TensorRT/CUDA runtime files supplied by a customer or an
authorized redistribution package. Those components remain under the
applicable NVIDIA and ONNX Runtime terms; they are not covered by ForgeFlow's
MIT license and must not be redistributed unless their vendor terms permit it.
