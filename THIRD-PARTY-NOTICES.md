# Third-Party Notices

The MIT License in `LICENSE` applies to ForgeFlow-authored source code. It does not replace or relicense third-party executables, libraries, or model files included under `Tools/`.

| Component | Included files | License and source |
| --- | --- | --- |
| FFmpeg / FFprobe 7.1.1 full build by Gyan | `Tools/ffmpeg.exe`, `Tools/ffprobe.exe` | GPL-3.0 build. See [`LICENSES/FFmpeg-GPL-3.0.txt`](LICENSES/FFmpeg-GPL-3.0.txt), [Gyan's 7.1.1 build](https://github.com/GyanD/codexffmpeg/releases/tag/7.1.1), [FFmpeg source commit](https://github.com/FFmpeg/FFmpeg/commit/db69d06), and [FFmpeg licensing information](https://ffmpeg.org/legal.html). The binary reports `--enable-gpl`; it is not covered by ForgeFlow's MIT license. |
| RIFE ncnn Vulkan | `Tools/rife-ncnn-vulkan.exe` | MIT; see [`LICENSES/RIFE-ncnn-MIT.txt`](LICENSES/RIFE-ncnn-MIT.txt) and [upstream project](https://github.com/nihui/rife-ncnn-vulkan). |
| Practical-RIFE v4.26 model | `Tools/rife-v4.26/` | MIT; the upstream project states that its linked trained models use the same license. See [`LICENSES/Practical-RIFE-MIT.txt`](LICENSES/Practical-RIFE-MIT.txt) and [project and model](https://github.com/hzwer/Practical-RIFE). |
| Real-CUGAN ncnn Vulkan | `Tools/realcugan-ncnn-vulkan.exe`, `Tools/models-se/` | MIT; see [`LICENSES/Real-CUGAN-MIT.txt`](LICENSES/Real-CUGAN-MIT.txt) and [upstream project](https://github.com/nihui/realcugan-ncnn-vulkan). |
| Real-ESRGAN ncnn Vulkan | `Tools/realesrgan-ncnn-vulkan.exe` | MIT; see [`LICENSES/Real-ESRGAN-ncnn-MIT.txt`](LICENSES/Real-ESRGAN-ncnn-MIT.txt) and [upstream project](https://github.com/xinntao/Real-ESRGAN-ncnn-vulkan). |
| Real-ESRGAN models | `Tools/models/` | BSD-3-Clause; see [`LICENSES/Real-ESRGAN-BSD-3-Clause.txt`](LICENSES/Real-ESRGAN-BSD-3-Clause.txt) and [upstream project](https://github.com/xinntao/Real-ESRGAN). |
| MODNet ONNX model | `Tools/subject-focus/modnet.onnx` | Apache-2.0, as stated in `Tools/subject-focus/NOTICE.txt`; see [`LICENSES/Apache-2.0.txt`](LICENSES/Apache-2.0.txt) and [upstream model](https://huggingface.co/onnx-community/modnet-webnn). |
| ncnn and related dependencies | Bundled within the NCNN executables | BSD-3-Clause for ncnn, plus notices for its included dependencies; see [`LICENSES/ncnn-license.txt`](LICENSES/ncnn-license.txt) and [ncnn's upstream license](https://github.com/Tencent/ncnn/blob/master/LICENSE.txt). |
| Microsoft Visual C++ runtime | `Tools/vcomp140.dll` | Microsoft redistribution terms apply; this file is not covered by ForgeFlow's MIT license. See [Microsoft's redistribution guidance](https://learn.microsoft.com/en-us/cpp/windows/redistributing-visual-cpp-files?view=msvc-170). |

Keep the corresponding upstream notices with third-party binaries when redistributing them. The FFmpeg executable is a GPLv3 build and includes additional external libraries; consult Gyan's build record and FFmpeg's legal page for corresponding source and library terms. Do not treat the top-level MIT license as applying to these components.
