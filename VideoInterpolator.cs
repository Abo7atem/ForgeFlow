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
        public string RifeModelName { get; set; } = "rife-v4.26";
        public string RifeGpuId { get; set; } = "auto";
        public string RifeThreads { get; set; } = "1:2:2";
        public bool RifeEnableSpatialTta { get; set; } = false;
        public bool RifeEnableUhdMode { get; set; } = false;

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
            string outputFramesDir = Path.Combine(_tempSessionDirectory, "output_frames");

            try
            {
                Directory.CreateDirectory(inputFramesDir);
                Directory.CreateDirectory(outputFramesDir);

                var (originalFps, fpsResult) = await GetOriginalVideoFpsAsync(inputVideoPath, progressCallback, cancellationToken).ConfigureAwait(false);
                if (originalFps == null)
                    return new ProcessExecutionResult(fpsResult.ExitCode, fpsResult.StandardOutput, "Failed to determine FPS: " + fpsResult.StandardError);

                double rifeFps = originalFps.Value * 2.0; // RIFE x2
                progressCallback?.Invoke($"Original FPS: {originalFps.Value:0.###} -> RIFE FPS: {rifeFps:0.###}");

                var (audioStartTime, _) = await GetAudioStartTimeAsync(inputVideoPath, progressCallback, cancellationToken).ConfigureAwait(false);

                // Stage 0: Pre-filter (optional denoising on compressed video)
                string effectiveInput = inputVideoPath;
                if (EnablePreFilter)
                {
                    progressCallback?.Invoke("STAGE:0/5 Pre-filtering input");
                    var (pfPath, pfRes) = await PreFilterInputAsync(inputVideoPath, showConsoleWindows, progressCallback, cancellationToken).ConfigureAwait(false);
                    if (!pfRes.Success) return pfRes;
                    effectiveInput = pfPath;
                    progressCallback?.Invoke("Pre-filter done.");
                }

                // Stage 1/5 â€“ Extract frames (0â†’20%)
                progressCallback?.Invoke("STAGE:1/5 Extracting frames");
                progressCallback?.Invoke("PROGRESS:0%");
                string framePattern = $"frame_%08d.{IntermediateFrameFormat.ToLowerInvariant()}";
                var extract = await ExtractFramesAsync(effectiveInput, inputFramesDir, framePattern, useNvdecForDecoding, showConsoleWindows, progressCallback, cancellationToken).ConfigureAwait(false);
                if (!extract.Success) return extract;

                // Stage 2/5 â€“ RIFE interpolation (20â†’70%)
                progressCallback?.Invoke("STAGE:2/5 RIFE interpolation");
                progressCallback?.Invoke("PROGRESS:20%");
                int _pollInputCount = Directory.GetFiles(inputFramesDir, $"*.{IntermediateFrameFormat.ToLowerInvariant()}").Length;
                int _pollExpected = _pollInputCount * 2;
                using var _rifePollCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var _rifePollTask = Task.Run(async () =>
                {
                    try
                    {
                        while (!_rifePollCts.Token.IsCancellationRequested)
                        {
                            await Task.Delay(700, _rifePollCts.Token).ConfigureAwait(false);
                            int _d = Directory.GetFiles(outputFramesDir, $"*.{IntermediateFrameFormat.ToLowerInvariant()}").Length;
                            if (_pollExpected > 0)
                            {
                                int _p = 20 + (int)(Math.Min(1.0, (double)_d / _pollExpected) * 50);
                                progressCallback?.Invoke($"PROGRESS:{_p}%");
                            }
                        }
                    }
                    catch (OperationCanceledException) { }
                }, CancellationToken.None);
                var rife = await RunRifeInterpolationAsync(inputFramesDir, outputFramesDir, showConsoleWindows, progressCallback, cancellationToken).ConfigureAwait(false);
                _rifePollCts.Cancel();
                try { await _rifePollTask.ConfigureAwait(false); } catch { }
                if (!rife.Success) return rife;

                int inputFrameCount = Directory.GetFiles(inputFramesDir, $"*.{IntermediateFrameFormat.ToLowerInvariant()}").Length;
                int outputFrameCount = Directory.GetFiles(outputFramesDir, $"*.{IntermediateFrameFormat.ToLowerInvariant()}").Length;
                progressCallback?.Invoke($"RIFE frames: input={inputFrameCount}, output={outputFrameCount}");
                progressCallback?.Invoke("PROGRESS:70%");

                // Outputs
                string outDir = Path.GetDirectoryName(outputVideoPath) ?? "";
                if (!Directory.Exists(outDir) && !string.IsNullOrWhiteSpace(outDir))
                    Directory.CreateDirectory(outDir);

                string baseName = Path.GetFileNameWithoutExtension(outputVideoPath);
                string ext = Path.GetExtension(outputVideoPath);
                if (string.IsNullOrWhiteSpace(ext)) ext = ".mp4";

                string pcOut = Path.Combine(outDir, $"{baseName}_Video 60FPS{ext}");
                string tt120Temp = Path.Combine(_tempSessionDirectory, $"{baseName}_TikTok30_TEMP{ext}");
                string tikTok30Out = Path.Combine(outDir, $"{baseName}_TikTok30FPS{ext}");

                string rifeOutPattern = $"%08d.{IntermediateFrameFormat.ToLowerInvariant()}";

                if (GeneratePc60)
                {
                    progressCallback?.Invoke("STAGE:3/5 Encoding Video 60FPS");
                    var pcRes = await EncodePcAsync(pcOut, outputFramesDir, rifeOutPattern, rifeFps, inputVideoPath, audioStartTime,
                        showConsoleWindows, progressCallback, cancellationToken).ConfigureAwait(false);
                    if (!pcRes.Success) return pcRes;
                    progressCallback?.Invoke("PROGRESS:85%");
                }

                if (GenerateTikTok30)
                {
                    progressCallback?.Invoke("STAGE:4/5 Preparing TikTok30FPS");
                    var ttRes = await EncodeTikTok120Async(tt120Temp, outputFramesDir, rifeOutPattern, rifeFps, inputVideoPath, audioStartTime,
                        showConsoleWindows, progressCallback, cancellationToken).ConfigureAwait(false);
                    if (!ttRes.Success) return ttRes;
                    progressCallback?.Invoke("PROGRESS:95%");

                    progressCallback?.Invoke("STAGE:5/5 Finalizing TikTok30FPS");
                    var (tempFps, tempFpsResult) = await GetOriginalVideoFpsAsync(tt120Temp, progressCallback, cancellationToken).ConfigureAwait(false);
                    if (tempFps == null || tempFps.Value <= 0)
                        return new ProcessExecutionResult(tempFpsResult.ExitCode, tempFpsResult.StandardOutput, "Failed to determine temporary TikTok FPS: " + tempFpsResult.StandardError);

                    double scaleToTargetFps = tempFps.Value / TikTokTargetFps;
                    var retimeRes = await RetimeCopyAsync(tt120Temp, tikTok30Out, scaleToTargetFps, showConsoleWindows, progressCallback, cancellationToken)
                        .ConfigureAwait(false);
                    if (!retimeRes.Success) return retimeRes;

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
            // ظ„ظˆ ط£ظٹ ط¬ط²ط، ظ…ظ† ط§ظ„ظ…ط´ط±ظˆط¹ ظ„ط³ظ‡ ظٹط¶ط¨ط· TikTokRateControl ظٹط¯ظˆظٹظ‹ط§ (ظ‚ط¯ظٹظ…)طŒ ط§ط³طھط®ط¯ظ…ظ‡ ظƒظ…ط§ ظ‡ظˆ ظ„ظ„طھظˆط§ظپظ‚.
            if (!string.IsNullOrWhiteSpace(TikTokRateControl))
                return TikTokRateControl.Trim();

            return $"-crf {TikTokCrf} -maxrate {TikTokMaxRate} -bufsize {TikTokBufSize}";
        }

        private string FilterGraphMild()
        {
            if (!EnableEnhanceFilters)
                return "setsar=1";

            return "setsar=1," +
                   "hqdn3d=0.9:0.7:2.4:2.0," +
                   "deband=1thr=0.012:2thr=0.012:3thr=0.012:4thr=0.012:range=12:blur=0," +
                   "eq=contrast=1.06:brightness=0.006:saturation=1.05," +
                   "unsharp=5:5:0.78";
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
            CancellationToken ct)
        {
            int outFps = PcFps;
            int g = Math.Max(1, outFps * Math.Max(1, PcKeyintSeconds));
            string vf = FilterGraphMild() + $",fps={outFps}";

            var args = new StringBuilder();
            args.Append("-y -nostdin ");

            if (audioOffset.HasValue && audioOffset.Value > 0)
                args.Append($"-itsoffset {audioOffset.Value.ToString(CultureInfo.InvariantCulture)} ");

            args.Append($"-framerate {rifeFps.ToString(CultureInfo.InvariantCulture)} -i {QuotePath(Path.Combine(framesDir, pattern))} ");
            args.Append($"-i {QuotePath(originalVideoPath)} ");
            args.Append("-map 0:v:0 -map 1:a? ");

            args.Append($"-vf \"{vf}\" ");
            args.Append($"-c:v libx264 -crf {PcCrf} -preset {PcPreset} -tune {PcTune} -profile:v high -pix_fmt yuv420p ");
            args.Append($"-g {g} -keyint_min {g} -sc_threshold 0 ");
            args.Append($"{AudioArgs} ");
            args.Append("-movflags +faststart -loglevel warning ");
            args.Append(QuotePath(outputPath));

            return await RunProcessAsync(FfmpegExecutablePath, args.ToString(), showCmd, _tempSessionDirectory, cb, ct).ConfigureAwait(false);
        }

        private async Task<ProcessExecutionResult> EncodeTikTok120Async(
            string outputPath,
            string framesDir,
            string pattern,
            double rifeFps,
            string originalVideoPath,
            double? audioOffset,
            bool showCmd,
            Action<string>? cb,
            CancellationToken ct)
        {
            int outFps = TikTokEncodeFps; // 120
            int g = Math.Max(1, outFps * Math.Max(1, TikTokKeyintSeconds));

            string vf = FilterGraphMild() + $",fps={outFps}";

            var args = new StringBuilder();
            args.Append("-y -nostdin ");

            if (audioOffset.HasValue && audioOffset.Value > 0)
                args.Append($"-itsoffset {audioOffset.Value.ToString(CultureInfo.InvariantCulture)} ");

            args.Append($"-framerate {rifeFps.ToString(CultureInfo.InvariantCulture)} -i {QuotePath(Path.Combine(framesDir, pattern))} ");
            args.Append($"-i {QuotePath(originalVideoPath)} ");
            args.Append("-map 0:v:0 -map 1:a? ");

            args.Append($"-vf \"{vf}\" ");
            args.Append($"-c:v libx264 -preset {TikTokPreset} -tune film -profile:v high -pix_fmt yuv420p ");
            args.Append(GetEffectiveTikTokRateControl() + " ");
            args.Append($"-r {outFps} -vsync cfr ");
            args.Append($"-g {g} -keyint_min {g} -sc_threshold 0 ");
            args.Append($"{AudioArgs} ");
            args.Append("-movflags +faststart -loglevel warning ");
            args.Append(QuotePath(outputPath));

            return await RunProcessAsync(FfmpegExecutablePath, args.ToString(), showCmd, _tempSessionDirectory, cb, ct).ConfigureAwait(false);
        }

        private async Task<ProcessExecutionResult> RetimeCopyAsync(
            string inputPath,
            string outputPath,
            double inputTimestampScale,
            bool showCmd,
            Action<string>? cb,
            CancellationToken ct)
        {
            var args = new StringBuilder();
            args.Append("-y -nostdin ");
            args.Append($"-itsscale {inputTimestampScale.ToString(CultureInfo.InvariantCulture)} ");
            args.Append($"-i {QuotePath(inputPath)} ");
            args.Append("-c:v copy -c:a copy -movflags +faststart -loglevel warning ");
            args.Append(QuotePath(outputPath));

            return await RunProcessAsync(FfmpegExecutablePath, args.ToString(), showCmd, _tempSessionDirectory, cb, ct).ConfigureAwait(false);
        }

        private void LogInternal(string message, bool isError = false)
        {
            if (VerboseLogging)
                Console.WriteLine($"[Interpolator][{(isError ? "ERROR" : "INFO")}] {message}");
        }

        private string QuotePath(string path) => path.Contains(" ") ? $"\"{path}\"" : path;

        private string? ValidateTooling()
        {
            string toolsDir = Path.GetDirectoryName(RifeExecutablePath) ?? "";
            string rifeModelDir = Path.Combine(toolsDir, RifeModelName);
            if (!Directory.Exists(rifeModelDir))
                return $"RIFE model folder not found: {rifeModelDir}";

            return null;
        }

        private async Task<ProcessExecutionResult> RunProcessAsync(
            string executablePath,
            string arguments,
            bool showWindow,
            string? workingDirectory = null,
            Action<string>? progressCallback = null,
            CancellationToken cancellationToken = default)
        {
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

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            if (!showWindow) await Task.Delay(150, CancellationToken.None).ConfigureAwait(false);

            return new ProcessExecutionResult(process.ExitCode, stdOut.ToString(), stdErr.ToString());
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
            string inputPath, string outputDir, string pattern, bool useNvdec, bool showCmd, Action<string>? cb, CancellationToken ct)
        {
            string hw = useNvdec ? "-hwaccel nvdec " : "";
            var args = new StringBuilder();
            args.Append($"-y -nostdin {hw}-i {QuotePath(inputPath)} -vsync vfr ");

            if (IntermediateFrameFormat.Equals("png", StringComparison.OrdinalIgnoreCase) && PngCompressionLevel >= 0)
                args.Append($"-compression_level {PngCompressionLevel} ");

            args.Append(QuotePath(Path.Combine(outputDir, pattern)));

            return await RunProcessAsync(FfmpegExecutablePath, args.ToString(), showCmd, _tempSessionDirectory, cb, ct).ConfigureAwait(false);
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
