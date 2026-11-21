using System;
using System.IO;
using System.Text.Json;

namespace ForgeFlow
{
    internal sealed class EditorExportSettings
    {
        public int Width { get; set; } = 1920;
        public int Height { get; set; } = 1080;
        public double FrameRate { get; set; } = 30;
        public string VideoEncoder { get; set; } = "libx264";
        public string RateControl { get; set; } = "bitrate";
        public double VideoBitrateMbps { get; set; } = 16;
        public double MaxBitrateMbps { get; set; } = 24;
        public double BufferSizeMbps { get; set; } = 32;
        public int Quality { get; set; } = 18;
        public string Preset { get; set; } = "medium";
        public string PixelFormat { get; set; } = "yuv420p";
        public int KeyframeIntervalSeconds { get; set; } = 2;
        public bool IncludeAudio { get; set; } = true;
        public int AudioBitrateKbps { get; set; } = 192;
        public bool FastStart { get; set; } = true;

        public bool IsHardwareEncoder => VideoEncoder is "h264_nvenc" or "hevc_nvenc" or "h264_amf" or "hevc_amf";
        public bool IsHevc => VideoEncoder is "libx265" or "hevc_nvenc" or "hevc_amf";
        public bool IsBitrateMode => string.Equals(RateControl, "bitrate", StringComparison.OrdinalIgnoreCase);

        public EditorExportSettings Clone() => (EditorExportSettings)MemberwiseClone();

        public void Normalize()
        {
            Width = MakeEven(Math.Clamp(Width, 64, 7680));
            Height = MakeEven(Math.Clamp(Height, 64, 4320));
            FrameRate = Math.Clamp(FrameRate, 15, 120);
            if (VideoEncoder is not ("libx264" or "libx265" or "h264_nvenc" or "hevc_nvenc" or "h264_amf" or "hevc_amf")) VideoEncoder = "libx264";
            RateControl = IsBitrateMode ? "bitrate" : "quality";
            VideoBitrateMbps = Math.Clamp(VideoBitrateMbps, 0.5, 300);
            MaxBitrateMbps = Math.Clamp(Math.Max(MaxBitrateMbps, VideoBitrateMbps), VideoBitrateMbps, 400);
            BufferSizeMbps = Math.Clamp(Math.Max(BufferSizeMbps, MaxBitrateMbps), MaxBitrateMbps, 800);
            Quality = Math.Clamp(Quality, 0, 51);
            Preset = Preset is "fast" or "medium" or "slow" or "quality" ? Preset : "medium";
            PixelFormat = PixelFormat == "yuv420p10le" && IsHevc ? "yuv420p10le" : "yuv420p";
            KeyframeIntervalSeconds = Math.Clamp(KeyframeIntervalSeconds, 1, 10);
            AudioBitrateKbps = Math.Clamp(AudioBitrateKbps, 64, 512);
        }

        public static double RecommendedBitrateMbps(int width, int height, double fps)
        {
            double pixelsPerSecond = Math.Max(1, width) * Math.Max(1, height) * Math.Max(15, fps);
            double value = pixelsPerSecond / (1920d * 1080d * 30d) * 12d;
            return Math.Round(Math.Clamp(value, 4, 120), 1);
        }

        public double EstimateSizeMegabytes(double durationSeconds)
        {
            double videoMbps = IsBitrateMode ? VideoBitrateMbps : RecommendedBitrateMbps(Width, Height, FrameRate);
            double audioMbps = IncludeAudio ? AudioBitrateKbps / 1000d : 0;
            return Math.Max(0, durationSeconds) * (videoMbps + audioMbps) / 8d;
        }

        public static EditorExportSettings Load(int width, int height, double frameRate)
        {
            EditorExportSettings settings;
            try
            {
                string path = SettingsPath;
                settings = File.Exists(path)
                    ? JsonSerializer.Deserialize<EditorExportSettings>(File.ReadAllText(path)) ?? new EditorExportSettings()
                    : new EditorExportSettings();
            }
            catch
            {
                settings = new EditorExportSettings();
            }

            settings.Width = width > 0 ? width : settings.Width;
            settings.Height = height > 0 ? height : settings.Height;
            settings.FrameRate = frameRate > 0 ? frameRate : settings.FrameRate;
            if (!File.Exists(SettingsPath))
            {
                settings.VideoBitrateMbps = RecommendedBitrateMbps(settings.Width, settings.Height, settings.FrameRate);
                settings.MaxBitrateMbps = Math.Round(settings.VideoBitrateMbps * 1.5, 1);
                settings.BufferSizeMbps = Math.Round(settings.VideoBitrateMbps * 2, 1);
            }
            settings.Normalize();
            return settings;
        }

        public void Save()
        {
            string? tempPath = null;
            try
            {
                Normalize();
                string? directory = Path.GetDirectoryName(SettingsPath);
                if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
                tempPath = SettingsPath + ".tmp." + Environment.ProcessId;
                File.WriteAllText(tempPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(tempPath, SettingsPath, true);
            }
            catch
            {
                // Export must not fail because preferences could not be persisted.
            }
            finally
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(tempPath) && File.Exists(tempPath)) File.Delete(tempPath);
                }
                catch { }
            }
        }

        private static int MakeEven(int value) => value % 2 == 0 ? value : value - 1;
        private static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ForgeFlow", "editor-export.json");
    }
}
