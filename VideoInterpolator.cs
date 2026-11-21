using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VideoProcessing
{
    public class ProcessExecutionResult
    {
        public int ExitCode { get; }
        public string StandardOutput { get; }
        public string StandardError { get; }
        public bool Success => ExitCode == 0;

        public ProcessExecutionResult(int exitCode, string standardOutput, string standardError)
        {
            ExitCode = exitCode;
            StandardOutput = standardOutput;
            StandardError = standardError;
        }
    }

#if !NET5_0_OR_GREATER
    internal static class ProcessExtensions
    {
        public static Task WaitForExitAsync(this Process process, CancellationToken cancellationToken = default)
        {
            if (process.HasExited) return Task.CompletedTask;

            var tcs = new TaskCompletionSource<object?>();
            process.EnableRaisingEvents = true;
            process.Exited += (_, __) => tcs.TrySetResult(null);

            if (cancellationToken != default)
            {
                cancellationToken.Register(() =>
                {
                    if (!process.HasExited)
                    {
                        try { process.CloseMainWindow(); } catch { }
                        if (!process.WaitForExit(150))
                        {
                            try { process.Kill(); } catch { }
                        }
                    }
                    tcs.TrySetCanceled(cancellationToken);
                });
            }

            return tcs.Task;
        }
    }
#endif

    public class VideoInterpolator
    {
        // Paths
        public string RifeExecutablePath { get; }
        public string FfmpegExecutablePath { get; }
        public string FfprobeExecutablePath { get; }

        // RIFE
        // Interpolating a native 60 FPS capture is wasteful and can introduce
        // HUD/edge warping. Keep it opt-in; the UI still exposes RIFE when needed.
        public bool EnableRifeInterpolation { get; set; } = false;
        public string RifeModelName { get; set; } = "rife-v4.26";
        public string RifeGpuId { get; set; } = "auto";
        public string RifeThreads { get; set; } = "1:2:2";
        public bool RifeEnableSpatialTta { get; set; } = false;
        public bool RifeEnableUhdMode { get; set; } = false;
        public bool ProcessUpscaleBeforeRife { get; set; } = false;

        // Frames
        public string IntermediateFrameFormat { get; set; } = "png";
        public int PngCompressionLevel { get; set; } = 1;

        // Outputs toggles
        public bool GeneratePc60 { get; set; } = true;
        public bool Generate4K { get; set; } = false;
        public bool EnableSourcePreserving { get; set; } = true;
        public bool GenerateTikTok30 { get; set; } = true;
        public bool GenerateTikTok60Ultra { get; set; } = true;

        // 4K local master.  Orientation follows the source: 3840x2160 for
        // landscape and 2160x3840 for portrait.  The master uses HEVC on
        // NVIDIA/AMD and falls back to libx265 on CPU for better detail/size.
        public string FourKBitrate { get; set; } = "150M";
        public string FourKMaxRate { get; set; } = "200M";
        public string FourKBufSize { get; set; } = "400M";
        public int FourKCrf { get; set; } = 12;
        public int FourKAudioKbps { get; set; } = 320;

        // PC output (x264 CRF)
        public int PcFps { get; set; } = 60;
        public int PcCrf { get; set; } = 13;
        public string PcBitrate { get; set; } = "";
        public string PcMaxRate { get; set; } = "";
        public string PcBufSize { get; set; } = "";
        public string PcPreset { get; set; } = "veryslow";
        public string PcTune { get; set; } = "film";
        public int PcKeyintSeconds { get; set; } = 1;

        // Patched 30fps output: encode a 120fps intermediate, then retime it to 30fps.
        public int TikTokEncodeFps { get; set; } = 120;
        public double TikTokTargetFps { get; set; } = 30.0;
        public string TikTokPreset { get; set; } = "veryslow";
        public int TikTokKeyintSeconds { get; set; } = 1;

        // TikTok CRF (Capped)
        public int TikTokCrf { get; set; } = 15;
        public string TikTokTargetBitrate { get; set; } = "";
        public string TikTokMaxRate { get; set; } = "70M";
        public string TikTokBufSize { get; set; } = "140M";

        // Backward compatible: if set manually, takes priority over TikTokCrf/MaxRate/BufSize
        public string TikTokRateControl { get; set; } = "";

        // TikTok 60 Ultra: a standards-compliant, upload-ready 59.94 FPS H.264 profile.
        // Kept separate from the legacy patched-30 container so it can be uploaded directly.
        // Upload-ready gaming profile: enough headroom for fine HUD/text detail
        // without producing oversized files that offer no proven TikTok benefit.
        public int TikTok60UltraCrf { get; set; } = 15;
        public string TikTok60UltraBitrate { get; set; } = "35M";
        public string TikTok60UltraMaxRate { get; set; } = "50M";
        public string TikTok60UltraBufSize { get; set; } = "100M";
        public string TikTok60UltraPreset { get; set; } = "veryslow";
        public int TikTok60UltraAudioKbps { get; set; } = 256;
        // Metadata/audio decoys are experimental and are not a visual enhancer.
        // Keep the standards-compliant output as the safe default.
        public bool EnableTikTokAudioDecoy { get; set; } = false;
        public int TikTokAudioDecoyPacketMultiplier { get; set; } = 10;
        // Experimental video sample-table inflation used by several TikTok quality tools.
        // It changes MP4 metadata and appends tiny filler samples; source pixels are not re-encoded.
        public bool EnableTikTokVideoInflate { get; set; } = false;
        public int TikTokVideoInflateMultiplier { get; set; } = 10;
        // Upload signal strategy.  "web-signal" is the low-risk edit-list
        // marker; "legacy-inflate" keeps the older sample-table experiment.
        public string TikTokVideoPatchMode { get; set; } = "legacy-inflate";
        public bool EnableTikTokTurboCopy { get; set; } = true;

        // Pre-filter input before frame extraction
        public bool EnablePreFilter { get; set; } = false;
        public string PreFilterGraph { get; set; } = "nlmeans=s=3:p=3:r=7";

        // Filters
        public bool EnableEnhanceFilters { get; set; } = true;
        public string EnhanceFilterStyle { get; set; } = "natural";
        public double EnhanceFilterStrength { get; set; } = 0.70;
        public int SourceWidth { get; set; }
        public int SourceHeight { get; set; }
        public double SourceFps { get; set; }
        public long SourceSizeBytes { get; set; }
        public double SourceDurationSeconds { get; set; }
        public double FinalOutputTimeScale { get; set; } = 1.0;
        public string HardwareAccelerationMode { get; set; } = "cpu";
        // Safe fast path for a plain 60fps export: NVDEC keeps frames on the
        // CUDA surface and NVENC consumes them directly. It is deliberately
        // limited to a no-filter/no-AI job and falls back to the PNG pipeline
        // if the installed driver rejects any option.
        public bool EnableGpuZeroCopy { get; set; } = true;

        // Optional bundled upscaling
        public bool EnableUpscale { get; set; } = false;
        public string UpscaleExecutablePath { get; set; } = "";
        public string UpscaleModelDirectory { get; set; } = "";
        public string UpscaleModelName { get; set; } = "";
        public string UpscaleDisplayName { get; set; } = "";
        public string UpscaleEngine { get; set; } = "none";
        public NanoVsrExecutionBackend NanoVsrBackend { get; set; } = NanoVsrExecutionBackend.DirectML;
        public int NanoVsrDeviceId { get; set; } = 0;
        public string NanoVsrEngineCachePath { get; set; } = "";
        public int UpscaleScale { get; set; } = 1;
        public string UpscaleGpuId { get; set; } = "auto";
        public int UpscaleTileSize { get; set; } = 0;
        public string UpscaleThreads { get; set; } = "1:2:2";
        // Explicit outcome for the optional quality accelerator. A successful
        // export must not be mistaken for a successful TensorRT enhancement.
        public string LastQualityBoostStatus { get; private set; } = "not-run";
        public string LastMotionStatus { get; private set; } = "not-run";
        public string LastNvidiaVfxStatus { get; private set; } = "not-run";
        public string LastPreFilterStatus { get; private set; } = "not-run";
        public string LastSubjectFocusStatus { get; private set; } = "not-run";

        // Developer-only NVIDIA VFX trial.  This is intentionally an external
        // opt-in runtime: the SDK/model files are not bundled or redistributed.
        public bool EnableNvidiaVfxTrial { get; set; } = false;
        public string NvidiaVfxPythonPath { get; set; } = "";
        public string NvidiaVfxScriptPath { get; set; } = "";
        public string NvidiaVfxRuntimePath { get; set; } = "";
        public string NvidiaVfxQuality { get; set; } = "HIGHBITRATE_ULTRA";
        public int NvidiaVfxScale { get; set; } = 1;

        // Optional foreground/person separation.
        public bool EnableSubjectFocus { get; set; } = false;
        public SubjectFocusMode SubjectFocusMode { get; set; } = SubjectFocusMode.Off;
        public double SubjectFocusStrength { get; set; } = 0.70;
        public string SubjectFocusModelPath { get; set; } = "";

        // Audio
        public string AudioArgs { get; set; } = "-c:a aac -b:a 256k -ar 48000 -ac 2";

        public bool VerboseLogging { get; set; } = false;
        public bool UseNvidiaHevcEncoder { get; set; } = false;
        public bool UseAmdHevcEncoder { get; set; } = false;

        private string _tempSessionDirectory = "";

        public VideoInterpolator(string rifeExecutablePath, string ffmpegExecutablePath, string? ffprobeExecutablePath = null)
        {
            if (string.IsNullOrWhiteSpace(rifeExecutablePath) || !File.Exists(rifeExecutablePath))
                throw new FileNotFoundException("RIFE executable not found.", rifeExecutablePath);
            if (string.IsNullOrWhiteSpace(ffmpegExecutablePath) || !File.Exists(ffmpegExecutablePath))
                throw new FileNotFoundException("FFmpeg executable not found.", ffmpegExecutablePath);

            RifeExecutablePath = Path.GetFullPath(rifeExecutablePath);
            FfmpegExecutablePath = Path.GetFullPath(ffmpegExecutablePath);

            if (!string.IsNullOrWhiteSpace(ffprobeExecutablePath) && File.Exists(ffprobeExecutablePath))
            {
                FfprobeExecutablePath = Path.GetFullPath(ffprobeExecutablePath);
            }
            else
            {
                string probePath = Path.Combine(Path.GetDirectoryName(FfmpegExecutablePath) ?? string.Empty, "ffprobe.exe");
                FfprobeExecutablePath = File.Exists(probePath) ? probePath : FfmpegExecutablePath;
            }
        }

        public async Task<ProcessExecutionResult> InterpolateVideoAsync(
            string inputVideoPath,
            string outputVideoPath,
            bool showConsoleWindows = false,
            bool useNvdecForDecoding = false,
            Action<string>? progressCallback = null,
            CancellationToken cancellationToken = default)
        {
            if (!File.Exists(inputVideoPath))
                return new ProcessExecutionResult(-1, "", $"Input file not found: {inputVideoPath}");

            var validationError = ValidateTooling();
            if (!string.IsNullOrWhiteSpace(validationError))
                return new ProcessExecutionResult(-1, "", validationError);

            DateTime runStartedUtc = DateTime.UtcNow;
            LastQualityBoostStatus = EnableUpscale ? "running" : "off";
            LastMotionStatus = EnableRifeInterpolation ? "running" : "off";
            LastNvidiaVfxStatus = EnableNvidiaVfxTrial ? "running" : "off";
            LastPreFilterStatus = EnablePreFilter ? "running" : "off";
            LastSubjectFocusStatus = EnableSubjectFocus && SubjectFocusMode != SubjectFocusMode.Off ? "running" : "off";
            SetupTempDirectory();
            string inputFramesDir = Path.Combine(_tempSessionDirectory, "input_frames");

            try
            {
                Directory.CreateDirectory(inputFramesDir);

                var (originalFps, fpsResult) = await GetOriginalVideoFpsAsync(inputVideoPath, progressCallback, cancellationToken).ConfigureAwait(false);
                if (originalFps == null)
                    return new ProcessExecutionResult(fpsResult.ExitCode, fpsResult.StandardOutput, "Failed to determine FPS: " + fpsResult.StandardError);
                var (durationSeconds, durationResult) = await GetVideoDurationAsync(inputVideoPath, progressCallback, cancellationToken).ConfigureAwait(false);
                if (durationSeconds == null)
                    progressCallback?.Invoke("WARNING: Could not determine video duration for detailed progress.");
                int expectedExtractFrames = durationSeconds.HasValue
                    ? Math.Max(1, (int)Math.Ceiling(durationSeconds.Value * originalFps.Value))
                    : 0;

                double workingSequenceFps = EnableRifeInterpolation ? originalFps.Value * 2.0 : originalFps.Value;
                progressCallback?.Invoke(EnableRifeInterpolation
                    ? $"Original FPS: {originalFps.Value:0.###} -> RIFE FPS: {workingSequenceFps:0.###}"
                    : $"Original FPS: {originalFps.Value:0.###} | RIFE disabled");
                if (string.Equals(EnhanceFilterStyle, "gaming-restore", StringComparison.OrdinalIgnoreCase))
                    progressCallback?.Invoke(GetGamingRestoreAnalysisMessage());

                var (audioStartTime, _) = await GetAudioStartTimeAsync(inputVideoPath, progressCallback, cancellationToken).ConfigureAwait(false);

                string effectiveInput = inputVideoPath;
                if (EnablePreFilter)
                {
                    progressCallback?.Invoke("STAGE:0/0 Pre-filtering input");
                    var (pfPath, pfRes) = await PreFilterInputAsync(inputVideoPath, showConsoleWindows, progressCallback, cancellationToken).ConfigureAwait(false);
                    if (!pfRes.Success)
                    {
                        LastPreFilterStatus = "skipped-prefilter";
                        progressCallback?.Invoke("Pre-filter failed safely; continuing with the original source.");
                        progressCallback?.Invoke("Pre-filter error: " + SummarizeProcessError(pfRes));
                    }
                    else
                    {
                        LastPreFilterStatus = "enhanced";
                        effectiveInput = pfPath;
                        progressCallback?.Invoke("Pre-filter done.");
                    }
                }

                int totalStages = 1;
                if (EnableNvidiaVfxTrial) totalStages++;
                if (EnableRifeInterpolation) totalStages++;
                if (EnableUpscale && !string.Equals(UpscaleEngine, "nvidia-vfx", StringComparison.OrdinalIgnoreCase)) totalStages++;
                if (EnableSubjectFocus && SubjectFocusMode != SubjectFocusMode.Off) totalStages++;
                if (GeneratePc60) totalStages++;
                if (Generate4K) totalStages++;
                if (GenerateTikTok60Ultra) totalStages++;
                if (GenerateTikTok30) totalStages += 2;

                // When the user requests only a 4K master and no processing
                // stage is enabled, preserve the original bitstream. This is
                // the only path that can honestly guarantee zero pixel loss.
                if (Generate4K && !GeneratePc60 && !GenerateTikTok60Ultra && !GenerateTikTok30 &&
                    EnableSourcePreserving && CanSourcePreserveFourK(originalFps.Value, inputVideoPath))
                {
                    string directDir = Path.GetDirectoryName(outputVideoPath) ?? AppContext.BaseDirectory;
                    Directory.CreateDirectory(directDir);
                    string directBase = Path.GetFileNameWithoutExtension(outputVideoPath);
                    string directExt = Path.GetExtension(outputVideoPath);
                    if (string.IsNullOrWhiteSpace(directExt)) directExt = ".mp4";
                    string directOut = Path.Combine(directDir, $"{directBase}_4K_Ultra{directExt}");
                    progressCallback?.Invoke("Source-Preserving 4K: source already matches the target canvas; copying bitstream without re-encoding.");
                    var directResult = await CopySourcePreservingAsync(inputVideoPath, directOut, showConsoleWindows, progressCallback, cancellationToken, durationSeconds).ConfigureAwait(false);
                    if (directResult.Success)
                        return new ProcessExecutionResult(0, "Done.\n4K Ultra (source-preserving): " + directOut, "");
                }

                if (CanUseGpuZeroCopy(originalFps.Value))
                {
                    string zeroCopyDir = Path.GetDirectoryName(outputVideoPath) ?? AppContext.BaseDirectory;
                    Directory.CreateDirectory(zeroCopyDir);
                    string zeroCopyExt = Path.GetExtension(outputVideoPath);
                    if (string.IsNullOrWhiteSpace(zeroCopyExt)) zeroCopyExt = ".mp4";
                    string zeroCopyOut = Path.Combine(zeroCopyDir, Path.GetFileNameWithoutExtension(outputVideoPath) + $"_Video 60FPS{zeroCopyExt}");
                    progressCallback?.Invoke("GPU Zero-Copy: NVDEC CUDA surfaces → NVENC (no frame extraction).");
                    ProcessExecutionResult zeroCopyResult = await EncodeGpuZeroCopyAsync(
                        inputVideoPath, zeroCopyOut, showConsoleWindows, progressCallback,
                        cancellationToken, durationSeconds).ConfigureAwait(false);
                    if (zeroCopyResult.Success)
                        return new ProcessExecutionResult(0, "Done.\nGPU Zero-Copy Video 60FPS: " + zeroCopyOut, "");
                    progressCallback?.Invoke("WARNING: GPU Zero-Copy was rejected by the driver; continuing with the compatible frame pipeline.");
                }

                int stageNumber = 1;
                if (EnableNvidiaVfxTrial)
                {
                    var nvidiaStage = GetStageProgressRange(stageNumber, totalStages);
                    progressCallback?.Invoke($"STAGE:{stageNumber}/{totalStages} NVIDIA Video Super Resolution (local trial)");
                    progressCallback?.Invoke($"NVIDIA VFX quality: {NvidiaVfxQuality}, scale={Math.Max(1, NvidiaVfxScale)}x, local-only runtime.");
                    string nvidiaOutput = Path.Combine(_tempSessionDirectory, "nvidia_vfx_input.mp4");
                    var nvidiaResult = await RunNvidiaVfxTrialAsync(
                        effectiveInput,
                        nvidiaOutput,
                        showConsoleWindows,
                        progressCallback,
                        cancellationToken).ConfigureAwait(false);
                    if (!nvidiaResult.Success)
                    {
                        LastNvidiaVfxStatus = "skipped-vfx";
                        progressCallback?.Invoke("NVIDIA VSR trial failed safely; continuing with the original source.");
                        progressCallback?.Invoke("VFX error: " + SummarizeProcessError(nvidiaResult));
                    }
                    else
                    {
                        LastNvidiaVfxStatus = "enhanced";
                        effectiveInput = nvidiaOutput;
                    }
                    progressCallback?.Invoke($"PROGRESS:{nvidiaStage.End}%");
                    stageNumber++;
                }

                var stageRange = GetStageProgressRange(stageNumber, totalStages);
                progressCallback?.Invoke($"STAGE:{stageNumber}/{totalStages} Extracting frames");
                progressCallback?.Invoke($"PROGRESS:{stageRange.Start}%");
                string framePattern = $"frame_%08d.{IntermediateFrameFormat.ToLowerInvariant()}";
                if (!EnsureFrameWorkspace(expectedExtractFrames, progressCallback))
                    return new ProcessExecutionResult(-1, "", "Insufficient free disk space for the temporary frame workspace. Choose another drive or use GPU Zero-Copy for a plain 60 FPS export.");
                if (useNvdecForDecoding && UseNvidiaAcceleration)
                    progressCallback?.Invoke("NVDEC decoding: adaptive mode (8 decoder threads).");
                var extract = await ExtractFramesAsync(effectiveInput, inputFramesDir, framePattern, useNvdecForDecoding, showConsoleWindows, progressCallback, cancellationToken, expectedExtractFrames, stageRange.Start, stageRange.End).ConfigureAwait(false);
                if (!extract.Success && useNvdecForDecoding && IsHardwareDecodeFailure(extract))
                {
                    progressCallback?.Invoke("NVDEC needs fewer decoder surfaces; retrying in compatibility mode (4 threads).");
                    ClearFrameDirectory(inputFramesDir);
                    extract = await ExtractFramesAsync(effectiveInput, inputFramesDir, framePattern, useNvdec: true, showConsoleWindows, progressCallback, cancellationToken, expectedExtractFrames, stageRange.Start, stageRange.End, nvdecThreads: 4).ConfigureAwait(false);
                    if (extract.Success)
                        progressCallback?.Invoke("NVDEC compatibility mode active; GPU decoding succeeded.");
                }
                if (!extract.Success && useNvdecForDecoding && IsHardwareDecodeFailure(extract))
                {
                    progressCallback?.Invoke("NVDEC is not available for this stream; continuing with CPU decoding while RIFE and encoding remain on GPU.");
                    ClearFrameDirectory(inputFramesDir);
                    extract = await ExtractFramesAsync(effectiveInput, inputFramesDir, framePattern, useNvdec: false, showConsoleWindows, progressCallback, cancellationToken, expectedExtractFrames, stageRange.Start, stageRange.End).ConfigureAwait(false);
                }
                if (!extract.Success && UseAmdAcceleration && IsHardwareDecodeFailure(extract))
                {
                    progressCallback?.Invoke("AMD D3D11 decoding is unavailable for this stream; retrying with CPU decoding while AMF encoding remains enabled.");
                    ClearFrameDirectory(inputFramesDir);
                    extract = await ExtractFramesAsync(effectiveInput, inputFramesDir, framePattern, useNvdec: false, showConsoleWindows, progressCallback, cancellationToken, expectedExtractFrames, stageRange.Start, stageRange.End, useHardwareDecode: false).ConfigureAwait(false);
                }
                if (!extract.Success) return extract;
                progressCallback?.Invoke($"PROGRESS:{stageRange.End}%");

                string workingFramesDir = inputFramesDir;
                string workingFramePattern = framePattern;

                if (EnableUpscale && !EnableNvidiaVfxTrial && ProcessUpscaleBeforeRife)
                {
                    stageNumber++;
                    stageRange = GetStageProgressRange(stageNumber, totalStages);
                    string upscaleLabel = string.IsNullOrWhiteSpace(UpscaleDisplayName) ? UpscaleModelName : UpscaleDisplayName;
                    progressCallback?.Invoke($"STAGE:{stageNumber}/{totalStages} Boosting resolution ({upscaleLabel})");
                    string upscaledBeforeDir = Path.Combine(_tempSessionDirectory, "upscaled_before_rife");
                    var upscaleRes = await UpscaleFramesAsync(workingFramesDir, upscaledBeforeDir, showConsoleWindows, progressCallback, cancellationToken, stageRange.Start, stageRange.End).ConfigureAwait(false);
                    if (!upscaleRes.Success) return upscaleRes;
                    workingFramesDir = upscaledBeforeDir;
                    workingFramePattern = framePattern;
                    progressCallback?.Invoke($"PROGRESS:{stageRange.End}%");
                }

                if (EnableRifeInterpolation)
                {
                    stageNumber++;
                    stageRange = GetStageProgressRange(stageNumber, totalStages);
                    progressCallback?.Invoke($"STAGE:{stageNumber}/{totalStages} RIFE interpolation");
                    progressCallback?.Invoke($"PROGRESS:{stageRange.Start}%");

                    string rifeFramesDir = Path.Combine(_tempSessionDirectory, ProcessUpscaleBeforeRife ? "rife_after_upscale" : "rife_output_frames");
                    Directory.CreateDirectory(rifeFramesDir);

                    int pollInputCount = Directory.GetFiles(workingFramesDir, $"*.{IntermediateFrameFormat.ToLowerInvariant()}").Length;
                    int pollExpected = pollInputCount * 2;
                    using var rifePollCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    var rifePollTask = Task.Run(async () =>
                    {
                        try
                        {
                            while (!rifePollCts.Token.IsCancellationRequested)
                            {
                                await Task.Delay(700, rifePollCts.Token).ConfigureAwait(false);
                                int done = Directory.GetFiles(rifeFramesDir, $"*.{IntermediateFrameFormat.ToLowerInvariant()}").Length;
                                if (pollExpected > 0)
                                {
                                    int progress = stageRange.Start + (int)Math.Round(Math.Min(1.0, (double)done / pollExpected) * (stageRange.End - stageRange.Start));
                                    progressCallback?.Invoke($"PROGRESS:{progress}%");
                                    progressCallback?.Invoke($"RIFE: {Math.Min(done, pollExpected)}/{pollExpected} frames");
                                }
                            }
                        }
                        catch (OperationCanceledException) { }
                    }, CancellationToken.None);

                    var rife = await RunRifeInterpolationAsync(workingFramesDir, rifeFramesDir, showConsoleWindows, progressCallback, cancellationToken).ConfigureAwait(false);
                    rifePollCts.Cancel();
                    try { await rifePollTask.ConfigureAwait(false); } catch { }
                    if (!rife.Success)
                    {
                        LastMotionStatus = "skipped-rife";
                        if (!TryPreserveNativeFrames(workingFramesDir, rifeFramesDir, cancellationToken, IntermediateFrameFormat))
                            return rife;
                        progressCallback?.Invoke("RIFE failed safely; continuing with the current frames without interpolation.");
                        progressCallback?.Invoke("RIFE error: " + SummarizeProcessError(rife));
                        progressCallback?.Invoke($"PROGRESS:{stageRange.End}%");
                    }
                    else
                    {
                        int inputFrameCount = Directory.GetFiles(workingFramesDir, $"*.{IntermediateFrameFormat.ToLowerInvariant()}").Length;
                        int outputFrameCount = Directory.GetFiles(rifeFramesDir, $"*.{IntermediateFrameFormat.ToLowerInvariant()}").Length;
                        progressCallback?.Invoke($"RIFE frames: input={inputFrameCount}, output={outputFrameCount}");
                        // Some RIFE builds return exit code 0 after a device
                        // reset but leave only a partial output directory. Do
                        // not feed that incomplete sequence to FFmpeg.
                        if (outputFrameCount < Math.Max(1, inputFrameCount))
                        {
                            LastMotionStatus = "skipped-rife";
                            if (!TryPreserveNativeFrames(workingFramesDir, rifeFramesDir, cancellationToken, IntermediateFrameFormat))
                                return new ProcessExecutionResult(-1, rife.StandardOutput, "RIFE completed with an incomplete frame sequence.");
                            progressCallback?.Invoke("RIFE returned an incomplete frame sequence; native frames were preserved.");
                            progressCallback?.Invoke($"PROGRESS:{stageRange.End}%");
                        }
                        else
                        {
                            LastMotionStatus = "enhanced";
                            progressCallback?.Invoke($"PROGRESS:{stageRange.End}%");
                            workingFramesDir = rifeFramesDir;
                            workingFramePattern = $"%08d.{IntermediateFrameFormat.ToLowerInvariant()}";
                        }
                    }
                }

                if (EnableUpscale && !EnableNvidiaVfxTrial && !ProcessUpscaleBeforeRife)
                {
                    stageNumber++;
                    stageRange = GetStageProgressRange(stageNumber, totalStages);
                    string upscaleLabel = string.IsNullOrWhiteSpace(UpscaleDisplayName) ? UpscaleModelName : UpscaleDisplayName;
                    progressCallback?.Invoke($"STAGE:{stageNumber}/{totalStages} Boosting resolution ({upscaleLabel})");
                    string upscaledAfterDir = Path.Combine(_tempSessionDirectory, "upscaled_after_rife");
                    var upscaleRes = await UpscaleFramesAsync(workingFramesDir, upscaledAfterDir, showConsoleWindows, progressCallback, cancellationToken, stageRange.Start, stageRange.End).ConfigureAwait(false);
                    if (!upscaleRes.Success) return upscaleRes;
                    workingFramesDir = upscaledAfterDir;
                    if (string.IsNullOrWhiteSpace(workingFramePattern))
                        workingFramePattern = framePattern;
                    progressCallback?.Invoke($"PROGRESS:{stageRange.End}%");
                }

                if (EnableSubjectFocus && SubjectFocusMode != SubjectFocusMode.Off)
                {
                    stageNumber++;
                    stageRange = GetStageProgressRange(stageNumber, totalStages);
                    progressCallback?.Invoke($"STAGE:{stageNumber}/{totalStages} Subject Focus");
                    string subjectFocusDir = Path.Combine(_tempSessionDirectory, "subject_focus_frames");
                    var subjectFocusRes = await ApplySubjectFocusAsync(workingFramesDir, subjectFocusDir, showConsoleWindows, progressCallback, cancellationToken, stageRange.Start, stageRange.End).ConfigureAwait(false);
                    if (!subjectFocusRes.Success)
                    {
                        LastSubjectFocusStatus = "skipped-subject";
                        if (!TryPreserveNativeFrames(workingFramesDir, subjectFocusDir, cancellationToken, IntermediateFrameFormat))
                            return subjectFocusRes;
                        progressCallback?.Invoke("Subject Focus failed safely; continuing with the current frames.");
                        progressCallback?.Invoke("Subject Focus error: " + SummarizeProcessError(subjectFocusRes));
                        progressCallback?.Invoke($"PROGRESS:{stageRange.End}%");
                    }
                    else
                    {
                        LastSubjectFocusStatus = "enhanced";
                        workingFramesDir = subjectFocusDir;
                        progressCallback?.Invoke($"PROGRESS:{stageRange.End}%");
                    }
                }

                string encodeFramesDir = workingFramesDir;

                string outDir = Path.GetDirectoryName(outputVideoPath) ?? "";
                if (!Directory.Exists(outDir) && !string.IsNullOrWhiteSpace(outDir))
                    Directory.CreateDirectory(outDir);

                string baseName = Path.GetFileNameWithoutExtension(outputVideoPath);
                string ext = Path.GetExtension(outputVideoPath);
                if (string.IsNullOrWhiteSpace(ext)) ext = ".mp4";

                string pcOut = Path.Combine(outDir, $"{baseName}_Video 60FPS{ext}");
                string fourKOut = Path.Combine(outDir, $"{baseName}_4K_Ultra{ext}");
                string tikTok60UltraOut = Path.Combine(outDir, $"{baseName}_TikTok60Ultra.mp4");
                string tikTok60UltraTemp = (EnableTikTokAudioDecoy || EnableTikTokVideoInflate)
                    ? Path.Combine(_tempSessionDirectory, $"{baseName}_TikTok60Ultra_CLEAN_TEMP.mp4")
                    : tikTok60UltraOut;
                string tikTok30Temp = Path.Combine(_tempSessionDirectory, $"{baseName}_TikTok30_TEMP{ext}");
                string tikTok30Out = Path.Combine(outDir, $"{baseName}_TikTok30FPS{ext}");

                int tikTokEncodeFps = Math.Max(120, TikTokEncodeFps);

                if (GeneratePc60)
                {
                    stageNumber++;
                    stageRange = GetStageProgressRange(stageNumber, totalStages);
                    progressCallback?.Invoke($"STAGE:{stageNumber}/{totalStages} Encoding Video 60FPS");
                    var pcRes = await EncodePcWithFallbackAsync(pcOut, encodeFramesDir, workingFramePattern, workingSequenceFps, inputVideoPath, audioStartTime,
                        showConsoleWindows, progressCallback, cancellationToken, durationSeconds, stageRange.Start, stageRange.End).ConfigureAwait(false);
                    if (!pcRes.Success) return pcRes;
                    progressCallback?.Invoke($"PROGRESS:{stageRange.End}%");
                }

                if (Generate4K)
                {
                    stageNumber++;
                    stageRange = GetStageProgressRange(stageNumber, totalStages);
                    progressCallback?.Invoke($"STAGE:{stageNumber}/{totalStages} Encoding 4K Ultra Master");
                    progressCallback?.Invoke($"4K canvas: {GetFourKDimensions().Width}x{GetFourKDimensions().Height}, HEVC/high bitrate profile.");
                    var fourKRes = await Encode4KWithFallbackAsync(
                        fourKOut, encodeFramesDir, workingFramePattern, workingSequenceFps, inputVideoPath, audioStartTime,
                        showConsoleWindows, progressCallback, cancellationToken, durationSeconds, stageRange.Start, stageRange.End).ConfigureAwait(false);
                    if (!fourKRes.Success) return fourKRes;
                    progressCallback?.Invoke($"PROGRESS:{stageRange.End}%");
                }

                if (GenerateTikTok60Ultra)
                {
                    stageNumber++;
                    stageRange = GetStageProgressRange(stageNumber, totalStages);
                    bool useTurboCopy = EnableTikTokTurboCopy &&
                                        originalFps.Value >= 59.0 && originalFps.Value <= 60.1 &&
                                        !EnableRifeInterpolation && !EnableUpscale && !EnablePreFilter &&
                                        (!EnableSubjectFocus || SubjectFocusMode == SubjectFocusMode.Off) &&
                                        (!EnableEnhanceFilters || string.Equals(EnhanceFilterStyle, "natural", StringComparison.OrdinalIgnoreCase)) &&
                                        IsTikTok1080CompatibleSource() &&
                                        string.Equals(Path.GetExtension(inputVideoPath), ".mp4", StringComparison.OrdinalIgnoreCase);
                    ProcessExecutionResult ultraRes;
                    progressCallback?.Invoke($"TikTok Turbo eligibility: enabled={EnableTikTokTurboCopy}, sourceFps={originalFps.Value:0.###}, input={Path.GetExtension(inputVideoPath)}, rife={!EnableRifeInterpolation}, upscale={!EnableUpscale}, prefilter={!EnablePreFilter}, subject={!EnableSubjectFocus || SubjectFocusMode == SubjectFocusMode.Off}, natural={!EnableEnhanceFilters || string.Equals(EnhanceFilterStyle, "natural", StringComparison.OrdinalIgnoreCase)}");
                    if (useTurboCopy)
                    {
                        progressCallback?.Invoke($"STAGE:{stageNumber}/{totalStages} TikTok 60 Ultra Turbo Copy");
                        progressCallback?.Invoke("TikTok Turbo Copy: preserving the original video bitstream without re-encoding.");
                        ultraRes = await EncodeTikTok60TurboAsync(tikTok60UltraTemp, inputVideoPath, showConsoleWindows,
                            progressCallback, cancellationToken, durationSeconds, stageRange.Start, stageRange.End).ConfigureAwait(false);
                        if (!ultraRes.Success)
                        {
                            progressCallback?.Invoke("WARNING: Turbo Copy was not compatible with this source; falling back to Studio encode.");
                            ultraRes = await EncodeTikTok60UltraWithFallbackAsync(tikTok60UltraTemp, encodeFramesDir, workingFramePattern, workingSequenceFps,
                                inputVideoPath, audioStartTime, showConsoleWindows, progressCallback, cancellationToken, durationSeconds, stageRange.Start, stageRange.End).ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        progressCallback?.Invoke($"STAGE:{stageNumber}/{totalStages} Encoding TikTok 60 Ultra Studio");
                    (int deliveryWidth, int deliveryHeight) = GetTikTokDeliveryDimensions();
                    progressCallback?.Invoke($"TikTok 60 Ultra Studio profile: {deliveryWidth}x{deliveryHeight}, {TikTok60UltraBitrate} target / {TikTok60UltraMaxRate} max, 59.94 FPS, H.264 High, level {GetTikTokH264Level()}.");
                        ultraRes = await EncodeTikTok60UltraWithFallbackAsync(tikTok60UltraTemp, encodeFramesDir, workingFramePattern, workingSequenceFps,
                            inputVideoPath, audioStartTime, showConsoleWindows, progressCallback, cancellationToken, durationSeconds, stageRange.Start, stageRange.End).ConfigureAwait(false);
                    }
                    if (!ultraRes.Success) return ultraRes;
                    if (File.Exists(tikTok60UltraTemp))
                    {
                        double masterSizeMb = new FileInfo(tikTok60UltraTemp).Length / (1024.0 * 1024.0);
                        progressCallback?.Invoke($"TikTok HQ master ready: {masterSizeMb:0.0} MB before upload (H.264 High / 60fps).");
                    }
                    string preservationInput = tikTok60UltraTemp;
                    if (EnableTikTokVideoInflate || string.Equals(TikTokVideoPatchMode, "web-signal", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            string patchedTemp = Path.Combine(_tempSessionDirectory, $"{baseName}_TikTok60Ultra_VIDEO_SIGNAL_TEMP.mp4");
                            TikTokVideoInflatePatcher.PatchResult? legacyPatch = null;
                            TikTokWebSignalPatcher.PatchResult? webPatch = null;
                            if (string.Equals(TikTokVideoPatchMode, "web-signal", StringComparison.OrdinalIgnoreCase))
                            {
                                webPatch = TikTokWebSignalPatcher.Patch(preservationInput, patchedTemp);
                                progressCallback?.Invoke(webPatch.Message);
                            }
                            else
                            {
                                legacyPatch = TikTokVideoInflatePatcher.Patch(
                                    preservationInput,
                                    patchedTemp,
                                    Math.Clamp(TikTokVideoInflateMultiplier, 2, 20));
                                progressCallback?.Invoke(legacyPatch.Message);
                            }
                            if ((webPatch?.Applied ?? false) || (legacyPatch?.Applied ?? false))
                                preservationInput = patchedTemp;
                        }
                        catch (Exception ex)
                        {
                            progressCallback?.Invoke("WARNING: TikTok video quality patch was skipped: " + ex.Message);
                        }
                    }

                    if (EnableTikTokAudioDecoy)
                    {
                        try
                        {
                            TikTokAudioDecoyPatcher.PatchResult patch = TikTokAudioDecoyPatcher.Patch(
                                preservationInput,
                                tikTok60UltraOut,
                                Math.Clamp(TikTokAudioDecoyPacketMultiplier, 2, 20));
                            progressCallback?.Invoke(patch.Message);
                            (bool valid, string validationMessage) = await ValidateTikTokPreservationOutputAsync(
                                tikTok60UltraOut,
                                durationSeconds,
                                cancellationToken).ConfigureAwait(false);
                            if (!valid)
                            {
                                File.Copy(tikTok60UltraTemp, tikTok60UltraOut, true);
                                progressCallback?.Invoke("WARNING: TikTok preservation validation failed; clean upload-safe master restored. " + validationMessage);
                            }
                            else
                            {
                                progressCallback?.Invoke("TikTok preservation validation: PASS. " + validationMessage);
                            }
                        }
                        catch (Exception ex)
                        {
                            File.Copy(tikTok60UltraTemp, tikTok60UltraOut, true);
                            progressCallback?.Invoke("WARNING: TikTok audio compatibility patch was skipped: " + ex.Message);
                        }
                    }
                    else if (EnableTikTokVideoInflate || string.Equals(TikTokVideoPatchMode, "web-signal", StringComparison.OrdinalIgnoreCase))
                    {
                        File.Copy(preservationInput, tikTok60UltraOut, true);
                        var (valid, validationMessage) = await ValidateTikTokPreservationOutputAsync(
                            tikTok60UltraOut,
                            durationSeconds,
                            cancellationToken).ConfigureAwait(false);
                        progressCallback?.Invoke(valid
                            ? "TikTok video quality patch validation: PASS. " + validationMessage
                            : "WARNING: TikTok video quality patch validation was inconclusive. " + validationMessage);
                    }

                    // Validate every TikTok60Ultra output, including the clean
                    // Studio path. This catches a successful FFmpeg exit that
                    // nevertheless produced an unexpected resolution/cadence.
                    (bool ultraValid, string ultraValidation) = await ValidateTikTokPreservationOutputAsync(
                        tikTok60UltraOut, durationSeconds, cancellationToken).ConfigureAwait(false);
                    bool hasSeparateCleanMaster = !string.Equals(
                        Path.GetFullPath(tikTok60UltraTemp), Path.GetFullPath(tikTok60UltraOut),
                        StringComparison.OrdinalIgnoreCase);
                    if (!ultraValid && hasSeparateCleanMaster && File.Exists(tikTok60UltraTemp))
                    {
                        File.Copy(tikTok60UltraTemp, tikTok60UltraOut, true);
                        (ultraValid, ultraValidation) = await ValidateTikTokPreservationOutputAsync(
                            tikTok60UltraOut, durationSeconds, cancellationToken).ConfigureAwait(false);
                        progressCallback?.Invoke("WARNING: TikTok patch output failed validation; clean Studio master restored.");
                    }
                    if (!ultraValid)
                        return new ProcessExecutionResult(-1, "", "TikTok60Ultra validation failed: " + ultraValidation);
                    progressCallback?.Invoke("TikTok60Ultra final validation: PASS — " + ultraValidation);
                    progressCallback?.Invoke($"PROGRESS:{stageRange.End}%");
                }

                if (GenerateTikTok30)
                {
                    stageNumber++;
                    stageRange = GetStageProgressRange(stageNumber, totalStages);
                    progressCallback?.Invoke($"STAGE:{stageNumber}/{totalStages} Preparing TikTok30FPS");
                    var ttRes = await EncodeTikTokWithFallbackAsync(tikTok30Temp, encodeFramesDir, workingFramePattern, workingSequenceFps, inputVideoPath, audioStartTime,
                        tikTokEncodeFps,
                        showConsoleWindows, progressCallback, cancellationToken, durationSeconds, stageRange.Start, stageRange.End).ConfigureAwait(false);
                    if (!ttRes.Success) return ttRes;
                    progressCallback?.Invoke($"PROGRESS:{stageRange.End}%");

                    stageNumber++;
                    stageRange = GetStageProgressRange(stageNumber, totalStages);
                    progressCallback?.Invoke($"STAGE:{stageNumber}/{totalStages} Applying patched 30FPS timing");
                    var (tempFps, tempFpsResult) = await GetOriginalVideoFpsAsync(tikTok30Temp, progressCallback, cancellationToken).ConfigureAwait(false);
                    if (tempFps == null || tempFps.Value <= 0)
                        return new ProcessExecutionResult(tempFpsResult.ExitCode, tempFpsResult.StandardOutput, "Failed to determine patched intermediate FPS: " + tempFpsResult.StandardError);
                    double patchScale = tempFps.Value / Math.Max(1, TikTokTargetFps);
                    progressCallback?.Invoke($"30FPS patch active: {tempFps.Value:0.###}fps intermediate -> {TikTokTargetFps:0.###}fps timing (scale {patchScale:0.###})");
                    ProcessExecutionResult tikTokFinalRes = await RetimeCopyAsync(tikTok30Temp, tikTok30Out, patchScale, showConsoleWindows, progressCallback, cancellationToken, durationSeconds, stageRange.Start, 100).ConfigureAwait(false);
                    if (!tikTokFinalRes.Success) return tikTokFinalRes;

                    progressCallback?.Invoke("PROGRESS:100%");
                }

                var missingOutputs = new List<string>();
                if (GeneratePc60 && !IsUsableOutput(pcOut, runStartedUtc)) missingOutputs.Add(pcOut);
                if (Generate4K && !IsUsableOutput(fourKOut, runStartedUtc)) missingOutputs.Add(fourKOut);
                if (GenerateTikTok60Ultra && !IsUsableOutput(tikTok60UltraOut, runStartedUtc)) missingOutputs.Add(tikTok60UltraOut);
                if (GenerateTikTok30 && !IsUsableOutput(tikTok30Out, runStartedUtc)) missingOutputs.Add(tikTok30Out);
                if (missingOutputs.Count > 0)
                    return new ProcessExecutionResult(-1, "", "The export pipeline reported success but did not create: " + string.Join(", ", missingOutputs));

                var msg = new StringBuilder();
                msg.AppendLine("Done.");
                if (GeneratePc60) msg.AppendLine("Video 60FPS: " + pcOut);
                if (Generate4K) msg.AppendLine("4K Ultra: " + fourKOut);
                if (GenerateTikTok60Ultra) msg.AppendLine("TikTok60Ultra: " + tikTok60UltraOut);
                if (GenerateTikTok30) msg.AppendLine("TikTok30FPS: " + tikTok30Out);

                return new ProcessExecutionResult(0, msg.ToString(), "");
            }
            catch (OperationCanceledException)
            {
                return new ProcessExecutionResult(-99, "", "Operation was canceled.");
            }
            catch (Exception ex)
            {
                return new ProcessExecutionResult(-1, "", $"Critical error: {ex.GetType().Name} - {ex.Message}\n{ex.StackTrace}");
            }
            finally
            {
                CleanupTempDirectory();
            }
        }

        private static bool IsUsableOutput(string path, DateTime runStartedUtc)
        {
            try
            {
                if (!File.Exists(path)) return false;
                FileInfo info = new(path);
                // NTFS timestamp precision can be coarser than the process
                // start instant, so allow a small clock-resolution margin.
                return info.Length >= 1024 && info.LastWriteTimeUtc >= runStartedUtc.AddSeconds(-2);
            }
            catch
            {
                return false;
            }
        }

        private bool CanUseGpuZeroCopy(double originalFps)
        {
            return EnableGpuZeroCopy
                && UseNvidiaAcceleration
                && !EnableRifeInterpolation
                && !EnableUpscale
                && !EnablePreFilter
                && !EnableSubjectFocus
                && !Generate4K
                && !GenerateTikTok60Ultra
                && !GenerateTikTok30
                && GeneratePc60
                && PcFps == 60
                && originalFps >= 59.0
                && originalFps <= 60.1
                && !EnableEnhanceFilters;
        }

        private async Task<ProcessExecutionResult> EncodeGpuZeroCopyAsync(
            string inputPath,
            string outputPath,
            bool showCmd,
            Action<string>? cb,
            CancellationToken ct,
            double? durationSeconds)
        {
            string partialPath = outputPath + ".forgeflow.partial." + Guid.NewGuid().ToString("N") + ".mp4";
            var args = new StringBuilder();
            args.Append("-y -nostdin -hwaccel cuda -hwaccel_output_format cuda ");
            args.Append($"-i {QuotePath(inputPath)} -map 0:v:0 -map 0:a:0? ");
            args.Append("-c:v h264_nvenc -preset p5 -tune hq ");
            if (HasBitrate(PcBitrate))
                args.Append($"-rc vbr {GetBitrateRateControl(PcBitrate, PcMaxRate, PcBufSize)} ");
            else
                args.Append($"-rc vbr -cq {Math.Clamp(PcCrf, 1, 30)} -b:v 0 ");
            args.Append("-r 60 -fps_mode cfr -g 60 -keyint_min 30 -bf 3 ");
            args.Append($"{AudioArgs} -map_metadata -1 -map_chapters -1 -movflags +faststart ");
            args.Append("-loglevel warning -progress pipe:1 -nostats ");
            args.Append(QuotePath(partialPath));
            try
            {
                ProcessExecutionResult result = await RunProcessAsync(
                    FfmpegExecutablePath,
                    args.ToString(),
                    showCmd,
                    _tempSessionDirectory,
                    CreateTimedProgressCallback(cb, 0, 100, durationSeconds, "GPU Zero-Copy 60FPS"),
                    ct).ConfigureAwait(false);
                if (!result.Success)
                    return result;
                if (!File.Exists(partialPath) || new FileInfo(partialPath).Length < 1024)
                    return new ProcessExecutionResult(-1, result.StandardOutput, "GPU Zero-Copy completed without a valid output file.");

                (bool valid, string validation) = await ValidateTikTokPreservationOutputAsync(
                    partialPath,
                    durationSeconds,
                    ct).ConfigureAwait(false);
                if (!valid)
                    return new ProcessExecutionResult(-1, result.StandardOutput, "GPU Zero-Copy validation failed: " + validation);
                cb?.Invoke("GPU Zero-Copy validation: PASS — " + validation);

                File.Move(partialPath, outputPath, true);
                return result;
            }
            finally
            {
                try
                {
                    if (File.Exists(partialPath)) File.Delete(partialPath);
                }
                catch
                {
                }
            }
        }

        private string GetEffectiveTikTokRateControl(bool forceCpu = false)
        {
            // Keep backward compatibility if older code sets the full rate-control string manually.
            if (!string.IsNullOrWhiteSpace(TikTokRateControl))
                return TikTokRateControl.Trim();

            if (HasBitrate(TikTokTargetBitrate))
                return !forceCpu && UseAmdAcceleration
                    ? GetAmdBitrateRateControl(TikTokTargetBitrate, TikTokMaxRate, TikTokBufSize)
                    : GetBitrateRateControl(TikTokTargetBitrate, TikTokMaxRate, TikTokBufSize);

            return $"-crf {TikTokCrf} -maxrate {TikTokMaxRate} -bufsize {TikTokBufSize}";
        }

        private static (int Start, int End) GetStageProgressRange(int stageNumber, int totalStages)
        {
            totalStages = Math.Max(1, totalStages);
            stageNumber = Math.Clamp(stageNumber, 1, totalStages);
            int start = (int)Math.Round(((stageNumber - 1) * 100.0) / totalStages);
            int end = (int)Math.Round((stageNumber * 100.0) / totalStages);
            return (Math.Clamp(start, 0, 100), Math.Clamp(Math.Max(start + 1, end), 0, 100));
        }

        private string FilterGraphMild()
        {
            if (!EnableEnhanceFilters)
                return "setsar=1";

            double strength = Math.Clamp(EnhanceFilterStrength, 0.0, 1.0);
            string style = (EnhanceFilterStyle ?? "natural").Trim().ToLowerInvariant();

            if (style == "game-detail")
            {
                // Fast, conservative detail pass for already-clear gameplay.
                // It avoids temporal AI hallucination and keeps HUD/text stable.
                double cas = 0.32 + (0.38 * strength);
                double sharp = 0.18 + (0.42 * strength);
                double gameContrast = 1.008 + (0.018 * strength);
                return "setsar=1," +
                       $"eq=contrast={FormatFilterNumber(gameContrast)}:brightness=0:saturation=1.01:gamma=1," +
                       $"cas=strength={FormatFilterNumber(cas)}," +
                       $"unsharp=5:5:{FormatFilterNumber(sharp)}:5:5:0";
            }

            if (style == "game-detail-pro")
            {
                // Experimental local-contrast/detail pass for gameplay that has already
                // been compressed by a capture card or social platform.  This is not an
                // AI hallucination pass: it removes only light ringing/noise, then applies
                // bounded CAS + unsharp so HUD text remains stable.  Keep CAS <= 1.0.
                double proStrength = Math.Clamp(strength, 0.0, 1.0);
                double deblock = 0.028 + (0.028 * proStrength);
                double proDenoiseLuma = 0.24 + (0.16 * proStrength);
                double proDenoiseChroma = 0.18 + (0.12 * proStrength);
                double proContrast = 1.024 + (0.018 * proStrength);
                double proSaturation = 1.018 + (0.018 * proStrength);
                double proCas = 0.72 + (0.20 * proStrength);
                double proSharp = 0.34 + (0.22 * proStrength);
                return "setsar=1," +
                       $"deblock=filter=weak:block=8:alpha={FormatFilterNumber(deblock)}:beta={FormatFilterNumber(deblock * 0.55)}:gamma={FormatFilterNumber(deblock * 0.42)}:delta={FormatFilterNumber(deblock * 0.42)}," +
                       $"hqdn3d={FormatFilterNumber(proDenoiseLuma)}:{FormatFilterNumber(proDenoiseChroma)}:1.4:1.0," +
                       $"eq=contrast={FormatFilterNumber(proContrast)}:brightness=0.001:saturation={FormatFilterNumber(proSaturation)}:gamma=0.997," +
                       $"cas=strength={FormatFilterNumber(proCas)}," +
                       $"unsharp=5:5:{FormatFilterNumber(proSharp)}:5:5:0";
            }

            if (style == "tiktok-detail")
            {
                double detailContrast = 1.035 + (0.020 * strength);
                double detailSaturation = 1.04 + (0.040 * strength);
                double detailGamma = 0.995 - (0.010 * strength);
                double detailCas = 0.72 + (0.24 * strength);
                return "setsar=1," +
                       "hqdn3d=0.35:0.25:1.0:0.8," +
                       $"eq=contrast={FormatFilterNumber(detailContrast)}:brightness=0.002:saturation={FormatFilterNumber(detailSaturation)}:gamma={FormatFilterNumber(detailGamma)}," +
                       $"cas=strength={FormatFilterNumber(detailCas)}";
            }

            if (style == "gaming-restore")
            {
                GamingRestoreProfile profile = GetGamingRestoreProfile(strength);
                return "setsar=1," +
                       $"deblock=filter=weak:block=8:alpha={FormatFilterNumber(profile.Deblock)}:beta={FormatFilterNumber(profile.Deblock * 0.55)}:gamma={FormatFilterNumber(profile.Deblock * 0.42)}:delta={FormatFilterNumber(profile.Deblock * 0.42)}," +
                       $"hqdn3d={FormatFilterNumber(profile.Denoise)}:{FormatFilterNumber(profile.Denoise * 0.72)}:{FormatFilterNumber(profile.Denoise * 2.15)}:{FormatFilterNumber(profile.Denoise * 1.65)}," +
                       $"eq=contrast={FormatFilterNumber(profile.Contrast)}:brightness=0.001:saturation={FormatFilterNumber(profile.Saturation)}:gamma={FormatFilterNumber(profile.Gamma)}," +
                       $"cas=strength={FormatFilterNumber(profile.Cas)}";
            }

            double contrast = 1.06;
            double brightness = 0.006;
            double saturation = 1.05;
            double gamma = 1.0;
            double sharpness = 0.78;
            double denoiseLuma = 0.9;
            double denoiseChroma = 0.7;

            if (style == "tiktok-vivid" || style == "aesthetic-vivid")
            {
                contrast = 1.06 + (0.09 * strength);
                brightness = 0.004 + (0.006 * strength);
                saturation = 1.08 + (0.32 * strength);
                gamma = 0.995 - (0.035 * strength);
                sharpness = 0.72 + (0.36 * strength);
                denoiseLuma = 0.65;
                denoiseChroma = 0.55;
            }
            else if (style == "tiktok-vivid-plus" || style == "aesthetic-vivid-plus")
            {
                contrast = 1.08 + (0.12 * strength);
                brightness = 0.004 + (0.008 * strength);
                saturation = 1.12 + (0.42 * strength);
                gamma = 0.99 - (0.045 * strength);
                sharpness = 0.84 + (0.42 * strength);
                denoiseLuma = 0.55;
                denoiseChroma = 0.50;
            }

            return "setsar=1," +
                   $"hqdn3d={FormatFilterNumber(denoiseLuma)}:{FormatFilterNumber(denoiseChroma)}:2.0:1.7," +
                   "deband=1thr=0.010:2thr=0.010:3thr=0.010:4thr=0.010:range=12:blur=0," +
                   $"eq=contrast={FormatFilterNumber(contrast)}:brightness={FormatFilterNumber(brightness)}:saturation={FormatFilterNumber(saturation)}:gamma={FormatFilterNumber(gamma)}," +
                   $"unsharp=5:5:{FormatFilterNumber(sharpness)}";
        }

        private static string FormatFilterNumber(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private GamingRestoreProfile GetGamingRestoreProfile(double requestedStrength)
        {
            double estimatedBitrate = SourceDurationSeconds > 0
                ? (SourceSizeBytes * 8.0) / SourceDurationSeconds
                : 0;
            double pixelsPerSecond = Math.Max(1.0, SourceWidth * (double)SourceHeight * Math.Max(1.0, SourceFps));
            double bitsPerPixel = estimatedBitrate > 0 ? estimatedBitrate / pixelsPerSecond : 0.12;

            // Lower-bitrate gameplay receives stronger cleanup. High-bitrate captures stay mild
            // so HUD text and hard game edges are not damaged by unnecessary restoration.
            double compressionNeed = Math.Clamp((0.22 - bitsPerPixel) / 0.18, 0.0, 1.0);
            double adaptive = Math.Clamp((requestedStrength * 0.68) + (compressionNeed * 0.32), 0.20, 1.0);
            return new GamingRestoreProfile(
                Deblock: 0.018 + (0.050 * adaptive),
                Denoise: 0.22 + (0.48 * adaptive),
                Contrast: 1.018 + (0.026 * adaptive),
                Saturation: 1.018 + (0.038 * adaptive),
                Gamma: 0.999 - (0.010 * adaptive),
                Cas: 0.54 + (0.34 * adaptive));
        }

        private string GetGamingRestoreAnalysisMessage()
        {
            double bitrateMbps = SourceDurationSeconds > 0
                ? (SourceSizeBytes * 8.0 / SourceDurationSeconds) / 1_000_000.0
                : 0;
            double pixelsPerSecond = Math.Max(1.0, SourceWidth * (double)SourceHeight * Math.Max(1.0, SourceFps));
            double bitsPerPixel = bitrateMbps > 0 ? (bitrateMbps * 1_000_000.0) / pixelsPerSecond : 0.12;
            string level = bitsPerPixel < 0.09 ? "strong" : bitsPerPixel < 0.18 ? "balanced" : "light";
            return $"Gaming Restore analysis: {SourceWidth}x{SourceHeight}, {SourceFps:0.##} FPS, ~{bitrateMbps:0.#} Mbps, {bitsPerPixel:0.###} bpp -> {level} adaptive restoration.";
        }

        private readonly record struct GamingRestoreProfile(
            double Deblock,
            double Denoise,
            double Contrast,
            double Saturation,
            double Gamma,
            double Cas);

        private bool UseNvidiaAcceleration => string.Equals(HardwareAccelerationMode, "nvidia", StringComparison.OrdinalIgnoreCase);
        private bool UseAmdAcceleration => string.Equals(HardwareAccelerationMode, "amd", StringComparison.OrdinalIgnoreCase);

        private string GetPcVideoEncoderArgs(bool forceCpu = false)
        {
            bool useBitrate = HasBitrate(PcBitrate);
            if (!forceCpu && UseNvidiaAcceleration)
            {
                if (UseNvidiaHevcEncoder)
                    return useBitrate
                        ? $"-c:v hevc_nvenc -preset {MapNvencPreset(PcPreset)} -profile:v main -pix_fmt yuv420p {GetBitrateRateControl(PcBitrate, PcMaxRate, PcBufSize)}"
                        : $"-c:v hevc_nvenc -preset {MapNvencPreset(PcPreset)} -profile:v main -pix_fmt yuv420p -cq {Math.Max(1, PcCrf)}";

                return useBitrate
                    ? $"-c:v h264_nvenc -preset {MapNvencPreset(PcPreset)} -profile:v high -pix_fmt yuv420p {GetBitrateRateControl(PcBitrate, PcMaxRate, PcBufSize)}"
                    : $"-c:v h264_nvenc -preset {MapNvencPreset(PcPreset)} -profile:v high -pix_fmt yuv420p -cq {Math.Max(1, PcCrf)}";
            }

            if (!forceCpu && UseAmdAcceleration)
            {
                if (UseAmdHevcEncoder)
                    return useBitrate
                        ? $"-c:v hevc_amf -quality {MapAmfQuality(PcPreset)} -profile:v main -pix_fmt yuv420p {GetAmdBitrateRateControl(PcBitrate, PcMaxRate, PcBufSize)}"
                        : $"-c:v hevc_amf -quality {MapAmfQuality(PcPreset)} -profile:v main -pix_fmt yuv420p -rc cqp -qp_i {Math.Max(1, PcCrf)} -qp_p {Math.Max(1, PcCrf)}";

                return useBitrate
                    ? $"-c:v h264_amf -quality {MapAmfQuality(PcPreset)} -profile:v high -pix_fmt yuv420p {GetAmdBitrateRateControl(PcBitrate, PcMaxRate, PcBufSize)}"
                    : $"-c:v h264_amf -quality {MapAmfQuality(PcPreset)} -profile:v high -pix_fmt yuv420p -rc cqp -qp_i {Math.Max(1, PcCrf)} -qp_p {Math.Max(1, PcCrf)}";
            }

            return useBitrate
                ? $"-c:v libx264 -preset {PcPreset} -tune {PcTune} -profile:v high -pix_fmt yuv420p {GetBitrateRateControl(PcBitrate, PcMaxRate, PcBufSize)}"
                : $"-c:v libx264 -crf {PcCrf} -preset {PcPreset} -tune {PcTune} -profile:v high -pix_fmt yuv420p";
        }

        private string GetTikTokVideoEncoderArgs(bool forceCpu = false)
        {
            bool useBitrate = HasBitrate(TikTokTargetBitrate);
            if (!forceCpu && UseNvidiaAcceleration)
            {
                if (UseNvidiaHevcEncoder)
                    return useBitrate
                        ? $"-c:v hevc_nvenc -preset {MapNvencPreset(TikTokPreset)} -profile:v main -pix_fmt yuv420p"
                        : $"-c:v hevc_nvenc -preset {MapNvencPreset(TikTokPreset)} -profile:v main -pix_fmt yuv420p -cq {Math.Max(1, TikTokCrf)}";

                return useBitrate
                    ? $"-c:v h264_nvenc -preset {MapNvencPreset(TikTokPreset)} -profile:v high -pix_fmt yuv420p"
                    : $"-c:v h264_nvenc -preset {MapNvencPreset(TikTokPreset)} -profile:v high -pix_fmt yuv420p -cq {Math.Max(1, TikTokCrf)}";
            }

            if (!forceCpu && UseAmdAcceleration)
                return useBitrate
                    ? $"-c:v h264_amf -quality {MapAmfQuality(TikTokPreset)} -profile:v high -pix_fmt yuv420p"
                    : $"-c:v h264_amf -quality {MapAmfQuality(TikTokPreset)} -profile:v high -pix_fmt yuv420p -rc cqp -qp_i {Math.Max(1, TikTokCrf)} -qp_p {Math.Max(1, TikTokCrf)}";

            return useBitrate
                ? $"-c:v libx264 -preset {TikTokPreset} -tune film -profile:v high -pix_fmt yuv420p"
                : $"-c:v libx264 -preset {TikTokPreset} -tune film -profile:v high -pix_fmt yuv420p";
        }

        private static bool HasBitrate(string? value)
        {
            return !string.IsNullOrWhiteSpace(value) && !string.Equals(value.Trim(), "0", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetBitrateRateControl(string bitrate, string maxRate, string bufSize)
        {
            // Keep this encoder-neutral: libx264 does not accept -rc, while
            // NVENC/AMF select VBR automatically from b/maxrate/bufsize.
            return $"-b:v {bitrate} -maxrate {maxRate} -bufsize {bufSize}";
        }

        private static string GetAmdBitrateRateControl(string bitrate, string maxRate, string bufSize)
        {
            // AMF's VBR peak + VBAQ/pre-encode path is the AMD-recommended
            // quality configuration for high-motion gameplay.  Keep it isolated
            // from NVENC/libx264 because these flags are encoder-specific.
            return $"-rc vbr_peak -b:v {bitrate} -maxrate {maxRate} -bufsize {bufSize} -vbaq true -preencode true -high_motion_quality_boost_enable true";
        }

        private static string MapNvencPreset(string preset)
        {
            return preset.ToLowerInvariant() switch
            {
                "fast" => "p4",
                "slow" => "p6",
                "veryslow" => "p7",
                _ => "p5"
            };
        }

        private static string MapAmfQuality(string preset)
        {
            return preset.ToLowerInvariant() switch
            {
                "fast" => "speed",
                "slow" => "balanced",
                "veryslow" => "quality",
                _ => "balanced"
            };
        }

        private async Task<ProcessExecutionResult> EncodePcAsync(
            string outputPath,
            string framesDir,
            string pattern,
            double rifeFps,
            string originalVideoPath,
            double? audioOffset,
            bool showCmd,
            Action<string>? cb,
            CancellationToken ct,
            double? durationSeconds,
            int progressStart,
            int progressEnd)
        {
            int outFps = PcFps;
            int g = Math.Max(1, outFps * Math.Max(1, PcKeyintSeconds));
            string vf = FilterGraphMild() + $",fps={outFps}";

            var args = new StringBuilder();
            args.Append("-y -nostdin ");

            args.Append($"-framerate {rifeFps.ToString(CultureInfo.InvariantCulture)} -i {QuotePath(Path.Combine(framesDir, pattern))} ");
            if (audioOffset.HasValue && audioOffset.Value > 0)
                args.Append($"-itsoffset {audioOffset.Value.ToString(CultureInfo.InvariantCulture)} ");
            args.Append($"-i {QuotePath(originalVideoPath)} ");
            args.Append("-map 0:v:0 -map 1:a? ");

            args.Append($"-vf \"{vf}\" ");
            args.Append(GetPcVideoEncoderArgs() + " ");
            args.Append($"-g {g} -keyint_min {g} -sc_threshold 0 ");
            args.Append($"{AudioArgs} ");
            args.Append("-map_metadata -1 -map_chapters -1 -movflags +faststart -loglevel warning -progress pipe:1 -nostats ");
            args.Append(QuotePath(outputPath));

            return await RunProcessAsync(FfmpegExecutablePath, args.ToString(), showCmd, _tempSessionDirectory, CreateTimedProgressCallback(cb, progressStart, progressEnd, durationSeconds, "Video 60FPS"), ct).ConfigureAwait(false);
        }

        private (int Width, int Height) GetFourKDimensions()
        {
            // Keep the delivery orientation.  A portrait source becomes the
            // portrait 4K canvas instead of being rotated or padded to 16:9.
            return SourceHeight > SourceWidth ? (2160, 3840) : (3840, 2160);
        }

        private bool CanSourcePreserveFourK(double sourceFps, string inputPath)
        {
            var (width, height) = GetFourKDimensions();
            return SourceWidth == width && SourceHeight == height && sourceFps >= 59.0 && sourceFps <= 60.1
                   && !EnableRifeInterpolation && !EnableUpscale && !EnablePreFilter
                   && (!EnableSubjectFocus || SubjectFocusMode == SubjectFocusMode.Off)
                   && (!EnableEnhanceFilters || string.Equals(EnhanceFilterStyle, "natural", StringComparison.OrdinalIgnoreCase))
                   && string.Equals(Path.GetExtension(inputPath), ".mp4", StringComparison.OrdinalIgnoreCase);
        }

        private async Task<ProcessExecutionResult> CopySourcePreservingAsync(
            string inputPath, string outputPath, bool showCmd, Action<string>? cb, CancellationToken ct, double? durationSeconds)
        {
            var args = $"-y -nostdin -i {QuotePath(inputPath)} -map 0 -c copy -movflags +faststart -progress pipe:1 -nostats {QuotePath(outputPath)}";
            ProcessExecutionResult result = await RunProcessAsync(FfmpegExecutablePath, args, showCmd, _tempSessionDirectory,
                CreateTimedProgressCallback(cb, 0, 100, durationSeconds, "Source-Preserving 4K"), ct).ConfigureAwait(false);
            if (result.Success && (!File.Exists(outputPath) || new FileInfo(outputPath).Length < 1024))
                return new ProcessExecutionResult(-1, result.StandardOutput, "Source-preserving export completed without a valid output file.");
            return result;
        }

        private string GetFourKVideoEncoderArgs(bool forceCpu)
        {
            bool useBitrate = HasBitrate(FourKBitrate);
            if (!forceCpu && UseNvidiaAcceleration)
            {
                return useBitrate
                    ? $"-c:v hevc_nvenc -preset p6 -tune hq -profile:v main -pix_fmt yuv420p {GetBitrateRateControl(FourKBitrate, FourKMaxRate, FourKBufSize)}"
                    : $"-c:v hevc_nvenc -preset p6 -tune hq -profile:v main -pix_fmt yuv420p -cq {Math.Clamp(FourKCrf, 1, 30)}";
            }

            if (!forceCpu && UseAmdAcceleration)
            {
                return useBitrate
                    ? $"-c:v hevc_amf -quality quality -profile:v main -pix_fmt yuv420p {GetAmdBitrateRateControl(FourKBitrate, FourKMaxRate, FourKBufSize)}"
                    : $"-c:v hevc_amf -quality quality -profile:v main -pix_fmt yuv420p -rc cqp -qp_i {Math.Clamp(FourKCrf, 1, 30)} -qp_p {Math.Clamp(FourKCrf, 1, 30)}";
            }

            return useBitrate
                ? $"-c:v libx265 -preset medium -x265-params crf={Math.Clamp(FourKCrf, 1, 30)}:aq-mode=3 {GetBitrateRateControl(FourKBitrate, FourKMaxRate, FourKBufSize)}"
                : $"-c:v libx265 -preset medium -crf {Math.Clamp(FourKCrf, 1, 30)}";
        }

        private async Task<ProcessExecutionResult> Encode4KWithFallbackAsync(
            string outputPath,
            string framesDir,
            string pattern,
            double sourceFps,
            string originalVideoPath,
            double? audioOffset,
            bool showCmd,
            Action<string>? cb,
            CancellationToken ct,
            double? durationSeconds,
            int progressStart,
            int progressEnd)
        {
            var result = await Encode4KAsync(outputPath, framesDir, pattern, sourceFps, originalVideoPath, audioOffset,
                showCmd, cb, ct, durationSeconds, progressStart, progressEnd, forceCpu: false).ConfigureAwait(false);
            if (result.Success || (!UseNvidiaAcceleration && !UseAmdAcceleration))
                return result;

            string combined = (result.StandardError + "\n" + result.StandardOutput).ToLowerInvariant();
            bool hardwareFailure = combined.Contains("no capable devices") || combined.Contains("invalid argument")
                                   || combined.Contains("nvenc") || combined.Contains("amf")
                                   || combined.Contains("external library") || combined.Contains("device");
            if (!hardwareFailure)
                return result;

            cb?.Invoke("WARNING: 4K hardware encoder failed; falling back to CPU HEVC.");
            return await Encode4KAsync(outputPath, framesDir, pattern, sourceFps, originalVideoPath, audioOffset,
                showCmd, cb, ct, durationSeconds, progressStart, progressEnd, forceCpu: true).ConfigureAwait(false);
        }

        private async Task<ProcessExecutionResult> Encode4KAsync(
            string outputPath,
            string framesDir,
            string pattern,
            double sourceFps,
            string originalVideoPath,
            double? audioOffset,
            bool showCmd,
            Action<string>? cb,
            CancellationToken ct,
            double? durationSeconds,
            int progressStart,
            int progressEnd,
            bool forceCpu)
        {
            var (width, height) = GetFourKDimensions();
            int fps = Math.Max(1, PcFps);
            int g = Math.Max(1, fps * Math.Max(1, PcKeyintSeconds));
            // Fill the selected 4K canvas while preserving orientation.  This
            // avoids black bars for normal 16:9 or 9:16 captures.
            string vf = $"scale={width}:{height}:force_original_aspect_ratio=increase,crop={width}:{height},setsar=1,fps={fps}";
            var args = new StringBuilder();
            args.Append("-y -nostdin ");
            args.Append($"-framerate {sourceFps.ToString(CultureInfo.InvariantCulture)} -i {QuotePath(Path.Combine(framesDir, pattern))} ");
            if (audioOffset.HasValue && audioOffset.Value > 0)
                args.Append($"-itsoffset {audioOffset.Value.ToString(CultureInfo.InvariantCulture)} ");
            args.Append($"-i {QuotePath(originalVideoPath)} -map 0:v:0 -map 1:a? -vf \"{vf}\" ");
            args.Append(GetFourKVideoEncoderArgs(forceCpu) + " ");
            args.Append($"-g {g} -keyint_min {g} -sc_threshold 0 -c:a aac -b:a {Math.Clamp(FourKAudioKbps, 128, 512)}k -ar 48000 -ac 2 ");
            args.Append("-map_metadata -1 -map_chapters -1 -movflags +faststart -loglevel warning -progress pipe:1 -nostats ");
            args.Append(QuotePath(outputPath));
            return await RunProcessAsync(FfmpegExecutablePath, args.ToString(), showCmd, _tempSessionDirectory,
                CreateTimedProgressCallback(cb, progressStart, progressEnd, durationSeconds, "4K Ultra"), ct).ConfigureAwait(false);
        }

        private async Task<ProcessExecutionResult> EncodePcWithFallbackAsync(
            string outputPath,
            string framesDir,
            string pattern,
            double rifeFps,
            string originalVideoPath,
            double? audioOffset,
            bool showCmd,
            Action<string>? cb,
            CancellationToken ct,
            double? durationSeconds,
            int progressStart,
            int progressEnd)
        {
            var result = await EncodePcAsync(outputPath, framesDir, pattern, rifeFps, originalVideoPath, audioOffset, showCmd, cb, ct, durationSeconds, progressStart, progressEnd).ConfigureAwait(false);
            if (result.Success || (!UseNvidiaAcceleration && !UseAmdAcceleration))
                return result;

            string combined = (result.StandardError + "\n" + result.StandardOutput).ToLowerInvariant();
            bool hardwareFailure = combined.Contains("no capable devices found") ||
                                  combined.Contains("invalid argument") ||
                                  combined.Contains("generic error in an external library") ||
                                  combined.Contains("device") ||
                                  combined.Contains("nvenc") ||
                                  combined.Contains("amf");
            if (!hardwareFailure)
                return result;

            cb?.Invoke("WARNING: Hardware encoder failed for Video 60FPS. Falling back to CPU encoder.");
            return await EncodePcCpuAsync(outputPath, framesDir, pattern, rifeFps, originalVideoPath, audioOffset, showCmd, cb, ct, durationSeconds, progressStart, progressEnd).ConfigureAwait(false);
        }

        private async Task<ProcessExecutionResult> EncodePcCpuAsync(
            string outputPath,
            string framesDir,
            string pattern,
            double rifeFps,
            string originalVideoPath,
            double? audioOffset,
            bool showCmd,
            Action<string>? cb,
            CancellationToken ct,
            double? durationSeconds,
            int progressStart,
            int progressEnd)
        {
            int outFps = PcFps;
            int g = Math.Max(1, outFps * Math.Max(1, PcKeyintSeconds));
            string vf = FilterGraphMild() + $",fps={outFps}";

            var args = new StringBuilder();
            args.Append("-y -nostdin ");
            args.Append($"-framerate {rifeFps.ToString(CultureInfo.InvariantCulture)} -i {QuotePath(Path.Combine(framesDir, pattern))} ");
            if (audioOffset.HasValue && audioOffset.Value > 0)
                args.Append($"-itsoffset {audioOffset.Value.ToString(CultureInfo.InvariantCulture)} ");
            args.Append($"-i {QuotePath(originalVideoPath)} ");
            args.Append("-map 0:v:0 -map 1:a? ");
            args.Append($"-vf \"{vf}\" ");
            args.Append(GetPcVideoEncoderArgs(forceCpu: true) + " ");
            args.Append($"-g {g} -keyint_min {g} -sc_threshold 0 ");
            args.Append($"{AudioArgs} ");
            args.Append("-map_metadata -1 -map_chapters -1 -movflags +faststart -loglevel warning -progress pipe:1 -nostats ");
            args.Append(QuotePath(outputPath));

            return await RunProcessAsync(FfmpegExecutablePath, args.ToString(), showCmd, _tempSessionDirectory, CreateTimedProgressCallback(cb, progressStart, progressEnd, durationSeconds, "Video 60FPS"), ct).ConfigureAwait(false);
        }

        private string GetTikTok60UltraEncoderArgs(bool forceCpu, bool compatibilityMode)
        {
            int quality = Math.Clamp(TikTok60UltraCrf, 1, 30);
            string h264Level = GetTikTokH264Level();
            if (!forceCpu && UseNvidiaAcceleration)
            {
                // HQ settings are useful for game captures even when the visual
                // style is Natural: lookahead/AQ spend bits on HUD edges and
                // fast motion instead of flat areas.  The compatibility retry
                // deliberately drops only the optional knobs for older drivers.
                string advanced = !compatibilityMode
                    ? " -rc-lookahead 32 -lookahead_level 2 -spatial_aq 1 -aq-strength 12 -temporal_aq 1 -b_ref_mode middle"
                    : "";
                // Level 5.1 is required for the 70/120 Mbps Studio tier; Level
                // 4.2 rejects that VBV ceiling even though the frame size is
                // nominally 1080p60.
                return $"-c:v h264_nvenc -preset {MapNvencPreset(TikTok60UltraPreset)} -tune hq -profile:v high -level:v {h264Level} -pix_fmt yuv420p -rc vbr -multipass fullres -cq {quality} -b:v {TikTok60UltraBitrate} -maxrate {TikTok60UltraMaxRate} -bufsize {TikTok60UltraBufSize}{advanced}";
            }

            if (!forceCpu && UseAmdAcceleration)
                return $"-c:v h264_amf -quality {MapAmfQuality(TikTok60UltraPreset)} -profile:v high -level:v {h264Level} -pix_fmt yuv420p -rc hqvbr -qvbr_quality_level {quality} -b:v {TikTok60UltraBitrate} -maxrate {TikTok60UltraMaxRate} -bufsize {TikTok60UltraBufSize}";

            return $"-c:v libx264 -preset {TikTok60UltraPreset} -tune film -profile:v high -level:v {h264Level} -pix_fmt yuv420p -b:v {TikTok60UltraBitrate} -maxrate {TikTok60UltraMaxRate} -bufsize {TikTok60UltraBufSize}";
        }

        private string GetTikTokH264Level()
        {
            // H.264 level is constrained by macroblocks per frame *and* per
            // second. Calculate it from the actual delivery frame after
            // upscaling and the TikTok delivery scale, not the source frame.
            (int width, int height) = GetTikTokDeliveryDimensions();
            double fps = 60.0;
            long mbPerFrame = ((width + 15L) / 16L) * ((height + 15L) / 16L);
            double mbPerSecond = mbPerFrame * fps;
            if (mbPerFrame <= 36864 && mbPerSecond <= 983040) return "5.1";
            if (mbPerFrame <= 36864 && mbPerSecond <= 2073600) return "5.2";
            if (mbPerFrame <= 139264 && mbPerSecond <= 4177920) return "6.0";
            if (mbPerFrame <= 139264 && mbPerSecond <= 8355840) return "6.1";
            return "6.2";
        }

        private (int Width, int Height) GetTikTokDeliveryDimensions()
        {
            double width = Math.Max(16, SourceWidth > 0 ? SourceWidth : 1920);
            double height = Math.Max(16, SourceHeight > 0 ? SourceHeight : 1080);
            if (EnableUpscale && UpscaleScale > 1)
            {
                width *= UpscaleScale;
                height *= UpscaleScale;
            }

            if (string.Equals(EnhanceFilterStyle, "gaming-restore", StringComparison.OrdinalIgnoreCase))
            {
                double maxWidth = width >= height ? 1920 : 1080;
                double maxHeight = width >= height ? 1080 : 1920;
                double factor = Math.Min(1.0, Math.Min(maxWidth / width, maxHeight / height));
                width *= factor;
                height *= factor;
            }
            else
            {
                // TikTok60Ultra is deliberately a 1080 delivery profile. Fit
                // the source inside a 1920x1080 landscape or 1080x1920
                // portrait canvas without upscaling small sources.
                double targetWidth = width >= height ? 1920.0 : 1080.0;
                double targetHeight = width >= height ? 1080.0 : 1920.0;
                double factor = Math.Min(1.0, Math.Min(targetWidth / width, targetHeight / height));
                width *= factor;
                height *= factor;
            }

            int evenWidth = Math.Max(16, (int)Math.Round(width / 2.0, MidpointRounding.ToEven) * 2);
            int evenHeight = Math.Max(16, (int)Math.Round(height / 2.0, MidpointRounding.ToEven) * 2);
            return (evenWidth, evenHeight);
        }

        private bool IsTikTok1080CompatibleSource()
        {
            if (SourceWidth <= 0 || SourceHeight <= 0)
                return false;

            bool landscape = SourceWidth >= SourceHeight;
            int maxWidth = landscape ? 1920 : 1080;
            int maxHeight = landscape ? 1080 : 1920;
            return SourceWidth <= maxWidth && SourceHeight <= maxHeight;
        }

        private async Task<ProcessExecutionResult> EncodeTikTok60TurboAsync(
            string outputPath,
            string originalVideoPath,
            bool showCmd,
            Action<string>? cb,
            CancellationToken ct,
            double? durationSeconds,
            int progressStart,
            int progressEnd)
        {
            var args = new StringBuilder();
            args.Append("-y -nostdin ");
            args.Append($"-i {QuotePath(originalVideoPath)} ");
            args.Append("-map 0:v:0 -map 0:a:0? ");
            if (EnableTikTokAudioDecoy) args.Append("-map 0:a:0? ");
            // A compliant source (H.264 + AAC) should keep both streams intact.
            // Re-encoding the audio here can force an unnecessary full remux and
            // was the reason some native-60 captures fell back to the studio encode.
            args.Append("-c:v copy -c:a copy ");
            if (EnableTikTokAudioDecoy)
                args.Append("-disposition:a:0 default -disposition:a:1 0 -metadata:s:a:0 title=Main -metadata:s:a:1 title=TikTokCompatibility ");
            args.Append("-map_metadata -1 -map_chapters -1 -avoid_negative_ts make_zero ");
            // Keep moov after mdat while the video sample-table patch is enabled;
            // this avoids rewriting every original chunk offset and preserves the
            // copied video bitstream. Normal Turbo exports retain fast-start.
            if (!EnableTikTokVideoInflate)
                args.Append("-movflags +faststart ");
            args.Append("-loglevel warning -progress pipe:1 -nostats ");
            args.Append(QuotePath(outputPath));

            return await RunProcessAsync(FfmpegExecutablePath, args.ToString(), showCmd, _tempSessionDirectory,
                CreateTimedProgressCallback(cb, progressStart, progressEnd, durationSeconds, "TikTok Turbo Copy"), ct).ConfigureAwait(false);
        }

        private async Task<ProcessExecutionResult> EncodeTikTok60UltraAsync(
            string outputPath,
            string framesDir,
            string pattern,
            double sourceSequenceFps,
            string originalVideoPath,
            double? audioOffset,
            bool forceCpu,
            bool compatibilityMode,
            bool showCmd,
            Action<string>? cb,
            CancellationToken ct,
            double? durationSeconds,
            int progressStart,
            int progressEnd)
        {
            const string outputRate = "60000/1001";
            // Two-second GOP is a practical upload cadence for 59.94/60fps and
            // keeps random access/segmenting predictable on TikTok's ingest.
            const int keyint = 120;
            // TikTok60Ultra is always delivered at a 1080-class canvas. The
            // orientation-aware limits avoid black bars and prevent a 4K
            // source from bypassing the intended upload profile.
            string deliveryScale = ",scale=w='if(gte(iw\\,ih)\\,min(iw\\,1920)\\,min(iw\\,1080))':h='if(gte(iw\\,ih)\\,min(ih\\,1080)\\,min(ih\\,1920))':force_original_aspect_ratio=decrease:force_divisible_by=2:flags=lanczos";
            string vf = FilterGraphMild() + deliveryScale +
                        $",setparams=range=limited:color_primaries=bt709:color_trc=bt709:colorspace=bt709,fps={outputRate}";
            (int finalWidth, int finalHeight) = GetTikTokDeliveryDimensions();
            cb?.Invoke($"TikTok final frame: {finalWidth}x{finalHeight}; H.264 level {GetTikTokH264Level()}.");

            var args = new StringBuilder();
            args.Append("-y -nostdin ");
            args.Append($"-framerate {sourceSequenceFps.ToString(CultureInfo.InvariantCulture)} -i {QuotePath(Path.Combine(framesDir, pattern))} ");
            if (audioOffset.HasValue && audioOffset.Value > 0)
                args.Append($"-itsoffset {audioOffset.Value.ToString(CultureInfo.InvariantCulture)} ");
            args.Append($"-i {QuotePath(originalVideoPath)} ");
            args.Append("-map 0:v:0 -map 1:a:0? ");
            if (EnableTikTokAudioDecoy) args.Append("-map 1:a:0? ");
            args.Append($"-vf \"{vf}\" ");
            args.Append(GetTikTok60UltraEncoderArgs(forceCpu, compatibilityMode) + " ");
            args.Append($"-r {outputRate} -fps_mode cfr -g {keyint} -keyint_min {keyint / 2} -sc_threshold 0 -bf 3 ");
            // Explicit SDR signalling prevents an HDR/colour-management branch
            // from being selected by the ingest service for game captures.
            args.Append("-tag:v avc1 -color_primaries bt709 -color_trc bt709 -colorspace bt709 -color_range tv ");
            args.Append($"-c:a aac -b:a {Math.Clamp(TikTok60UltraAudioKbps, 96, 512)}k -ar 48000 -ac 2 ");
            if (EnableTikTokAudioDecoy)
                args.Append("-disposition:a:0 default -disposition:a:1 0 -metadata:s:a:0 title=Main -metadata:s:a:1 title=TikTokCompatibility ");
            args.Append("-map_metadata -1 -map_chapters -1 -avoid_negative_ts make_zero ");
            args.Append("-loglevel warning -progress pipe:1 -nostats ");
            args.Append(QuotePath(outputPath));

            return await RunProcessAsync(FfmpegExecutablePath, args.ToString(), showCmd, _tempSessionDirectory,
                CreateTimedProgressCallback(cb, progressStart, progressEnd, durationSeconds, "TikTok 60 Ultra"), ct).ConfigureAwait(false);
        }

        private async Task<ProcessExecutionResult> EncodeTikTok60UltraWithFallbackAsync(
            string outputPath,
            string framesDir,
            string pattern,
            double sourceSequenceFps,
            string originalVideoPath,
            double? audioOffset,
            bool showCmd,
            Action<string>? cb,
            CancellationToken ct,
            double? durationSeconds,
            int progressStart,
            int progressEnd)
        {
            ProcessExecutionResult result = await EncodeTikTok60UltraAsync(outputPath, framesDir, pattern, sourceSequenceFps,
                originalVideoPath, audioOffset, forceCpu: false, compatibilityMode: false, showCmd, cb, ct, durationSeconds, progressStart, progressEnd).ConfigureAwait(false);
            if (result.Success || (!UseNvidiaAcceleration && !UseAmdAcceleration))
                return result;

            string combined = (result.StandardError + "\n" + result.StandardOutput).ToLowerInvariant();
            bool hardwareFailure = combined.Contains("no capable devices found") ||
                                   combined.Contains("invalid argument") ||
                                   combined.Contains("external library") ||
                                   combined.Contains("device") ||
                                   combined.Contains("nvenc") ||
                                   combined.Contains("amf");
            if (!hardwareFailure)
                return result;

            cb?.Invoke("WARNING: Advanced NVENC options were not accepted; retrying the NVIDIA compatibility profile.");
            ProcessExecutionResult compatibilityResult = await EncodeTikTok60UltraAsync(outputPath, framesDir, pattern, sourceSequenceFps,
                originalVideoPath, audioOffset, forceCpu: false, compatibilityMode: true, showCmd, cb, ct, durationSeconds, progressStart, progressEnd).ConfigureAwait(false);
            if (compatibilityResult.Success)
                return compatibilityResult;

            cb?.Invoke("WARNING: Hardware encoder failed for TikTok 60 Ultra. Falling back to CPU H.264.");
            return await EncodeTikTok60UltraAsync(outputPath, framesDir, pattern, sourceSequenceFps, originalVideoPath,
                audioOffset, forceCpu: true, compatibilityMode: true, showCmd, cb, ct, durationSeconds, progressStart, progressEnd).ConfigureAwait(false);
        }

        private async Task<ProcessExecutionResult> EncodeTikTok120Async(
            string outputPath,
            string framesDir,
            string pattern,
            double rifeFps,
            string originalVideoPath,
            double? audioOffset,
            int outputFps,
            bool showCmd,
            Action<string>? cb,
            CancellationToken ct,
            double? durationSeconds,
            int progressStart,
            int progressEnd)
        {
            int outFps = Math.Max(1, outputFps);
            int g = Math.Max(1, outFps * Math.Max(1, TikTokKeyintSeconds));

            string vf = FilterGraphMild() + $",fps={outFps}";

            var args = new StringBuilder();
            args.Append("-y -nostdin ");

            args.Append($"-framerate {rifeFps.ToString(CultureInfo.InvariantCulture)} -i {QuotePath(Path.Combine(framesDir, pattern))} ");
            if (audioOffset.HasValue && audioOffset.Value > 0)
                args.Append($"-itsoffset {audioOffset.Value.ToString(CultureInfo.InvariantCulture)} ");
            args.Append($"-i {QuotePath(originalVideoPath)} ");
            args.Append("-map 0:v:0 -map 1:a? ");

            args.Append($"-vf \"{vf}\" ");
            args.Append(GetTikTokVideoEncoderArgs() + " ");
            args.Append(GetEffectiveTikTokRateControl(forceCpu: false) + " ");
            args.Append($"-r {outFps} -vsync cfr ");
            args.Append($"-g {g} -keyint_min {g} -sc_threshold 0 ");
            args.Append($"{AudioArgs} ");
            args.Append("-map_metadata -1 -map_chapters -1 -movflags +faststart -loglevel warning -progress pipe:1 -nostats ");
            args.Append(QuotePath(outputPath));

            return await RunProcessAsync(FfmpegExecutablePath, args.ToString(), showCmd, _tempSessionDirectory, CreateTimedProgressCallback(cb, progressStart, progressEnd, durationSeconds, "TikTok encode"), ct).ConfigureAwait(false);
        }

        private async Task<ProcessExecutionResult> EncodeTikTokWithFallbackAsync(
            string outputPath,
            string framesDir,
            string pattern,
            double rifeFps,
            string originalVideoPath,
            double? audioOffset,
            int outputFps,
            bool showCmd,
            Action<string>? cb,
            CancellationToken ct,
            double? durationSeconds,
            int progressStart,
            int progressEnd)
        {
            var result = await EncodeTikTok120Async(outputPath, framesDir, pattern, rifeFps, originalVideoPath, audioOffset, outputFps, showCmd, cb, ct, durationSeconds, progressStart, progressEnd).ConfigureAwait(false);
            if (result.Success || (!UseNvidiaAcceleration && !UseAmdAcceleration))
                return result;

            string combined = (result.StandardError + "\n" + result.StandardOutput).ToLowerInvariant();
            bool hardwareFailure = combined.Contains("no capable devices found") ||
                                  combined.Contains("invalid argument") ||
                                  combined.Contains("generic error in an external library") ||
                                  combined.Contains("device") ||
                                  combined.Contains("nvenc") ||
                                  combined.Contains("amf");
            if (!hardwareFailure)
                return result;

            cb?.Invoke("WARNING: Hardware encoder failed for TikTok30FPS. Falling back to CPU encoder.");
            return await EncodeTikTokCpuAsync(outputPath, framesDir, pattern, rifeFps, originalVideoPath, audioOffset, outputFps, showCmd, cb, ct, durationSeconds, progressStart, progressEnd).ConfigureAwait(false);
        }

        private async Task<ProcessExecutionResult> EncodeTikTokCpuAsync(
            string outputPath,
            string framesDir,
            string pattern,
            double rifeFps,
            string originalVideoPath,
            double? audioOffset,
            int outputFps,
            bool showCmd,
            Action<string>? cb,
            CancellationToken ct,
            double? durationSeconds,
            int progressStart,
            int progressEnd)
        {
            int outFps = Math.Max(1, outputFps);
            int g = Math.Max(1, outFps * Math.Max(1, TikTokKeyintSeconds));
            string vf = FilterGraphMild() + $",fps={outFps}";

            var args = new StringBuilder();
            args.Append("-y -nostdin ");
            args.Append($"-framerate {rifeFps.ToString(CultureInfo.InvariantCulture)} -i {QuotePath(Path.Combine(framesDir, pattern))} ");
            if (audioOffset.HasValue && audioOffset.Value > 0)
                args.Append($"-itsoffset {audioOffset.Value.ToString(CultureInfo.InvariantCulture)} ");
            args.Append($"-i {QuotePath(originalVideoPath)} ");
            args.Append("-map 0:v:0 -map 1:a? ");
            args.Append($"-vf \"{vf}\" ");
            args.Append(GetTikTokVideoEncoderArgs(forceCpu: true) + " ");
            args.Append(GetEffectiveTikTokRateControl(forceCpu: true) + " ");
            args.Append($"-r {outFps} -vsync cfr ");
            args.Append($"-g {g} -keyint_min {g} -sc_threshold 0 ");
            args.Append($"{AudioArgs} ");
            args.Append("-map_metadata -1 -map_chapters -1 -movflags +faststart -loglevel warning -progress pipe:1 -nostats ");
            args.Append(QuotePath(outputPath));

            return await RunProcessAsync(FfmpegExecutablePath, args.ToString(), showCmd, _tempSessionDirectory, CreateTimedProgressCallback(cb, progressStart, progressEnd, durationSeconds, "TikTok encode"), ct).ConfigureAwait(false);
        }

        private async Task<ProcessExecutionResult> RetimeCopyAsync(
            string inputPath,
            string outputPath,
            double inputTimestampScale,
            bool showCmd,
            Action<string>? cb,
            CancellationToken ct,
            double? durationSeconds,
            int progressStart,
            int progressEnd)
        {
            var args = new StringBuilder();
            args.Append("-y -nostdin ");
            args.Append($"-itsscale {inputTimestampScale.ToString(CultureInfo.InvariantCulture)} ");
            args.Append($"-i {QuotePath(inputPath)} ");
            args.Append("-c:v copy -c:a copy -map_metadata -1 -map_chapters -1 -movflags +faststart -loglevel warning -progress pipe:1 -nostats ");
            args.Append(QuotePath(outputPath));

            return await RunProcessAsync(FfmpegExecutablePath, args.ToString(), showCmd, _tempSessionDirectory, CreateTimedProgressCallback(cb, progressStart, progressEnd, durationSeconds, "Finalizing TikTok"), ct).ConfigureAwait(false);
        }

        private async Task<ProcessExecutionResult> ApplyFinalTimeScaleAsync(
            string inputPath,
            string outputPath,
            double timeScale,
            bool showCmd,
            Action<string>? cb,
            CancellationToken ct,
            double? durationSeconds,
            int progressStart,
            int progressEnd)
        {
            return await RetimeCopyAsync(inputPath, outputPath, timeScale, showCmd, cb, ct, durationSeconds, progressStart, progressEnd).ConfigureAwait(false);
        }

        private void LogInternal(string message, bool isError = false)
        {
            if (VerboseLogging)
                Console.WriteLine($"[Interpolator][{(isError ? "ERROR" : "INFO")}] {message}");
        }

        private string QuotePath(string path) => path.Contains(" ") ? $"\"{path}\"" : path;

        private string? ValidateTooling()
        {
            if (EnableRifeInterpolation)
            {
                string toolsDir = Path.GetDirectoryName(RifeExecutablePath) ?? "";
                string rifeModelDir = Path.Combine(toolsDir, RifeModelName);
                if (!Directory.Exists(rifeModelDir))
                    return $"RIFE model folder not found: {rifeModelDir}";
            }

            if (EnableUpscale)
            {
                if (IsNanoVsrEngine())
                {
                    if (!string.Equals(IntermediateFrameFormat, "png", StringComparison.OrdinalIgnoreCase))
                        return "NanoVSR requires PNG intermediate frames.";

                    if (string.IsNullOrWhiteSpace(UpscaleModelDirectory) || !Directory.Exists(UpscaleModelDirectory))
                        return "NanoVSR model directory not found.";

                    string nanoModelPath = Path.Combine(UpscaleModelDirectory, UpscaleModelName);
                    if (string.IsNullOrWhiteSpace(UpscaleModelName) || !File.Exists(nanoModelPath))
                        return "NanoVSR model file is missing.";
                    if (string.Equals(UpscaleEngine, "nanovsr-tensorrt", StringComparison.OrdinalIgnoreCase)
                        && File.Exists(nanoModelPath + ".data") == false)
                        return "NanoVSR TensorRT external weight file is missing (.onnx.data).";
                }
                else
                {
                if (string.IsNullOrWhiteSpace(UpscaleExecutablePath) || !File.Exists(UpscaleExecutablePath))
                    return "Upscale executable not found.";

                if (string.IsNullOrWhiteSpace(UpscaleModelDirectory) || !Directory.Exists(UpscaleModelDirectory))
                    return "Upscale model directory not found.";

                if (string.IsNullOrWhiteSpace(UpscaleModelName))
                    return "Upscale model name is empty.";

                string paramPath = Path.Combine(UpscaleModelDirectory, UpscaleModelName + ".param");
                string binPath = Path.Combine(UpscaleModelDirectory, UpscaleModelName + ".bin");
                if (!File.Exists(paramPath) || !File.Exists(binPath))
                    return "Upscale model files are missing.";
                }
            }

            if (EnableSubjectFocus && SubjectFocusMode != SubjectFocusMode.Off)
            {
                if (string.IsNullOrWhiteSpace(SubjectFocusModelPath) || !File.Exists(SubjectFocusModelPath))
                    return "Subject Focus model not found.";
            }

            return null;
        }

        private async Task<ProcessExecutionResult> ApplySubjectFocusAsync(
            string inputFramesDir,
            string outputFramesDir,
            bool showCmd,
            Action<string>? cb,
            CancellationToken ct,
            int progressStart,
            int progressEnd)
        {
            try
            {
                Directory.CreateDirectory(outputFramesDir);
                string extension = IntermediateFrameFormat.ToLowerInvariant();
                string[] frames = Directory.GetFiles(inputFramesDir, $"*.{extension}")
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                if (frames.Length == 0)
                    return new ProcessExecutionResult(-1, "", "Subject Focus found no frames to process.");

                cb?.Invoke($"Subject Focus mode: {SubjectFocusMode} ({SubjectFocusStrength:P0})");
                cb?.Invoke($"Subject Focus frames: {frames.Length}");

                await Task.Run(() =>
                {
                    using var processor = new SubjectFocusProcessor(SubjectFocusModelPath);
                    for (int i = 0; i < frames.Length; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        string output = Path.Combine(outputFramesDir, Path.GetFileName(frames[i]));
                        processor.ProcessFrame(frames[i], output, SubjectFocusMode, SubjectFocusStrength);

                        if (i == 0 || (i + 1) % 10 == 0 || i + 1 == frames.Length)
                        {
                            int progress = progressStart + (int)Math.Round(Math.Min(1.0, (double)(i + 1) / frames.Length) * (progressEnd - progressStart));
                            progress = Math.Clamp(progress, progressStart, progressEnd);
                            cb?.Invoke($"PROGRESS:{progress}%");
                            cb?.Invoke($"Subject Focus: {i + 1}/{frames.Length}");
                        }
                    }
                }, ct).ConfigureAwait(false);

                return new ProcessExecutionResult(0, "", "");
            }
            catch (OperationCanceledException)
            {
                // Cancellation is a user action, not an optional-stage
                // failure. Propagate it so the main pipeline stops instead of
                // silently exporting after the user pressed Cancel.
                throw;
            }
            catch (Exception ex)
            {
                return new ProcessExecutionResult(-1, "", "Subject Focus failed: " + ex.Message);
            }
        }

        private async Task<ProcessExecutionResult> UpscaleFramesAsync(
            string inputDir,
            string outputDir,
            bool showCmd,
            Action<string>? cb,
            CancellationToken ct,
            int progressStart,
            int progressEnd)
        {
            LastQualityBoostStatus = "running";
            Directory.CreateDirectory(outputDir);
            string upscaleLabel = string.IsNullOrWhiteSpace(UpscaleDisplayName) ? UpscaleModelName : UpscaleDisplayName;
            cb?.Invoke($"Upscale model: {upscaleLabel} [{UpscaleModelName}] ({UpscaleScale}x)");
            cb?.Invoke($"PROGRESS:{progressStart}%");

            if (IsNanoVsrEngine())
            {
                if (NanoVsrBackend == NanoVsrExecutionBackend.TensorRT)
                    cb?.Invoke("Upscale acceleration: NVIDIA TensorRT EP (FP16 + engine cache)");
                else
                    cb?.Invoke("Upscale acceleration: DirectML GPU, temporal window=15");
                ProcessExecutionResult nanoResult = await UpscaleNanoVsrFramesAsync(
                    inputDir,
                    outputDir,
                    cb,
                    ct,
                    progressStart,
                    progressEnd).ConfigureAwait(false);
                if (nanoResult.Success && LastQualityBoostStatus == "running")
                    LastQualityBoostStatus = "enhanced";
                else if (!nanoResult.Success)
                    LastQualityBoostStatus = "failed";
                return nanoResult;
            }

            var argsList = new List<string>
            {
                "-i", QuotePath(inputDir),
                "-o", QuotePath(outputDir),
                "-s", Math.Max(1, UpscaleScale).ToString(CultureInfo.InvariantCulture),
                "-f", IntermediateFrameFormat.ToLowerInvariant(),
                "-j", string.IsNullOrWhiteSpace(UpscaleThreads) ? "1:2:2" : UpscaleThreads,
                "-m", QuotePath(UpscaleModelDirectory),
                "-n", UpscaleModelName
            };

            if (UpscaleTileSize >= 32)
                argsList.AddRange(new[] { "-t", UpscaleTileSize.ToString(CultureInfo.InvariantCulture) });

            if (!string.IsNullOrWhiteSpace(UpscaleGpuId)
                && !string.Equals(UpscaleGpuId, "auto", StringComparison.OrdinalIgnoreCase))
            {
                argsList.AddRange(new[] { "-g", UpscaleGpuId });
            }

            if (string.Equals(UpscaleEngine, "realcugan", StringComparison.OrdinalIgnoreCase))
                argsList.AddRange(new[] { "-c", "3" });

            int expectedFrames = Directory.GetFiles(inputDir, $"*.{IntermediateFrameFormat.ToLowerInvariant()}").Length;
            int completedFrames = 0;
            int lastReportedProgress = -1;
            int lastReportedFrames = -1;
            object progressSync = new();
            int reportStride = Math.Max(1, expectedFrames / 50);

            void ReportUpscaleProgress(int done)
            {
                if (expectedFrames <= 0)
                    return;

                lock (progressSync)
                {
                    done = Math.Clamp(done, 0, expectedFrames);
                    double ratio = Math.Min(1.0, (double)done / expectedFrames);
                    int progress = progressStart + (int)Math.Round((progressEnd - progressStart) * ratio);
                    progress = Math.Clamp(progress, progressStart, progressEnd);
                    if (progress != lastReportedProgress)
                    {
                        cb?.Invoke($"PROGRESS:{progress}%");
                        lastReportedProgress = progress;
                    }

                    if (done != lastReportedFrames && (done >= expectedFrames || done % reportStride == 0))
                    {
                        cb?.Invoke($"Upscale: {done}/{expectedFrames} frames");
                        lastReportedFrames = done;
                    }
                }
            }

            string outputExtension = "." + IntermediateFrameFormat.ToLowerInvariant();
            using var outputWatcher = new FileSystemWatcher(outputDir)
            {
                Filter = "*" + outputExtension,
                NotifyFilter = NotifyFilters.FileName,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true
            };
            outputWatcher.Created += (_, _) =>
            {
                int done = Interlocked.Increment(ref completedFrames);
                ReportUpscaleProgress(done);
            };

            cb?.Invoke($"Upscale acceleration: GPU={UpscaleGpuId}, tile={UpscaleTileSize}, jobs={UpscaleThreads}");
            var stopwatch = Stopwatch.StartNew();

            void HandleUpscaleProcessMessage(string message)
            {
                string raw = message.StartsWith("[stderr] ", StringComparison.Ordinal)
                    ? message[9..].Trim()
                    : message.Trim();

                // Real-ESRGAN writes tile percentages (often dozens per frame) to stderr.
                // Forwarding each one synchronously to WPF can make the UI the bottleneck.
                if (raw.EndsWith('%')
                    && double.TryParse(raw[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                {
                    return;
                }

                bool diagnostic = raw.Contains("error", StringComparison.OrdinalIgnoreCase)
                                  || raw.Contains("failed", StringComparison.OrdinalIgnoreCase)
                                  || raw.Contains("invalid", StringComparison.OrdinalIgnoreCase)
                                  || raw.Contains("vkallocate", StringComparison.OrdinalIgnoreCase)
                                  || raw.Contains("out of memory", StringComparison.OrdinalIgnoreCase);
                if (VerboseLogging || diagnostic)
                    cb?.Invoke(message);
            }

            ProcessExecutionResult result;
            result = await RunProcessAsync(
                UpscaleExecutablePath,
                string.Join(" ", argsList),
                showCmd,
                Path.GetDirectoryName(UpscaleExecutablePath),
                HandleUpscaleProcessMessage,
                ct).ConfigureAwait(false);
            stopwatch.Stop();

            int finalDone = Directory.Exists(outputDir)
                ? Directory.GetFiles(outputDir, $"*.{IntermediateFrameFormat.ToLowerInvariant()}").Length
                : 0;
            cb?.Invoke(expectedFrames > 0
                ? $"Upscale: {finalDone}/{expectedFrames} frames"
                : $"Upscale: {finalDone} frames");
            if (finalDone > 0 && stopwatch.Elapsed.TotalSeconds > 0)
                cb?.Invoke($"Upscale speed: {finalDone / stopwatch.Elapsed.TotalSeconds:F1} frames/s ({stopwatch.Elapsed:mm\\:ss})");
            if (result.Success)
            {
                cb?.Invoke($"PROGRESS:{progressEnd}%");
                LastQualityBoostStatus = "enhanced";
            }
            else
            {
                // Quality boosters are optional. A Vulkan/DirectML/driver
                // failure must not discard an otherwise valid export. Replace
                // any partial output with the source frames and continue at
                // native resolution, just like the NanoVSR fallback path.
                if (TryPreserveNativeFrames(inputDir, outputDir, ct, IntermediateFrameFormat))
                {
                    LastQualityBoostStatus = "skipped-upscale";
                    string detail = string.IsNullOrWhiteSpace(result.StandardError)
                        ? result.StandardOutput
                        : result.StandardError;
                    detail = string.Join(" ", detail.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                    if (detail.Length > 240) detail = detail[..240] + "…";
                    cb?.Invoke(string.IsNullOrWhiteSpace(detail)
                        ? "Upscale failed safely; native frames were preserved."
                        : "Upscale failed safely; native frames were preserved: " + detail);
                    cb?.Invoke("Export will continue without AI enhancement. Try a smaller tile size or a lighter x2 model.");
                    cb?.Invoke($"PROGRESS:{progressEnd}%");
                    return new ProcessExecutionResult(0, result.StandardOutput, "");
                }

                LastQualityBoostStatus = "failed";
            }

            return result;
        }

        private async Task<ProcessExecutionResult> UpscaleNanoVsrFramesAsync(
            string inputDir,
            string outputDir,
            Action<string>? cb,
            CancellationToken ct,
            int progressStart,
            int progressEnd)
        {
            if (TryGetNanoVsrInputSize(inputDir, IntermediateFrameFormat, out int inputWidth, out int inputHeight) &&
                (long)inputWidth * inputHeight * NanoVsrProcessor.ModelScale * NanoVsrProcessor.ModelScale > NanoVsrProcessor.MaxOutputPixels)
            {
                LastQualityBoostStatus = "skipped-safe-limit";
                // NanoVSR is a fixed 4x model. Avoid constructing either a
                // TensorRT or DirectML session for an oversized source: the
                // runtime can fail inside a reshape node before we can fall
                // back cleanly. Preserve the extracted frames and let the
                // normal encoder continue at native resolution.
                Directory.CreateDirectory(outputDir);
                string frameExtension = "." + IntermediateFrameFormat.Trim().TrimStart('.').ToLowerInvariant();
                foreach (string frame in Directory.EnumerateFiles(inputDir)
                    .Where(path => string.Equals(Path.GetExtension(path), frameExtension, StringComparison.OrdinalIgnoreCase)))
                {
                    ct.ThrowIfCancellationRequested();
                    File.Copy(frame, Path.Combine(outputDir, Path.GetFileName(frame)), true);
                }

                long outputWidth = (long)inputWidth * NanoVsrProcessor.ModelScale;
                long outputHeight = (long)inputHeight * NanoVsrProcessor.ModelScale;
                long outputPixels = outputWidth * outputHeight;
                cb?.Invoke($"NanoVSR x{NanoVsrProcessor.ModelScale} skipped before inference for {inputWidth}x{inputHeight}: output would be {outputWidth}x{outputHeight} ({outputPixels:N0} pixels), above the safe limit of {NanoVsrProcessor.MaxOutputPixels:N0}.");
                cb?.Invoke("Native frames were preserved. Choose a bundled x2 model (or reduce the source resolution) for an actual enhancement.");
                cb?.Invoke($"PROGRESS:{progressEnd}%");
                return new ProcessExecutionResult(0, "", "");
            }

            string modelPath = Path.Combine(UpscaleModelDirectory, UpscaleModelName);
            var stopwatch = Stopwatch.StartNew();
            int lastProgress = -1;
            int lastFrameReport = -1;

            try
            {
                using var processor = new NanoVsrProcessor(
                    modelPath,
                    NanoVsrDeviceId,
                    NanoVsrBackend,
                    NanoVsrEngineCachePath);
                await processor.ProcessDirectoryAsync(
                    inputDir,
                    outputDir,
                    IntermediateFrameFormat,
                    (done, total, elapsedSeconds) =>
                    {
                        double ratio = total > 0 ? Math.Min(1.0, (double)done / total) : 0;
                        int progress = progressStart + (int)Math.Round((progressEnd - progressStart) * ratio);
                        progress = Math.Clamp(progress, progressStart, progressEnd);
                        if (progress != lastProgress)
                        {
                            cb?.Invoke($"PROGRESS:{progress}%");
                            lastProgress = progress;
                        }

                        int reportStride = Math.Max(1, total / 50);
                        if (done != lastFrameReport && (done == total || done % reportStride == 0))
                        {
                            double fps = elapsedSeconds > 0 ? done / elapsedSeconds : 0;
                            cb?.Invoke($"NanoVSR: {done}/{total} frames ({fps:F1} frames/s)");
                            lastFrameReport = done;
                        }
                    },
                    ct,
                    message => cb?.Invoke(message)).ConfigureAwait(false);

                stopwatch.Stop();
                int expected = Directory.GetFiles(inputDir, "*.png").Length;
                int produced = Directory.GetFiles(outputDir, "*.png").Length;
                if (produced != expected)
                {
                    LastQualityBoostStatus = "failed";
                    return new ProcessExecutionResult(-1, "", $"NanoVSR produced {produced} of {expected} frames.");
                }

                if (produced > 0 && stopwatch.Elapsed.TotalSeconds > 0)
                    cb?.Invoke($"Upscale speed: {produced / stopwatch.Elapsed.TotalSeconds:F1} frames/s ({stopwatch.Elapsed:mm\\:ss})");
                cb?.Invoke($"PROGRESS:{progressEnd}%");
                LastQualityBoostStatus = "enhanced";
                return new ProcessExecutionResult(0, "", "");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // NanoVSR is an optional quality stage. A driver/runtime or
                // VRAM failure must not invalidate the otherwise valid encode.
                // Preserve the extracted native frames and let the normal
                // encoder continue, while exposing the exact reason in the
                // log and status field.
                if (TryPreserveNativeFrames(inputDir, outputDir, ct, IntermediateFrameFormat))
                {
                    LastQualityBoostStatus = NanoVsrBackend == NanoVsrExecutionBackend.TensorRT
                        ? "skipped-tensorrt"
                        : "skipped-nanovsr";
                    string backendName = NanoVsrBackend == NanoVsrExecutionBackend.TensorRT ? "TensorRT" : "DirectML";
                    cb?.Invoke($"NanoVSR {backendName} failed safely: {ex.Message}");
                    cb?.Invoke("Native frames were preserved and the export will continue without AI enhancement. Try a bundled x2 model or update the GPU driver for enhancement.");
                    cb?.Invoke($"PROGRESS:{progressEnd}%");
                    return new ProcessExecutionResult(0, "", "");
                }

                LastQualityBoostStatus = "failed";
                return new ProcessExecutionResult(-1, "", "NanoVSR failed: " + ex.Message);
            }
        }

        private static bool TryPreserveNativeFrames(
            string inputDir,
            string outputDir,
            CancellationToken cancellationToken,
            string extension)
        {
            try
            {
                string normalizedExtension = "." + extension.Trim().TrimStart('.').ToLowerInvariant();
                Directory.CreateDirectory(outputDir);
                foreach (string partial in Directory.EnumerateFiles(outputDir)
                    .Where(path => string.Equals(Path.GetExtension(path), normalizedExtension, StringComparison.OrdinalIgnoreCase)))
                    File.Delete(partial);

                string[] frames = Directory.EnumerateFiles(inputDir)
                    .Where(path => string.Equals(Path.GetExtension(path), normalizedExtension, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (frames.Length == 0)
                    return false;

                foreach (string frame in frames)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    File.Copy(frame, Path.Combine(outputDir, Path.GetFileName(frame)), true);
                }

                return Directory.EnumerateFiles(outputDir)
                    .Count(path => string.Equals(Path.GetExtension(path), normalizedExtension, StringComparison.OrdinalIgnoreCase)) == frames.Length;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return false;
            }
        }

        private bool IsNanoVsrEngine()
        {
            return string.Equals(UpscaleEngine, "nanovsr", StringComparison.OrdinalIgnoreCase)
                || string.Equals(UpscaleEngine, "nanovsr-tensorrt", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryGetNanoVsrInputSize(string inputDir, string extension, out int width, out int height)
        {
            width = 0;
            height = 0;
            try
            {
                string normalizedExtension = "." + extension.Trim().TrimStart('.').ToLowerInvariant();
                string? firstFrame = Directory.EnumerateFiles(inputDir)
                    .Where(path => string.Equals(Path.GetExtension(path), normalizedExtension, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (string.IsNullOrWhiteSpace(firstFrame))
                    return false;

                using var bitmap = new Bitmap(firstFrame);
                width = bitmap.Width;
                height = bitmap.Height;
                return width > 0 && height > 0;
            }
            catch
            {
                width = 0;
                height = 0;
                return false;
            }
        }

        private static void TryCopyFile(string sourcePath, string destinationPath)
        {
            if (File.Exists(sourcePath))
                File.Copy(sourcePath, destinationPath, true);
        }

        private async Task<ProcessExecutionResult> RunNvidiaVfxTrialAsync(
            string inputVideoPath,
            string outputVideoPath,
            bool showWindow,
            Action<string>? cb,
            CancellationToken ct)
        {
            if (!EnableNvidiaVfxTrial)
                return new ProcessExecutionResult(0, "", "");

            if (string.IsNullOrWhiteSpace(NvidiaVfxPythonPath) || !File.Exists(NvidiaVfxPythonPath))
                return new ProcessExecutionResult(-1, "", "NVIDIA VFX trial Python runtime was not found. This local-only profile requires the NVIDIA runtime to be installed separately.");
            if (string.IsNullOrWhiteSpace(NvidiaVfxScriptPath) || !File.Exists(NvidiaVfxScriptPath))
                return new ProcessExecutionResult(-1, "", "NVIDIA VFX trial script was not found.");
            if (string.IsNullOrWhiteSpace(NvidiaVfxRuntimePath) || !Directory.Exists(NvidiaVfxRuntimePath))
                return new ProcessExecutionResult(-1, "", "NVIDIA VFX trial runtime directory was not found.");

            int scale = Math.Clamp(NvidiaVfxScale, 1, 2);
            string quality = string.IsNullOrWhiteSpace(NvidiaVfxQuality) ? "HIGHBITRATE_ULTRA" : NvidiaVfxQuality.Trim();
            string args = $"{QuotePath(NvidiaVfxScriptPath)} {QuotePath(inputVideoPath)} {QuotePath(outputVideoPath)} --quality {QuotePath(quality)} --scale {scale}";
            return await RunProcessAsync(
                NvidiaVfxPythonPath,
                args,
                showWindow,
                Path.GetDirectoryName(NvidiaVfxScriptPath),
                cb,
                ct,
                ProcessPriorityClass.AboveNormal).ConfigureAwait(false);
        }

        private async Task<ProcessExecutionResult> RunProcessAsync(
            string executablePath,
            string arguments,
            bool showWindow,
            string? workingDirectory = null,
            Action<string>? progressCallback = null,
            CancellationToken cancellationToken = default,
            ProcessPriorityClass? processPriority = null)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var psi = new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = arguments,
                RedirectStandardOutput = !showWindow,
                RedirectStandardError = !showWindow,
                UseShellExecute = false,
                CreateNoWindow = !showWindow,
                WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(executablePath) ?? Environment.CurrentDirectory
            };

            using var process = new Process { StartInfo = psi };
            using var cancellationRegistration = cancellationToken.Register(() => KillProcessTree(process));
            var stdOut = new StringBuilder();
            var stdErr = new StringBuilder();
            var stdoutClosed = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var stderrClosed = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);

            if (!showWindow)
            {
                process.OutputDataReceived += (_, e) =>
                {
                    if (e.Data != null)
                    {
                        progressCallback?.Invoke(e.Data);
                        stdOut.AppendLine(e.Data);
                    }
                    else stdoutClosed.TrySetResult(null);
                };
                process.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data != null)
                    {
                        progressCallback?.Invoke("[stderr] " + e.Data);
                        stdErr.AppendLine(e.Data);
                    }
                    else stderrClosed.TrySetResult(null);
                };
            }

            try
            {
                process.Start();
                if (processPriority.HasValue)
                {
                    try { process.PriorityClass = processPriority.Value; }
                    catch { }
                }
            }
            catch (Exception ex) { return new ProcessExecutionResult(-1, "", "Failed to start process: " + ex.Message); }

            if (!showWindow)
            {
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }

            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                KillProcessTree(process);
                throw;
            }

            if (!showWindow)
            {
                // Wait for both asynchronous pipe readers to receive their EOF.
                // A fixed sleep could lose the last diagnostics on a busy encode.
                try
                {
                    await Task.WhenAll(
                        stdoutClosed.Task.WaitAsync(TimeSpan.FromSeconds(2)),
                        stderrClosed.Task.WaitAsync(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    // The process has exited; incomplete trailing diagnostics do
                    // not make an otherwise valid encode fail.
                }
            }

            return new ProcessExecutionResult(process.ExitCode, stdOut.ToString(), stdErr.ToString());
        }

        private static void KillProcessTree(Process process)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
            }
        }

        private static Action<string>? CreateTimedProgressCallback(
            Action<string>? inner,
            int progressStart,
            int progressEnd,
            double? durationSeconds,
            string label)
        {
            if (inner == null)
                return null;

            int lastProgress = -1;
            int lastSecond = -1;
            return msg =>
            {
                inner(msg);
                if (!durationSeconds.HasValue || durationSeconds.Value <= 0)
                    return;

                if (!TryParseFfmpegOutTimeSeconds(msg, out double seconds))
                    return;

                double ratio = Math.Clamp(seconds / durationSeconds.Value, 0.0, 1.0);
                int progress = progressStart + (int)Math.Round((progressEnd - progressStart) * ratio);
                progress = Math.Clamp(progress, progressStart, progressEnd);
                int wholeSecond = (int)Math.Floor(seconds);

                if (progress != lastProgress)
                {
                    inner($"PROGRESS:{progress}%");
                    lastProgress = progress;
                }

                if (wholeSecond != lastSecond)
                {
                    inner($"{label}: {FormatSeconds(seconds)}/{FormatSeconds(durationSeconds.Value)}");
                    lastSecond = wholeSecond;
                }
            };
        }

        private static Action<string>? CreateFrameProgressCallback(
            Action<string>? inner,
            int progressStart,
            int progressEnd,
            int expectedFrames,
            string label)
        {
            if (inner == null)
                return null;

            int lastProgress = -1;
            int lastFrame = -1;
            return msg =>
            {
                inner(msg);
                if (expectedFrames <= 0 || !TryParseFfmpegFrame(msg, out int frame))
                    return;

                double ratio = Math.Clamp((double)frame / expectedFrames, 0.0, 1.0);
                int progress = progressStart + (int)Math.Round((progressEnd - progressStart) * ratio);
                progress = Math.Clamp(progress, progressStart, progressEnd);

                if (progress != lastProgress)
                {
                    inner($"PROGRESS:{progress}%");
                    lastProgress = progress;
                }

                if (frame != lastFrame && (frame == 0 || frame % 10 == 0 || frame >= expectedFrames))
                {
                    inner($"{label}: {Math.Min(frame, expectedFrames)}/{expectedFrames} frames");
                    lastFrame = frame;
                }
            };
        }

        private static bool TryParseFfmpegFrame(string msg, out int frame)
        {
            frame = 0;
            if (!msg.StartsWith("frame=", StringComparison.OrdinalIgnoreCase))
                return false;

            string raw = msg.Substring("frame=".Length).Trim();
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out frame);
        }

        private static bool TryParseFfmpegOutTimeSeconds(string msg, out double seconds)
        {
            seconds = 0;
            if (msg.StartsWith("out_time_ms=", StringComparison.OrdinalIgnoreCase) ||
                msg.StartsWith("out_time_us=", StringComparison.OrdinalIgnoreCase))
            {
                int idx = msg.IndexOf('=');
                if (idx >= 0 && long.TryParse(msg[(idx + 1)..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long microseconds))
                {
                    seconds = microseconds / 1_000_000.0;
                    return true;
                }
            }

            if (msg.StartsWith("out_time=", StringComparison.OrdinalIgnoreCase))
            {
                string raw = msg.Substring("out_time=".Length).Trim();
                if (TimeSpan.TryParse(raw, CultureInfo.InvariantCulture, out TimeSpan time))
                {
                    seconds = time.TotalSeconds;
                    return true;
                }
            }

            return false;
        }

        private static string FormatSeconds(double seconds)
        {
            seconds = Math.Max(0, seconds);
            TimeSpan time = TimeSpan.FromSeconds(seconds);
            return time.TotalHours >= 1
                ? time.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
                : time.ToString(@"m\:ss", CultureInfo.InvariantCulture);
        }

        private async Task<(double? Fps, ProcessExecutionResult Result)> GetOriginalVideoFpsAsync(
            string videoPath, Action<string>? cb, CancellationToken ct)
        {
            string args = $"-v error -select_streams v:0 -show_entries stream=r_frame_rate -of default=noprint_wrappers=1:nokey=1 {QuotePath(videoPath)}";
            var result = await RunProcessAsync(FfprobeExecutablePath, args, false, progressCallback: cb, cancellationToken: ct).ConfigureAwait(false);

            if (result.Success && !string.IsNullOrWhiteSpace(result.StandardOutput))
            {
                string s = result.StandardOutput.Trim();
                if (s.Contains("/"))
                {
                    var parts = s.Split('/');
                    if (double.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out double num) &&
                        double.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out double den) && den != 0)
                        return (num / den, result);
                }
                else if (double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out double fps))
                {
                    return (fps, result);
                }
            }

            return (null, result);
        }

        private async Task<(double? DurationSeconds, ProcessExecutionResult Result)> GetVideoDurationAsync(
            string videoPath, Action<string>? cb, CancellationToken ct)
        {
            string args = $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 {QuotePath(videoPath)}";
            var result = await RunProcessAsync(FfprobeExecutablePath, args, false, progressCallback: cb, cancellationToken: ct).ConfigureAwait(false);

            if (result.Success && double.TryParse(result.StandardOutput.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double duration) && duration > 0)
                return (duration, result);

            return (null, result);
        }

        private async Task<(bool Valid, string Message)> ValidateTikTokPreservationOutputAsync(
            string videoPath,
            double? expectedDurationSeconds,
            CancellationToken ct)
        {
            if (!File.Exists(videoPath) || new FileInfo(videoPath).Length < 64 * 1024)
                return (false, "Output file is missing or unexpectedly small.");

            var (fps, fpsResult) = await GetOriginalVideoFpsAsync(videoPath, null, ct).ConfigureAwait(false);
            if (!fpsResult.Success || fps == null || fps.Value < 55 || fps.Value > 61)
                return (false, $"Unexpected video cadence: {(fps?.ToString("0.###", CultureInfo.InvariantCulture) ?? "unknown")} FPS.");

            string dimensionsArgs = $"-v error -select_streams v:0 -show_entries stream=width,height -of csv=p=0:s=x {QuotePath(videoPath)}";
            ProcessExecutionResult dimensionsResult = await RunProcessAsync(
                FfprobeExecutablePath, dimensionsArgs, false, progressCallback: null, cancellationToken: ct).ConfigureAwait(false);
            string[] dimensions = dimensionsResult.StandardOutput.Trim().Split('x', StringSplitOptions.RemoveEmptyEntries);
            if (!dimensionsResult.Success || dimensions.Length != 2 ||
                !int.TryParse(dimensions[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int width) ||
                !int.TryParse(dimensions[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int height) ||
                width <= 0 || height <= 0)
                return (false, "Could not validate TikTok video dimensions.");

            bool landscape = width >= height;
            int maxWidth = landscape ? 1920 : 1080;
            int maxHeight = landscape ? 1080 : 1920;
            if (width > maxWidth || height > maxHeight)
                return (false, $"TikTok60Ultra dimensions {width}x{height} exceed the 1080-class limit ({maxWidth}x{maxHeight}).");

            var (duration, durationResult) = await GetVideoDurationAsync(videoPath, null, ct).ConfigureAwait(false);
            if (!durationResult.Success || duration == null)
                return (false, "Could not validate MP4 duration.");

            if (expectedDurationSeconds.HasValue && expectedDurationSeconds.Value > 0)
            {
                double tolerance = Math.Max(0.35, expectedDurationSeconds.Value * 0.035);
                if (Math.Abs(duration.Value - expectedDurationSeconds.Value) > tolerance)
                    return (false, $"Duration changed from {expectedDurationSeconds.Value:0.###}s to {duration.Value:0.###}s.");
            }

            string decodeArgs = $"-v error -i {QuotePath(videoPath)} -map 0:v:0 -frames:v 2 -f null -";
            ProcessExecutionResult decode = await RunProcessAsync(
                FfmpegExecutablePath,
                decodeArgs,
                false,
                progressCallback: null,
                cancellationToken: ct).ConfigureAwait(false);
            if (!decode.Success)
                return (false, "Video stream failed the decode smoke test: " + decode.StandardError.Trim());

            return (true, $"{width}x{height}, {fps.Value:0.###} FPS, {duration.Value:0.###}s, MP4 decode OK.");
        }

        private async Task<(double? StartTime, ProcessExecutionResult Result)> GetAudioStartTimeAsync(
            string videoPath, Action<string>? cb, CancellationToken ct)
        {
            string args = $"-v error -select_streams a:0 -show_entries stream=start_time -of default=noprint_wrappers=1:nokey=1 {QuotePath(videoPath)}";
            var result = await RunProcessAsync(FfprobeExecutablePath, args, false, progressCallback: cb, cancellationToken: ct).ConfigureAwait(false);

            if (result.Success && !string.IsNullOrWhiteSpace(result.StandardOutput))
            {
                string s = result.StandardOutput.Trim();
                if (double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out double st) && st > 0)
                    return (st, result);
            }

            return (null, result);
        }

        private async Task<ProcessExecutionResult> ExtractFramesAsync(
            string inputPath,
            string outputDir,
            string pattern,
            bool useNvdec,
            bool showCmd,
            Action<string>? cb,
            CancellationToken ct,
            int expectedFrames,
            int progressStart,
            int progressEnd,
            int nvdecThreads = 8,
            bool useHardwareDecode = true)
        {
            string hw = "";
            if (useHardwareDecode && useNvdec && UseNvidiaAcceleration)
                hw = $"-threads {Math.Clamp(nvdecThreads, 1, 8)} -hwaccel cuda ";
            else if (useHardwareDecode && UseAmdAcceleration)
                hw = "-hwaccel d3d11va ";

            var args = new StringBuilder();
            args.Append($"-y -nostdin {hw}-i {QuotePath(inputPath)} -vsync vfr ");

            if (IntermediateFrameFormat.Equals("png", StringComparison.OrdinalIgnoreCase) && PngCompressionLevel >= 0)
                args.Append($"-compression_level {PngCompressionLevel} ");

            args.Append("-progress pipe:1 -nostats ");
            args.Append(QuotePath(Path.Combine(outputDir, pattern)));

            Action<string>? frameProgress = CreateFrameProgressCallback(cb, progressStart, progressEnd, expectedFrames, "Extracting");
            Action<string>? filteredProgress = frameProgress;
            if (useNvdec && UseNvidiaAcceleration && frameProgress != null)
            {
                filteredProgress = message =>
                {
                    if (!IsTransientNvdecDiagnostic(message))
                        frameProgress(message);
                };
            }

            return await RunProcessAsync(FfmpegExecutablePath, args.ToString(), showCmd, _tempSessionDirectory, filteredProgress, ct).ConfigureAwait(false);
        }

        private static bool IsTransientNvdecDiagnostic(string message)
        {
            return message.Contains("cuda_error", StringComparison.OrdinalIgnoreCase)
                   || message.Contains("cuvid", StringComparison.OrdinalIgnoreCase)
                   || message.Contains("decode surfaces", StringComparison.OrdinalIgnoreCase)
                   || message.Contains("hwaccel initialisation", StringComparison.OrdinalIgnoreCase)
                   || message.Contains("failed setup for format cuda", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsHardwareDecodeFailure(ProcessExecutionResult result)
        {
            string text = (result.StandardError + "\n" + result.StandardOutput).ToLowerInvariant();
            return text.Contains("cuda_error", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("cuvid", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("nvdec", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("d3d11va", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("dxva", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("amf", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("device setup failed", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("hardware accelerator failed", StringComparison.OrdinalIgnoreCase);
        }

        private static string SummarizeProcessError(ProcessExecutionResult result)
        {
            string text = string.IsNullOrWhiteSpace(result.StandardError)
                ? result.StandardOutput
                : result.StandardError;
            text = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (string.IsNullOrWhiteSpace(text))
                return $"exit code {result.ExitCode}";
            return text.Length > 240 ? text[..240] + "…" : text;
        }

        private static void ClearFrameDirectory(string directory)
        {
            if (!Directory.Exists(directory))
                return;

            foreach (string file in Directory.EnumerateFiles(directory))
            {
                try { File.Delete(file); }
                catch { }
            }
        }

        private bool EnsureFrameWorkspace(int expectedSourceFrames, Action<string>? cb)
        {
            if (expectedSourceFrames <= 0 || SourceWidth <= 0 || SourceHeight <= 0 || string.IsNullOrWhiteSpace(_tempSessionDirectory))
                return true;

            try
            {
                long frameCount = (long)expectedSourceFrames * (EnableRifeInterpolation ? 2L : 1L);
                long pixelsPerFrame = (long)SourceWidth * SourceHeight;
                // PNG working frames are intentionally low-compression. Reserve
                // a conservative 4 bytes/pixel plus headroom for RIFE/NanoVSR
                // output and FFmpeg's own temporary buffers.
                long estimatedBytes = checked(frameCount * pixelsPerFrame * 4L);
                long requiredBytes = Math.Max(2L * 1024 * 1024 * 1024, estimatedBytes + estimatedBytes / 2);
                string? root = Path.GetPathRoot(_tempSessionDirectory);
                if (string.IsNullOrWhiteSpace(root)) return true;
                long available = new DriveInfo(root).AvailableFreeSpace;
                if (available >= requiredBytes) return true;

                cb?.Invoke($"Insufficient temporary workspace: need about {FormatBytes(requiredBytes)}, available {FormatBytes(available)}.");
                return false;
            }
            catch (OverflowException)
            {
                cb?.Invoke("Temporary frame workspace estimate exceeded the supported size.");
                return false;
            }
            catch
            {
                // Drive probing is advisory; do not block processing when the
                // operating system refuses to report free space.
                return true;
            }
        }

        private static string FormatBytes(long bytes)
        {
            const double kb = 1024;
            const double mb = kb * 1024;
            const double gb = mb * 1024;
            return bytes >= gb ? $"{bytes / gb:0.##} GB" : bytes >= mb ? $"{bytes / mb:0.##} MB" : $"{bytes / kb:0} KB";
        }

        private async Task<ProcessExecutionResult> RunRifeInterpolationAsync(
            string inputDir, string outputDir, bool showCmd, Action<string>? cb, CancellationToken ct)
        {
            var argsList = new List<string>
            {
                "-i", QuotePath(inputDir),
                "-o", QuotePath(outputDir),
                "-f", IntermediateFrameFormat.ToLowerInvariant(),
                "-m", QuotePath(Path.Combine(Path.GetDirectoryName(RifeExecutablePath)!, RifeModelName)),
                "-j", RifeThreads
            };

            if (!string.IsNullOrWhiteSpace(RifeGpuId) && RifeGpuId.ToLowerInvariant() != "auto")
                argsList.AddRange(new[] { "-g", RifeGpuId });

            if (RifeEnableSpatialTta) argsList.Add("-x");
            if (RifeEnableUhdMode) argsList.Add("-u");
            if (VerboseLogging) argsList.Add("-v");

            return await RunProcessAsync(RifeExecutablePath, string.Join(" ", argsList), showCmd, Path.GetDirectoryName(RifeExecutablePath), cb, ct).ConfigureAwait(false);
        }

        private async Task<(string Path, ProcessExecutionResult Result)> PreFilterInputAsync(
            string inputPath, bool showCmd, Action<string>? cb, CancellationToken ct)
        {
            string filteredPath = Path.Combine(_tempSessionDirectory, "prefiltered_input.mp4");
            var args = new StringBuilder();
            args.Append($"-y -nostdin -i {QuotePath(inputPath)} ");
            args.Append($"-vf \"{PreFilterGraph}\" ");
            args.Append("-c:v libx264 -crf 0 -preset ultrafast -c:a copy ");
            args.Append(QuotePath(filteredPath));
            var result = await RunProcessAsync(FfmpegExecutablePath, args.ToString(), showCmd, _tempSessionDirectory, cb, ct).ConfigureAwait(false);
            return (filteredPath, result);
        }

        private void SetupTempDirectory()
        {
            string tempRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ForgeFlow",
                "TempProcessingData");
            _tempSessionDirectory = Path.Combine(tempRoot, "Session_" + Guid.NewGuid().ToString("N").Substring(0, 12));
            Directory.CreateDirectory(_tempSessionDirectory);
        }

        private void CleanupTempDirectory()
        {
            if (string.IsNullOrEmpty(_tempSessionDirectory) || !Directory.Exists(_tempSessionDirectory)) return;
            if (_tempSessionDirectory.Contains("TempProcessingData") && _tempSessionDirectory.Contains("Session_"))
            {
                try { Directory.Delete(_tempSessionDirectory, true); } catch { }
            }
        }
    }
}
