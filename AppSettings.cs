using System.IO;
using System.Text.Json;

namespace ForgeFlow
{
    internal sealed class AppSettings
    {
        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ForgeFlow",
            "settings.json");

        public string LastInputDirectory { get; set; } = "";
        public string LastOutputDirectory { get; set; } = "";
        public bool PlayCompletionSound { get; set; } = true;
        public bool ShowProcessingSummary { get; set; } = true;
        public string SelectedUpscaleProfileId { get; set; } = "off";
        public bool EnableRife { get; set; } = true;
        public string ProcessingOrder { get; set; } = "rife-then-upscale";
        public string SubjectFocusMode { get; set; } = "off";
        public int SubjectFocusStrength { get; set; } = 70;
        public string HardwareAccelerationMode { get; set; } = "cpu";
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
            try
            {
                string? dir = Path.GetDirectoryName(SettingsPath);
                if (!string.IsNullOrWhiteSpace(dir))
                    Directory.CreateDirectory(dir);

                File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch
            {
            }
        }
    }
}
