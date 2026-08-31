using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ForgeFlow
{
    internal static class EditorExportService
    {
        public static Task ExportAsync(EditTimelineModel model, string outputPath, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
            => ExportAsync(model, outputPath, 0, 0, progress, cancellationToken);

        public static Task ExportAsync(EditTimelineModel model, string outputPath, int targetWidth, int targetHeight, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
            => ExportAsync(model, outputPath, targetWidth, targetHeight, 30, 1, 1, 0, 0, false, 0.5, 0.5, 4, 0.28, progress, cancellationToken);

        public static Task ExportAsync(
            EditTimelineModel model, string outputPath, int targetWidth, int targetHeight, double targetFrameRate,
            double videoScaleX, double videoScaleY, double videoOffsetX, double videoOffsetY,
            bool lensEnabled, double lensX, double lensY, double lensZoom, double lensSize,
            IProgress<string>? progress = null, CancellationToken cancellationToken = default)
            => ExportAsync(model, outputPath, new EditorExportSettings
            {
                Width = targetWidth,
                Height = targetHeight,
                FrameRate = targetFrameRate,
                VideoEncoder = "libx264",
                RateControl = "quality",
                Quality = 18,
                Preset = "medium",
                IncludeAudio = true,
                AudioBitrateKbps = 192
            }, videoScaleX, videoScaleY, videoOffsetX, videoOffsetY,
                lensEnabled, lensX, lensY, lensZoom, lensSize, progress, cancellationToken);

        public static async Task ExportAsync(
            EditTimelineModel model, string outputPath, EditorExportSettings settings,
            double videoScaleX, double videoScaleY, double videoOffsetX, double videoOffsetY,
            bool lensEnabled, double lensX, double lensY, double lensZoom, double lensSize,
            IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        {
            settings = settings.Clone();
            settings.Normalize();
            int targetWidth = settings.Width;
            int targetHeight = settings.Height;
            double targetFrameRate = settings.FrameRate;
            string ffmpeg = Path.Combine(AppContext.BaseDirectory, "Tools", "ffmpeg.exe");
            if (!File.Exists(ffmpeg)) throw new FileNotFoundException("لم يتم العثور على FFmpeg المرفق مع ForgeFlow.", ffmpeg);

            var video = model.Clips.Where(c => c.Kind == EditTimelineModel.ClipKind.Video).OrderBy(c => c.Start).ToList();
            if (video.Count == 0) throw new InvalidOperationException("لا يوجد أي مقطع فيديو على المسار V1.");
            var audio = model.Clips.Where(c => c.Kind == EditTimelineModel.ClipKind.Audio).OrderBy(c => c.Start).ToList();
            string source = video[0].Source;
            var overlays = model.Clips.Where(c => c.Kind is EditTimelineModel.ClipKind.Image or EditTimelineModel.ClipKind.Text).OrderBy(c => c.Start).ToList();
            var lenses = model.Clips.Where(c => c.Kind == EditTimelineModel.ClipKind.Lens).OrderBy(c => c.Start).ToList();
            var blurs = model.Clips.Where(c => c.Kind == EditTimelineModel.ClipKind.Blur).OrderBy(c => c.Start).ToList();

            var start = new ProcessStartInfo
            {
                FileName = ffmpeg,
                WorkingDirectory = Path.GetDirectoryName(outputPath) ?? AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            start.ArgumentList.Add("-hide_banner");
            start.ArgumentList.Add("-y");
            start.ArgumentList.Add("-i");
            start.ArgumentList.Add(source);

            var videoInputs = new Dictionary<Guid, int>();
            var audioInputs = new Dictionary<Guid, int>();
            var mediaInputIndexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                [Path.GetFullPath(source)] = 0
            };
            videoInputs[video[0].Id] = 0;
            int inputIndex = 1;
            foreach (var videoClip in video.Skip(1))
            {
                string videoSource = Path.GetFullPath(videoClip.Source);
                if (!mediaInputIndexes.TryGetValue(videoSource, out int index))
                {
                    start.ArgumentList.Add("-i");
                    start.ArgumentList.Add(videoSource);
                    index = inputIndex++;
                    mediaInputIndexes[videoSource] = index;
                }
                videoInputs[videoClip.Id] = index;
            }
            foreach (var audioClip in audio)
            {
                string audioSource = Path.GetFullPath(audioClip.Source);
                if (!mediaInputIndexes.TryGetValue(audioSource, out int index))
                {
                    start.ArgumentList.Add("-i");
                    start.ArgumentList.Add(audioSource);
                    index = inputIndex++;
                    mediaInputIndexes[audioSource] = index;
                }
                audioInputs[audioClip.Id] = index;
            }

            var inputsWithAudio = new HashSet<int>();
            if (settings.IncludeAudio)
            {
                foreach ((string mediaSource, int index) in mediaInputIndexes)
                    if (await HasAudioStreamAsync(mediaSource, cancellationToken)) inputsWithAudio.Add(index);
            }

            var imageInputs = new Dictionary<Guid, int>();
            foreach (var image in overlays.Where(c => c.Kind == EditTimelineModel.ClipKind.Image))
            {
                start.ArgumentList.Add("-loop");
                start.ArgumentList.Add("1");
                start.ArgumentList.Add("-i");
                start.ArgumentList.Add(image.Source);
                imageInputs[image.Id] = inputIndex++;
            }

            double outputDuration = video.Sum(c => c.End - c.Start);
            string filter = BuildFilter(video, audio, overlays, lenses, blurs, videoInputs, audioInputs, imageInputs, inputsWithAudio, targetWidth, targetHeight, targetFrameRate,
                videoScaleX, videoScaleY, videoOffsetX, videoOffsetY, lensEnabled, lensX, lensY, lensZoom, lensSize, settings.IncludeAudio);
            start.ArgumentList.Add("-filter_complex");
            start.ArgumentList.Add(filter);
            start.ArgumentList.Add("-map");
            start.ArgumentList.Add("[finalv]");
            if (settings.IncludeAudio)
            {
                start.ArgumentList.Add("-map");
                start.ArgumentList.Add("[finala]");
            }
            AddVideoEncodingArguments(start, settings);
            if (settings.IncludeAudio)
            {
                start.ArgumentList.Add("-c:a");
                start.ArgumentList.Add("aac");
                start.ArgumentList.Add("-b:a");
                start.ArgumentList.Add($"{settings.AudioBitrateKbps}k");
            }
            else start.ArgumentList.Add("-an");
            if (settings.FastStart)
            {
                start.ArgumentList.Add("-movflags");
                start.ArgumentList.Add("+faststart");
            }
            start.ArgumentList.Add("-shortest");
            start.ArgumentList.Add("-progress");
            start.ArgumentList.Add("pipe:1");
            start.ArgumentList.Add("-nostats");
            start.ArgumentList.Add(outputPath);

            progress?.Report("بدء التصدير...");
            using Process process = Process.Start(start) ?? throw new InvalidOperationException("تعذر تشغيل FFmpeg.");
            using CancellationTokenRegistration registration = cancellationToken.Register(() =>
            {
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // The process may have completed between HasExited and Kill.
                }
            });
            Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            Task progressTask = ReadFfmpegProgressAsync(process.StandardOutput, outputDuration, progress, cancellationToken);
            await Task.WhenAll(process.WaitForExitAsync(cancellationToken), progressTask);
            string stderr = await stderrTask;
            cancellationToken.ThrowIfCancellationRequested();
            if (process.ExitCode != 0)
            {
                string tail = stderr.Length > 3000 ? stderr[^3000..] : stderr;
                throw new InvalidOperationException("فشل تصدير الفيديو.\n" + tail);
            }
            progress?.Report("اكتمل التصدير.");
        }

        private static void AddVideoEncodingArguments(ProcessStartInfo start, EditorExportSettings settings)
        {
            start.ArgumentList.Add("-c:v");
            start.ArgumentList.Add(settings.VideoEncoder);
            start.ArgumentList.Add("-preset");
            start.ArgumentList.Add(settings.IsHardwareEncoder ? settings.Preset switch
            {
                "fast" => "p3",
                "slow" => "p6",
                "quality" => "p7",
                _ => "p5"
            } : settings.Preset switch
            {
                "fast" => "fast",
                "slow" => "slow",
                "quality" => "veryslow",
                _ => "medium"
            });

            if (settings.IsBitrateMode)
            {
                if (settings.IsHardwareEncoder)
                {
                    start.ArgumentList.Add("-rc");
                    start.ArgumentList.Add("vbr");
                }
                start.ArgumentList.Add("-b:v");
                start.ArgumentList.Add(FormatMbps(settings.VideoBitrateMbps));
                start.ArgumentList.Add("-maxrate");
                start.ArgumentList.Add(FormatMbps(settings.MaxBitrateMbps));
                start.ArgumentList.Add("-bufsize");
                start.ArgumentList.Add(FormatMbps(settings.BufferSizeMbps));
            }
            else if (settings.IsHardwareEncoder)
            {
                start.ArgumentList.Add("-rc");
                start.ArgumentList.Add("vbr");
                start.ArgumentList.Add("-cq");
                start.ArgumentList.Add(settings.Quality.ToString(CultureInfo.InvariantCulture));
                start.ArgumentList.Add("-b:v");
                start.ArgumentList.Add("0");
            }
            else
            {
                start.ArgumentList.Add("-crf");
                start.ArgumentList.Add(settings.Quality.ToString(CultureInfo.InvariantCulture));
            }

            start.ArgumentList.Add("-pix_fmt");
            start.ArgumentList.Add(settings.PixelFormat == "yuv420p10le" && settings.IsHardwareEncoder ? "p010le" : settings.PixelFormat);
            if (settings.IsHevc)
            {
                start.ArgumentList.Add("-tag:v");
                start.ArgumentList.Add("hvc1");
            }
            start.ArgumentList.Add("-g");
            start.ArgumentList.Add(Math.Max(1, (int)Math.Round(settings.FrameRate * settings.KeyframeIntervalSeconds)).ToString(CultureInfo.InvariantCulture));
            start.ArgumentList.Add("-metadata:s:v:0");
            start.ArgumentList.Add("encoder=ForgeFlow");
        }

        private static string FormatMbps(double value) => value.ToString("0.###", CultureInfo.InvariantCulture) + "M";

        private static async Task ReadFfmpegProgressAsync(StreamReader reader, double duration, IProgress<string>? progress, CancellationToken cancellationToken)
        {
            double lastPercent = -1;
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                if (line.StartsWith("progress=end", StringComparison.Ordinal))
                {
                    progress?.Report("التصدير: 100% — إنهاء الملف...");
                    continue;
                }

                int separator = line.IndexOf('=');
                if (separator <= 0) continue;
                string key = line[..separator];
                if (key is not ("out_time_us" or "out_time_ms")) continue;
                if (!long.TryParse(line[(separator + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out long microseconds)) continue;
                double percent = Math.Clamp(microseconds / 1_000_000d / Math.Max(0.01, duration) * 100, 0, 99.5);
                if (percent - lastPercent < 0.5) continue;
                lastPercent = percent;
                progress?.Report($"التصدير: {percent:0}% — {TimeSpan.FromSeconds(microseconds / 1_000_000d):hh\\:mm\\:ss} / {TimeSpan.FromSeconds(duration):hh\\:mm\\:ss}");
            }
        }

        private static string BuildFilter(
            IReadOnlyList<EditTimelineModel.Clip> video,
            IReadOnlyList<EditTimelineModel.Clip> audio,
            IReadOnlyList<EditTimelineModel.Clip> overlays,
            IReadOnlyList<EditTimelineModel.Clip> lenses,
            IReadOnlyList<EditTimelineModel.Clip> blurs,
            IReadOnlyDictionary<Guid, int> videoInputs,
            IReadOnlyDictionary<Guid, int> audioInputs,
            IReadOnlyDictionary<Guid, int> imageInputs,
            IReadOnlySet<int> inputsWithAudio,
            int targetWidth,
            int targetHeight,
            double targetFrameRate,
            double videoScaleX,
            double videoScaleY,
            double videoOffsetX,
            double videoOffsetY,
            bool lensEnabled,
            double lensX,
            double lensY,
            double lensZoom,
            double lensSize,
            bool includeAudio)
        {
            var f = new StringBuilder();
            for (int i = 0; i < video.Count; i++)
            {
                var clip = video[i];
                double length = clip.End - clip.Start;
                double sourceStart = clip.SourceOffset;
                double sourceEnd = sourceStart + length;
                string canvasFilter = "";
                if (targetWidth > 0 && targetHeight > 0)
                {
                    double scaleX = Math.Clamp(clip.HasVideoTransform ? clip.VideoScaleX : videoScaleX, 1, 4);
                    double scaleY = Math.Clamp(clip.HasVideoTransform ? clip.VideoScaleY : videoScaleY, 1, 4);
                    double offsetX = Math.Clamp(clip.HasVideoTransform ? clip.VideoOffsetX : videoOffsetX, -1, 1);
                    double offsetY = Math.Clamp(clip.HasVideoTransform ? clip.VideoOffsetY : videoOffsetY, -1, 1);
                    canvasFilter = string.Create(CultureInfo.InvariantCulture,
                        $",scale={targetWidth}:{targetHeight}:force_original_aspect_ratio=increase," +
                        $"scale='max({targetWidth},trunc(iw*{scaleX:0.###}/2)*2)':'max({targetHeight},trunc(ih*{scaleY:0.###}/2)*2)'," +
                        $"crop={targetWidth}:{targetHeight}:" +
                        $"x='max(0,min(iw-ow,(iw-ow)/2-{offsetX:0.###}*(iw-ow)/2))':" +
                        $"y='max(0,min(ih-oh,(ih-oh)/2-{offsetY:0.###}*(ih-oh)/2))'");
                }
                int videoInput = videoInputs[clip.Id];
                f.Append(CultureInfo.InvariantCulture, $"[{videoInput}:v]trim=start={sourceStart:0.###}:end={sourceEnd:0.###},setpts=PTS-STARTPTS{canvasFilter},fps={targetFrameRate:0.###},format=yuv420p,setsar=1,settb=AVTB[v{i}];");
            }
            for (int i = 0; i < video.Count; i++) f.Append($"[v{i}]");
            f.Append($"concat=n={video.Count}:v=1:a=0[basev];");

            double outputDuration = video.Sum(c => c.End - c.Start);
            if (includeAudio)
            {
                f.Append(CultureInfo.InvariantCulture, $"anullsrc=r=48000:cl=stereo,atrim=duration={outputDuration:0.###},asetpts=PTS-STARTPTS[silence];");
                var audioLabels = new List<string> { "silence" };
                int audioSegment = 0;
                foreach (EditTimelineModel.Clip audioClip in audio)
                {
                    int input = audioInputs[audioClip.Id];
                    if (!inputsWithAudio.Contains(input)) continue;

                    double outputStart = ToOutputTime(video, audioClip.Start);
                    double outputEnd = ToOutputTime(video, audioClip.End);
                    double timelineLength = Math.Max(0, outputEnd - outputStart);
                    if (timelineLength <= 0.001) continue;

                    double speed = Math.Clamp(audioClip.Speed, 0.5, 2.0);
                    double sourceLength = timelineLength * speed;
                    int delay = Math.Max(0, (int)Math.Round(outputStart * 1000));
                    double volume = audioClip.IsMuted ? 0 : Math.Clamp(audioClip.Volume, 0, 2);
                    string label = $"aud{audioSegment++}";
                    f.Append(CultureInfo.InvariantCulture,
                        $"[{input}:a]atrim=start={audioClip.SourceOffset:0.###}:duration={sourceLength:0.###}," +
                        $"asetpts=PTS-STARTPTS,atempo={speed:0.###},volume={volume:0.###}," +
                        $"aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,adelay={delay}|{delay}[{label}];");
                    audioLabels.Add(label);
                }
                foreach (string label in audioLabels) f.Append($"[{label}]");
                f.Append($"amix=inputs={audioLabels.Count}:duration=longest:normalize=0,atrim=duration={outputDuration.ToString("0.###", CultureInfo.InvariantCulture)}[finala];");
            }

            string current = "basev";
            int blurLayer = 0;
            foreach (EditTimelineModel.Clip blur in blurs)
            {
                double outputStart = ToOutputTime(video, blur.Start);
                double outputEnd = ToOutputTime(video, blur.End);
                if (outputEnd <= outputStart) continue;
                string next = $"blur{blurLayer++}";
                double strength = Math.Clamp(blur.EffectStrength, 0, 50);
                f.Append(CultureInfo.InvariantCulture,
                    $"[{current}]gblur=sigma={strength:0.###}:steps=2:enable='between(t,{outputStart:0.###},{outputEnd:0.###})'[{next}];");
                current = next;
            }

            int lensLayer = 0;
            foreach (EditTimelineModel.Clip lens in lenses)
            {
                if (targetWidth <= 0 || targetHeight <= 0) break;
                double outputStart = ToOutputTime(video, lens.Start);
                double outputEnd = ToOutputTime(video, lens.End);
                if (outputEnd <= outputStart) continue;
                int n = lensLayer++;
                double zoom = Math.Clamp(lens.LensZoom, 2, 6);
                double size = Math.Clamp(lens.Width, 0.08, 0.60);
                int lensDiameter = Math.Max(64, (int)Math.Round(Math.Min(targetWidth, targetHeight) * size));
                int lensWidth = lensDiameter;
                int lensHeight = lensDiameter;
                int cropWidth = Math.Max(16, (int)Math.Round(lensWidth / zoom));
                int cropHeight = Math.Max(16, (int)Math.Round(lensHeight / zoom));
                int cropX = Math.Clamp((int)Math.Round(targetWidth * lens.X - cropWidth / 2.0), 0, Math.Max(0, targetWidth - cropWidth));
                int cropY = Math.Clamp((int)Math.Round(targetHeight * lens.Y - cropHeight / 2.0), 0, Math.Max(0, targetHeight - cropHeight));
                int overlayX = Math.Clamp((int)Math.Round(targetWidth * lens.X - lensWidth / 2.0), 0, Math.Max(0, targetWidth - lensWidth));
                int overlayY = Math.Clamp((int)Math.Round(targetHeight * lens.Y - lensHeight / 2.0), 0, Math.Max(0, targetHeight - lensHeight));
                f.Append(CultureInfo.InvariantCulture, $"[{current}]split=2[lensbase{n}][lenssource{n}];");
                f.Append(CultureInfo.InvariantCulture, $"[lenssource{n}]crop={cropWidth}:{cropHeight}:{cropX}:{cropY},scale={lensWidth}:{lensHeight}[lenszoomed{n}];");
                f.Append(CultureInfo.InvariantCulture,
                    $"[lensbase{n}][lenszoomed{n}]overlay={overlayX}:{overlayY}:enable='between(t,{outputStart:0.###},{outputEnd:0.###})'[lenscomposite{n}];");
                string next = $"lens{n}";
                f.Append(CultureInfo.InvariantCulture,
                    $"[lenscomposite{n}]drawbox=x={overlayX}:y={overlayY}:w={lensWidth}:h={lensHeight}:color=orange@0.95:t=4:enable='between(t,{outputStart:0.###},{outputEnd:0.###})'[{next}];");
                current = next;
            }

            if (lenses.Count == 0 && lensEnabled && targetWidth > 0 && targetHeight > 0)
            {
                lensZoom = Math.Clamp(lensZoom, 2, 6);
                lensSize = Math.Clamp(lensSize, 0.1, 0.5);
                int lensDiameter = Math.Max(64, (int)Math.Round(Math.Min(targetWidth, targetHeight) * lensSize));
                int lensWidth = lensDiameter;
                int lensHeight = lensDiameter;
                int cropWidth = Math.Max(16, (int)Math.Round(lensWidth / lensZoom));
                int cropHeight = Math.Max(16, (int)Math.Round(lensHeight / lensZoom));
                int cropX = Math.Clamp((int)Math.Round(targetWidth * lensX - cropWidth / 2.0), 0, Math.Max(0, targetWidth - cropWidth));
                int cropY = Math.Clamp((int)Math.Round(targetHeight * lensY - cropHeight / 2.0), 0, Math.Max(0, targetHeight - cropHeight));
                int overlayX = Math.Clamp((int)Math.Round(targetWidth * lensX - lensWidth / 2.0), 0, Math.Max(0, targetWidth - lensWidth));
                int overlayY = Math.Clamp((int)Math.Round(targetHeight * lensY - lensHeight / 2.0), 0, Math.Max(0, targetHeight - lensHeight));
                f.Append(CultureInfo.InvariantCulture, $"[{current}]split=2[lensbasecopy][lenssource];");
                f.Append(CultureInfo.InvariantCulture, $"[lenssource]crop={cropWidth}:{cropHeight}:{cropX}:{cropY},scale={lensWidth}:{lensHeight}[lenszoomed];");
                f.Append(CultureInfo.InvariantCulture, $"[lensbasecopy][lenszoomed]overlay={overlayX}:{overlayY}[lenscomposite];");
                f.Append(CultureInfo.InvariantCulture, $"[lenscomposite]drawbox=x={overlayX}:y={overlayY}:w={lensWidth}:h={lensHeight}:color=orange@0.95:t=4[lenslegacy];");
                current = "lenslegacy";
            }
            int layer = 0;
            foreach (var clip in overlays)
            {
                string next = $"layer{layer++}";
                double outputStart = ToOutputTime(video, clip.Start);
                double outputEnd = ToOutputTime(video, clip.End);
                if (outputEnd <= outputStart) continue;
                if (clip.Kind == EditTimelineModel.ClipKind.Text)
                {
                    string text = EscapeDrawText(clip.Text);
                    string fontFile = ResolveFontFile(clip.FontFamily);
                    double fontSize = clip.FontSize * (targetHeight > 0 ? targetHeight / 1080.0 : 1);
                    f.Append(CultureInfo.InvariantCulture,
                        $"[{current}]drawtext=fontfile='{fontFile}':text='{text}':x=w*{clip.X:0.####}:y=h*{clip.Y:0.####}:fontsize={fontSize:0.#}:fontcolor={clip.Color}:borderw=2:bordercolor=black@0.75:enable='between(t,{outputStart:0.###},{outputEnd:0.###})'[{next}];");
                }
                else
                {
                    int index = imageInputs[clip.Id];
                    int width = Math.Max(16, (int)Math.Round((targetWidth > 0 ? targetWidth : 1920) * clip.Width));
                    int height = Math.Max(16, (int)Math.Round((targetHeight > 0 ? targetHeight : 1080) * clip.Height));
                    f.Append(CultureInfo.InvariantCulture, $"[{index}:v]scale={width}:{height}:force_original_aspect_ratio=decrease[img{layer}];");
                    f.Append(CultureInfo.InvariantCulture,
                        $"[{current}][img{layer}]overlay=x=main_w*{clip.X:0.####}:y=main_h*{clip.Y:0.####}:enable='between(t,{outputStart:0.###},{outputEnd:0.###})'[{next}];");
                }
                current = next;
            }
            f.Append(CultureInfo.InvariantCulture, $"[{current}]null[finalv]");
            return f.ToString();
        }

        private static double ToOutputTime(IReadOnlyList<EditTimelineModel.Clip> clips, double timelineTime)
        {
            double result = 0;
            foreach (var clip in clips)
            {
                if (timelineTime <= clip.Start) break;
                result += Math.Min(timelineTime, clip.End) - clip.Start;
                if (timelineTime < clip.End) break;
            }
            return Math.Max(0, result);
        }

        private static string EscapeDrawText(string value) => value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal)
            .Replace(":", "\\:", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

        private static string ResolveFontFile(string family)
        {
            string file = family switch
            {
                "Segoe UI" => "segoeui.ttf",
                "Tahoma" => "tahoma.ttf",
                "Calibri" => "calibri.ttf",
                "Georgia" => "georgia.ttf",
                "Times New Roman" => "times.ttf",
                "Courier New" => "cour.ttf",
                _ => "arial.ttf"
            };
            return "C\\:/Windows/Fonts/" + file;
        }

        private static async Task<bool> HasAudioStreamAsync(string source, CancellationToken cancellationToken)
        {
            string ffprobe = Path.Combine(AppContext.BaseDirectory, "Tools", "ffprobe.exe");
            if (!File.Exists(ffprobe)) return true;
            var start = new ProcessStartInfo
            {
                FileName = ffprobe, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.ArgumentList.Add("-v"); start.ArgumentList.Add("error");
            start.ArgumentList.Add("-select_streams"); start.ArgumentList.Add("a:0");
            start.ArgumentList.Add("-show_entries"); start.ArgumentList.Add("stream=codec_type");
            start.ArgumentList.Add("-of"); start.ArgumentList.Add("csv=p=0");
            start.ArgumentList.Add(source);
            using Process process = Process.Start(start) ?? throw new InvalidOperationException("تعذر تشغيل FFprobe.");
            string output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode == 0 && output.Contains("audio", StringComparison.OrdinalIgnoreCase);
        }
    }
}
