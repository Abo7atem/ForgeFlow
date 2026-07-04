using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        public bool EnableRifeInterpolation { get; set; } = true;
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
        public bool GenerateTikTok30 { get; set; } = true;

        // PC output (x264 CRF)
        public int PcFps { get; set; } = 60;
        public int PcCrf { get; set; } = 13;
        public string PcPreset { get; set; } = "veryslow";
        public string PcTune { get; set; } = "film";
        public int PcKeyintSeconds { get; set; } = 1;

        // TikTok output: encode 120fps temp, then retime-copy with FFmpeg.
        public int TikTokEncodeFps { get; set; } = 120;
        public double TikTokTargetFps { get; set; } = 30.0;
        public string TikTokPreset { get; set; } = "veryslow";
        public int TikTokKeyintSeconds { get; set; } = 1;

        // TikTok CRF (Capped)
        public int TikTokCrf { get; set; } = 15;
        public string TikTokMaxRate { get; set; } = "70M";
        public string TikTokBufSize { get; set; } = "140M";

        // Backward compatible: if set manually, takes priority over TikTokCrf/MaxRate/BufSize
        public string TikTokRateControl { get; set; } = "";

        // Pre-filter input before frame extraction
        public bool EnablePreFilter { get; set; } = false;
        public string PreFilterGraph { get; set; } = "nlmeans=s=3:p=3:r=7";

        // Filters
        public bool EnableEnhanceFilters { get; set; } = true;
        public string EnhanceFilterStyle { get; set; } = "natural";
        public double EnhanceFilterStrength { get; set; } = 0.70;
        public double FinalOutputTimeScale { get; set; } = 2.0;
        public string HardwareAccelerationMode { get; set; } = "cpu";

        // Optional bundled upscaling
        public bool EnableUpscale { get; set; } = false;
        public string UpscaleExecutablePath { get; set; } = "";
        public string UpscaleModelDirectory { get; set; } = "";
        public string UpscaleModelName { get; set; } = "";
        public string UpscaleDisplayName { get; set; } = "";
        public string UpscaleEngine { get; set; } = "none";
        public int UpscaleScale { get; set; } = 1;

        // Optional foreground/person separation.
        public bool EnableSubjectFocus { get; set; } = false;
        public SubjectFocusMode SubjectFocusMode { get; set; } = SubjectFocusMode.Off;
        public double SubjectFocusStrength { get; set; } = 0.70;
        public string SubjectFocusModelPath { get; set; } = "";

        // Audio
        public string AudioArgs { get; set; } = "-c:a aac -b:a 192k";

        public bool VerboseLogging { get; set; } = false;

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

                var (audioStartTime, _) = await GetAudioStartTimeAsync(inputVideoPath, progressCallback, cancellationToken).ConfigureAwait(false);

                string effectiveInput = inputVideoPath;
                if (EnablePreFilter)
                {
                    progressCallback?.Invoke("STAGE:0/0 Pre-filtering input");
                    var (pfPath, pfRes) = await PreFilterInputAsync(inputVideoPath, showConsoleWindows, progressCallback, cancellationToken).ConfigureAwait(false);
                    if (!pfRes.Success) return pfRes;
                    effectiveInput = pfPath;
                    progressCallback?.Invoke("Pre-filter done.");
                }

                int totalStages = 1;
                if (EnableRifeInterpolation) totalStages++;
                if (EnableUpscale) totalStages++;
                if (EnableSubjectFocus && SubjectFocusMode != SubjectFocusMode.Off) totalStages++;
                if (GeneratePc60) totalStages++;
                if (GenerateTikTok30) totalStages += 2;

                int stageNumber = 1;
                var stageRange = GetStageProgressRange(stageNumber, totalStages);
                progressCallback?.Invoke($"STAGE:{stageNumber}/{totalStages} Extracting frames");
                progressCallback?.Invoke($"PROGRESS:{stageRange.Start}%");
                string framePattern = $"frame_%08d.{IntermediateFrameFormat.ToLowerInvariant()}";
                var extract = await ExtractFramesAsync(effectiveInput, inputFramesDir, framePattern, useNvdecForDecoding, showConsoleWindows, progressCallback, cancellationToken, expectedExtractFrames, stageRange.Start, stageRange.End).ConfigureAwait(false);
                if (!extract.Success && useNvdecForDecoding && IsHardwareDecodeFailure(extract))
                {
                    progressCallback?.Invoke("NVDEC decode failed; retrying frame extraction with CPU decode.");
                    ClearFrameDirectory(inputFramesDir);
                    extract = await ExtractFramesAsync(effectiveInput, inputFramesDir, framePattern, useNvdec: false, showConsoleWindows, progressCallback, cancellationToken, expectedExtractFrames, stageRange.Start, stageRange.End).ConfigureAwait(false);
                }
                if (!extract.Success) return extract;
                progressCallback?.Invoke($"PROGRESS:{stageRange.End}%");

                string workingFramesDir = inputFramesDir;
                string workingFramePattern = framePattern;

                if (EnableUpscale && ProcessUpscaleBeforeRife)
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
                    if (!rife.Success) return rife;

                    int inputFrameCount = Directory.GetFiles(workingFramesDir, $"*.{IntermediateFrameFormat.ToLowerInvariant()}").Length;
                    int outputFrameCount = Directory.GetFiles(rifeFramesDir, $"*.{IntermediateFrameFormat.ToLowerInvariant()}").Length;
                    progressCallback?.Invoke($"RIFE frames: input={inputFrameCount}, output={outputFrameCount}");
                    progressCallback?.Invoke($"PROGRESS:{stageRange.End}%");
                    workingFramesDir = rifeFramesDir;
                    workingFramePattern = $"%08d.{IntermediateFrameFormat.ToLowerInvariant()}";
                }

                if (EnableUpscale && !ProcessUpscaleBeforeRife)
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
                    if (!subjectFocusRes.Success) return subjectFocusRes;
                    workingFramesDir = subjectFocusDir;
                    progressCallback?.Invoke($"PROGRESS:{stageRange.End}%");
                }

                string encodeFramesDir = workingFramesDir;

                string outDir = Path.GetDirectoryName(outputVideoPath) ?? "";
                if (!Directory.Exists(outDir) && !string.IsNullOrWhiteSpace(outDir))
                    Directory.CreateDirectory(outDir);

                string baseName = Path.GetFileNameWithoutExtension(outputVideoPath);
                string ext = Path.GetExtension(outputVideoPath);
                if (string.IsNullOrWhiteSpace(ext)) ext = ".mp4";

                string pcOut = Path.Combine(outDir, $"{baseName}_Video 60FPS{ext}");
                string tt120Temp = Path.Combine(_tempSessionDirectory, $"{baseName}_TikTok30_TEMP{ext}");
                string tikTok30PreFinal = Path.Combine(_tempSessionDirectory, $"{baseName}_TikTok30_PRE_FINAL{ext}");
                string tikTok30Out = Path.Combine(outDir, $"{baseName}_TikTok30FPS{ext}");

                int tikTokEncodeFps = EnableRifeInterpolation ? TikTokEncodeFps : (int)Math.Round(TikTokTargetFps);

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

                if (GenerateTikTok30)
                {
                    stageNumber++;
                    stageRange = GetStageProgressRange(stageNumber, totalStages);
                    progressCallback?.Invoke($"STAGE:{stageNumber}/{totalStages} Preparing TikTok30FPS");
                    var ttRes = await EncodeTikTokWithFallbackAsync(tt120Temp, encodeFramesDir, workingFramePattern, workingSequenceFps, inputVideoPath, audioStartTime,
                        tikTokEncodeFps,
                        showConsoleWindows, progressCallback, cancellationToken, durationSeconds, stageRange.Start, stageRange.End).ConfigureAwait(false);
                    if (!ttRes.Success) return ttRes;
                    progressCallback?.Invoke($"PROGRESS:{stageRange.End}%");

                    if (EnableRifeInterpolation)
                    {
                        stageNumber++;
                        stageRange = GetStageProgressRange(stageNumber, totalStages);
                        progressCallback?.Invoke($"STAGE:{stageNumber}/{totalStages} Finalizing TikTok30FPS");
                        var (tempFps, tempFpsResult) = await GetOriginalVideoFpsAsync(tt120Temp, progressCallback, cancellationToken).ConfigureAwait(false);
                        if (tempFps == null || tempFps.Value <= 0)
                            return new ProcessExecutionResult(tempFpsResult.ExitCode, tempFpsResult.StandardOutput, "Failed to determine temporary TikTok FPS: " + tempFpsResult.StandardError);

                        double scaleToTargetFps = tempFps.Value / TikTokTargetFps;
                        int retimeEnd = stageRange.Start + Math.Max(1, (stageRange.End - stageRange.Start) / 2);
                        var retimeRes = await RetimeCopyAsync(tt120Temp, tikTok30PreFinal, scaleToTargetFps, showConsoleWindows, progressCallback, cancellationToken, durationSeconds, stageRange.Start, retimeEnd)
                            .ConfigureAwait(false);
                        if (!retimeRes.Success) return retimeRes;
                    }
                    else
                    {
                        TryCopyFile(tt120Temp, tikTok30PreFinal);
                    }

                    progressCallback?.Invoke($"Applying TikTok final timing pass (itsscale {FinalOutputTimeScale:0.###})");
                    int finalStart = EnableRifeInterpolation
                        ? stageRange.Start + Math.Max(1, (stageRange.End - stageRange.Start) / 2)
                        : stageRange.End;
                    var tikTokFinalRes = await ApplyFinalTimeScaleAsync(tikTok30PreFinal, tikTok30Out, FinalOutputTimeScale, showConsoleWindows, progressCallback, cancellationToken, durationSeconds, finalStart, 100).ConfigureAwait(false);
                    if (!tikTokFinalRes.Success) return tikTokFinalRes;

                    progressCallback?.Invoke("PROGRESS:100%");
                }

                var msg = new StringBuilder();
                msg.AppendLine("Done.");
                if (GeneratePc60) msg.AppendLine("Video 60FPS: " + pcOut);
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

        private string GetEffectiveTikTokRateControl()
        {
            // Keep backward compatibility if older code sets the full rate-control string manually.
            if (!string.IsNullOrWhiteSpace(TikTokRateControl))
                return TikTokRateControl.Trim();

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

        private bool UseNvidiaAcceleration => string.Equals(HardwareAccelerationMode, "nvidia", StringComparison.OrdinalIgnoreCase);
        private bool UseAmdAcceleration => string.Equals(HardwareAccelerationMode, "amd", StringComparison.OrdinalIgnoreCase);

        private string GetPcVideoEncoderArgs(bool forceCpu = false)
        {
            if (!forceCpu && UseNvidiaAcceleration)
                return $"-c:v h264_nvenc -preset {MapNvencPreset(PcPreset)} -profile:v high -pix_fmt yuv420p -cq {Math.Max(1, PcCrf)}";

            if (!forceCpu && UseAmdAcceleration)
                return $"-c:v h264_amf -quality {MapAmfQuality(PcPreset)} -profile:v high -pix_fmt yuv420p -rc cqp -qp_i {Math.Max(1, PcCrf)} -qp_p {Math.Max(1, PcCrf)}";

            return $"-c:v libx264 -crf {PcCrf} -preset {PcPreset} -tune {PcTune} -profile:v high -pix_fmt yuv420p";
        }

        private string GetTikTokVideoEncoderArgs(bool forceCpu = false)
        {
            if (!forceCpu && UseNvidiaAcceleration)
                return $"-c:v h264_nvenc -preset {MapNvencPreset(TikTokPreset)} -profile:v high -pix_fmt yuv420p -cq {Math.Max(1, TikTokCrf)}";

            if (!forceCpu && UseAmdAcceleration)
                return $"-c:v h264_amf -quality {MapAmfQuality(TikTokPreset)} -profile:v high -pix_fmt yuv420p -rc cqp -qp_i {Math.Max(1, TikTokCrf)} -qp_p {Math.Max(1, TikTokCrf)}";

            return $"-c:v libx264 -preset {TikTokPreset} -tune film -profile:v high -pix_fmt yuv420p";
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
            args.Append(GetEffectiveTikTokRateControl() + " ");
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
            args.Append(GetEffectiveTikTokRateControl() + " ");
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
                return new ProcessExecutionResult(-1, "", "Subject Focus canceled.");
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
            Directory.CreateDirectory(outputDir);
            string upscaleLabel = string.IsNullOrWhiteSpace(UpscaleDisplayName) ? UpscaleModelName : UpscaleDisplayName;
            cb?.Invoke($"Upscale model: {upscaleLabel} [{UpscaleModelName}] ({UpscaleScale}x)");
            cb?.Invoke($"PROGRESS:{progressStart}%");

            var argsList = new List<string>
            {
                "-i", QuotePath(inputDir),
                "-o", QuotePath(outputDir),
                "-s", Math.Max(1, UpscaleScale).ToString(CultureInfo.InvariantCulture),
                "-f", IntermediateFrameFormat.ToLowerInvariant(),
                "-j", "1:2:2",
                "-m", QuotePath(UpscaleModelDirectory),
                "-n", UpscaleModelName
            };

            if (string.Equals(UpscaleEngine, "realcugan", StringComparison.OrdinalIgnoreCase))
                argsList.AddRange(new[] { "-c", "3" });

            int expectedFrames = Directory.GetFiles(inputDir, $"*.{IntermediateFrameFormat.ToLowerInvariant()}").Length;
            using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var pollTask = Task.Run(async () =>
            {
                try
                {
                    int lastReportedProgress = -1;
                    int lastReportedFrames = -1;
                    while (!pollCts.Token.IsCancellationRequested)
                    {
                        await Task.Delay(900, pollCts.Token).ConfigureAwait(false);
                        int done = Directory.Exists(outputDir)
                            ? Directory.GetFiles(outputDir, $"*.{IntermediateFrameFormat.ToLowerInvariant()}").Length
                            : 0;

                        if (expectedFrames > 0)
                        {
                            double ratio = Math.Min(1.0, (double)done / expectedFrames);
                            int progress = progressStart + (int)Math.Round((progressEnd - progressStart) * ratio);
                            progress = Math.Clamp(progress, progressStart, progressEnd);
                            if (progress != lastReportedProgress)
                            {
                                cb?.Invoke($"PROGRESS:{progress}%");
                                lastReportedProgress = progress;
                            }
                        }

                        if (done != lastReportedFrames)
                        {
                            cb?.Invoke(expectedFrames > 0
                                ? $"Upscale: {done}/{expectedFrames} frames"
                                : $"Upscale: {done} frames");
                            lastReportedFrames = done;
                        }
                    }
                }
                catch (OperationCanceledException) { }
            }, CancellationToken.None);

            ProcessExecutionResult result;
            try
            {
                result = await RunProcessAsync(UpscaleExecutablePath, string.Join(" ", argsList), showCmd, Path.GetDirectoryName(UpscaleExecutablePath), cb, ct).ConfigureAwait(false);
            }
            finally
            {
                pollCts.Cancel();
                try { await pollTask.ConfigureAwait(false); } catch { }
            }

            int finalDone = Directory.Exists(outputDir)
                ? Directory.GetFiles(outputDir, $"*.{IntermediateFrameFormat.ToLowerInvariant()}").Length
                : 0;
            cb?.Invoke(expectedFrames > 0
                ? $"Upscale: {finalDone}/{expectedFrames} frames"
                : $"Upscale: {finalDone} frames");
            if (result.Success)
                cb?.Invoke($"PROGRESS:{progressEnd}%");

            return result;
        }

        private static void TryCopyFile(string sourcePath, string destinationPath)
        {
            if (File.Exists(sourcePath))
                File.Copy(sourcePath, destinationPath, true);
        }

        private async Task<ProcessExecutionResult> RunProcessAsync(
            string executablePath,
            string arguments,
            bool showWindow,
            string? workingDirectory = null,
            Action<string>? progressCallback = null,
            CancellationToken cancellationToken = default)
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

            if (!showWindow)
            {
                process.OutputDataReceived += (_, e) =>
                {
                    if (e.Data != null)
                    {
                        progressCallback?.Invoke(e.Data);
                        stdOut.AppendLine(e.Data);
                    }
                };
                process.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data != null)
                    {
                        progressCallback?.Invoke("[stderr] " + e.Data);
                        stdErr.AppendLine(e.Data);
                    }
                };
            }

            try { process.Start(); }
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

            if (!showWindow) await Task.Delay(150, CancellationToken.None).ConfigureAwait(false);

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
            int progressEnd)
        {
            string hw = "";
            if (useNvdec && UseNvidiaAcceleration)
                hw = "-hwaccel cuda ";
            else if (UseAmdAcceleration)
                hw = "-hwaccel d3d11va ";

            var args = new StringBuilder();
            args.Append($"-y -nostdin {hw}-i {QuotePath(inputPath)} -vsync vfr ");

            if (IntermediateFrameFormat.Equals("png", StringComparison.OrdinalIgnoreCase) && PngCompressionLevel >= 0)
                args.Append($"-compression_level {PngCompressionLevel} ");

            args.Append("-progress pipe:1 -nostats ");
            args.Append(QuotePath(Path.Combine(outputDir, pattern)));

            return await RunProcessAsync(FfmpegExecutablePath, args.ToString(), showCmd, _tempSessionDirectory, CreateFrameProgressCallback(cb, progressStart, progressEnd, expectedFrames, "Extracting"), ct).ConfigureAwait(false);
        }

        private static bool IsHardwareDecodeFailure(ProcessExecutionResult result)
        {
            string text = (result.StandardError + "\n" + result.StandardOutput).ToLowerInvariant();
            return text.Contains("cuda_error", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("cuvid", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("nvdec", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("device setup failed", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("hardware accelerator failed", StringComparison.OrdinalIgnoreCase);
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
