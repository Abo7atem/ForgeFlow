using System.IO;
using System.Text.Json;

namespace ForgeFlow
{
    internal sealed class AppSettings
    {
        private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ForgeFlow",
            "settings.json");

        public string LastInputDirectory { get; set; } = "";
        public string LastOutputDirectory { get; set; } = "";
        public bool PlayCompletionSound { get; set; } = true;
        public bool ShowProcessingSummary { get; set; } = true;
        public string SelectedUpscaleProfileId { get; set; } = "off";
        // Do not interpolate already-smooth 60 FPS gameplay by default. RIFE is
        // still available as an explicit opt-in for lower-FPS or creative work.
        public bool EnableRife { get; set; } = false;
        public string ProcessingOrder { get; set; } = "rife-then-upscale";
        public string SubjectFocusMode { get; set; } = "off";
        public int SubjectFocusStrength { get; set; } = 70;
        public string VisualStyle { get; set; } = "natural";
        public int VisualStyleStrength { get; set; } = 70;
        public string HardwareAccelerationMode { get; set; } = "cpu";
        // TikTok export controls.  The TikTok profile is the default for the
        // quality-preservation path; Safe H.264 remains available for players
        // that reject non-standard timing metadata.
        public string TikTokProfile { get; set; } = "safe";
        public int TikTokBitrateMbps { get; set; } = 35;
        public int TikTokAudioKbps { get; set; } = 256;
        public int FourKBitrateMbps { get; set; } = 150;
        public bool HasSeenWelcomeScreen { get; set; }

        public static AppSettings Load()
        {
            try
            {
                if (!File.Exists(SettingsPath))
                    return new AppSettings();

                string json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
            catch
            {
                return new AppSettings();
            }
        }

        public void Save()
        {
            string? tempPath = null;
            try
            {
                string? dir = Path.GetDirectoryName(SettingsPath);
                if (!string.IsNullOrWhiteSpace(dir))
                    Directory.CreateDirectory(dir);

                tempPath = SettingsPath + ".tmp." + Environment.ProcessId;
                File.WriteAllText(tempPath, JsonSerializer.Serialize(this, SerializerOptions));
                File.Move(tempPath, SettingsPath, true);
            }
            catch
            {
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
    }
}
