using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace ForgeFlow
{
    public sealed class VideoInfo
    {
        public int Width { get; init; }
        public int Height { get; init; }
        public double Fps { get; init; }
        public double DurationSeconds { get; init; }
        public long SizeBytes { get; init; }
        public bool HasAudio { get; init; }

        public string Summary =>
            $"{Width}x{Height} | {Fps:0.##} FPS | {FormatDuration(DurationSeconds)} | {FormatBytes(SizeBytes)} | Audio: {(HasAudio ? "Yes" : "No")}";

        public string Estimate =>
            $"Estimated temp space: {FormatBytes(EstimateRequiredBytes(SizeBytes))} | Time: {EstimateTime(DurationSeconds)}";

        public static long EstimateRequiredBytes(long inputSize) => Math.Max(5L * 1024 * 1024 * 1024, inputSize * 8);

        public static string FormatDuration(double seconds)
        {
            if (seconds <= 0) return "unknown";
            var value = TimeSpan.FromSeconds(seconds);
            return value.TotalHours >= 1 ? $"{(int)value.TotalHours}h {value.Minutes}m {value.Seconds}s" : $"{value.Minutes}m {value.Seconds}s";
        }

        public static string FormatBytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double value = Math.Max(0, bytes);
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }
            return $"{value:0.##} {units[unit]}";
        }

        private static string EstimateTime(double seconds)
        {
            if (seconds <= 0) return "depends on GPU";
            double estimatedMinutes = Math.Max(1, seconds / 60d * 2.5d);
            return estimatedMinutes >= 60 ? $"{estimatedMinutes / 60d:0.#}h approx" : $"{estimatedMinutes:0.#}m approx";
        }
    }

    public static class VideoInfoReader
    {
        public static async Task<VideoInfo?> ReadAsync(string ffprobePath, string videoPath, CancellationToken cancellationToken = default)
        {
            if (!File.Exists(ffprobePath) || !File.Exists(videoPath))
                return null;

            string args = "-v error -show_format -show_streams -of json " + QuotePath(videoPath);
            var psi = new ProcessStartInfo
            {
                FileName = ffprobePath,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = psi };
            process.Start();
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            string json = await stdoutTask.ConfigureAwait(false);
            await stderrTask.ConfigureAwait(false);
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(json))
                return null;

            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            int width = 0;
            int height = 0;
            double fps = 0;
            bool hasAudio = false;

            if (root.TryGetProperty("streams", out JsonElement streams))
            {
                foreach (JsonElement stream in streams.EnumerateArray())
                {
                    string type = stream.TryGetProperty("codec_type", out JsonElement codecType) ? codecType.GetString() ?? "" : "";
                    if (type == "video" && width == 0)
                    {
                        width = stream.TryGetProperty("width", out JsonElement widthValue) ? widthValue.GetInt32() : 0;
                        height = stream.TryGetProperty("height", out JsonElement heightValue) ? heightValue.GetInt32() : 0;
                        string rate = stream.TryGetProperty("avg_frame_rate", out JsonElement avgRate) ? avgRate.GetString() ?? "" : "";
                        fps = ParseFps(rate);
                    }
                    else if (type == "audio")
                    {
                        hasAudio = true;
                    }
                }
            }

            double duration = 0;
            if (root.TryGetProperty("format", out JsonElement format) &&
                format.TryGetProperty("duration", out JsonElement durationValue))
            {
                double.TryParse(durationValue.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out duration);
            }

            return new VideoInfo
            {
                Width = width,
                Height = height,
                Fps = fps,
                DurationSeconds = duration,
                SizeBytes = new FileInfo(videoPath).Length,
                HasAudio = hasAudio
            };
        }

        private static double ParseFps(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return 0;
            if (value.Contains('/'))
            {
                string[] parts = value.Split('/');
                if (parts.Length == 2 &&
                    double.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out double num) &&
                    double.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out double den) &&
                    den != 0)
                    return num / den;
            }

            return double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out double fps) ? fps : 0;
        }

        private static string QuotePath(string path) => path.Contains(' ') ? $"\"{path}\"" : path;
    }
}
