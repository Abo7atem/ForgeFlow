using System.IO;
using System.Security.Cryptography;

namespace ForgeFlow
{
    public sealed class ToolCheckResult
    {
        public bool Ready { get; init; }
        public bool ManifestPresent { get; init; }
        public bool VersionFilePresent { get; init; }
        public bool VersionMatchesAssembly { get; init; }
        public bool RifeModelPresent { get; init; }
        public string Summary { get; init; } = "";
        public string Details { get; init; } = "";
        public string VersionText { get; init; } = "";
        public string FfmpegPath { get; init; } = "";
        public string FfprobePath { get; init; } = "";
        public string RifePath { get; init; } = "";
    }

    public static class ToolVerifier
    {
        private static readonly string[] RequiredRelativePaths =
        {
            @"ffmpeg.exe",
            @"ffprobe.exe",
            @"rife-ncnn-vulkan.exe",
            @"rife-v4.26\flownet.bin",
            @"rife-v4.26\flownet.param",
            @"nanovsr\nanovsr_644k_t15_directml.onnx",
            @"nanovsr\LICENSE-NanoVSR.txt"
        };

        public static ToolCheckResult Check(string baseDirectory, string? expectedVersion = null)
        {
            string toolsDir = Path.Combine(baseDirectory, "Tools");
            var details = new List<string>();
            var missing = new List<string>();
            var hashFailures = new List<string>();
            bool versionMatchesAssembly = false;
            string versionText = "";

            string versionPath = Path.Combine(baseDirectory, "version.txt");
            if (File.Exists(versionPath))
            {
                versionText = File.ReadAllText(versionPath).Trim();
                versionMatchesAssembly = string.IsNullOrWhiteSpace(expectedVersion)
                    || string.Equals(versionText, expectedVersion, StringComparison.OrdinalIgnoreCase);

                details.Add(versionMatchesAssembly
                    ? "OK: version.txt (" + versionText + ")"
                    : "Mismatch: version.txt=" + versionText + ", app=" + (expectedVersion ?? "unknown"));
            }
            else
                details.Add("Warning: version.txt is missing.");

            foreach (string relative in RequiredRelativePaths)
            {
                string path = Path.Combine(toolsDir, relative);
                if (!File.Exists(path))
                {
                    missing.Add(relative);
                    details.Add("Missing: " + relative);
                    continue;
                }

                var file = new FileInfo(path);
                details.Add("OK: " + relative + " (" + FormatBytes(file.Length) + ")");
            }

            string manifestPath = Path.Combine(toolsDir, "tools.sha256");
            bool manifestPresent = File.Exists(manifestPath);
            if (manifestPresent)
            {
                foreach (string line in File.ReadAllLines(manifestPath))
                {
                    string trimmed = line.Trim();
                    if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("#", StringComparison.Ordinal))
                        continue;

                    string[] parts = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2 || parts[0].Length != 64)
                        continue;

                    string relative = string.Join(" ", parts.Skip(1)).Trim().Replace('/', Path.DirectorySeparatorChar);
                    string path = Path.Combine(toolsDir, relative);
                    if (!File.Exists(path))
                    {
                        hashFailures.Add(relative + " missing from manifest check");
                        continue;
                    }

                    string actual = ComputeSha256(path);
                    if (!string.Equals(actual, parts[0], StringComparison.OrdinalIgnoreCase))
                        hashFailures.Add(relative + " SHA256 mismatch");
                }
            }
            else
            {
                details.Add("Warning: tools.sha256 manifest is not present.");
            }

            bool rifeModelPresent =
                File.Exists(Path.Combine(toolsDir, @"rife-v4.26\flownet.bin")) &&
                File.Exists(Path.Combine(toolsDir, @"rife-v4.26\flownet.param"));

            if (rifeModelPresent)
                details.Add("OK: rife-v4.26 model");
            else
                details.Add("Missing: rife-v4.26 model");

            bool ready = missing.Count == 0
                && hashFailures.Count == 0
                && File.Exists(versionPath)
                && versionMatchesAssembly
                && rifeModelPresent;

            if (hashFailures.Count > 0)
            {
                details.Add("");
                details.Add("Integrity failures:");
                details.AddRange(hashFailures);
            }

            return new ToolCheckResult
            {
                Ready = ready,
                ManifestPresent = manifestPresent,
                VersionFilePresent = File.Exists(versionPath),
                VersionMatchesAssembly = versionMatchesAssembly,
                RifeModelPresent = rifeModelPresent,
                Summary = ready
                    ? (manifestPresent ? "Startup ready: tools verified" : "Startup ready: tools found")
                    : "Startup check failed",
                Details = string.Join(Environment.NewLine, details),
                VersionText = versionText,
                FfmpegPath = Path.Combine(toolsDir, "ffmpeg.exe"),
                FfprobePath = Path.Combine(toolsDir, "ffprobe.exe"),
                RifePath = Path.Combine(toolsDir, "rife-ncnn-vulkan.exe")
            };
        }

        private static string ComputeSha256(string path)
        {
            using FileStream stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }

        private static string FormatBytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB" };
            double value = Math.Max(0, bytes);
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }
            return $"{value:0.##} {units[unit]}";
        }
    }
}
