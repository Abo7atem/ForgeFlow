using System.IO;
using System.Security.Cryptography;
using VideoProcessing;

namespace ForgeFlow
{
    public sealed class ToolCheckResult
    {
        public bool Ready { get; init; }
        public bool ManifestPresent { get; init; }
        public bool ManifestSignaturePresent { get; init; }
        public bool ManifestSignatureValid { get; init; }
        public bool VersionFilePresent { get; init; }
        public bool VersionMatchesAssembly { get; init; }
        public bool RifeModelPresent { get; init; }
        public string Summary { get; init; } = "";
        public string Details { get; init; } = "";
        public string VersionText { get; init; } = "";
        public string FfmpegPath { get; init; } = "";
        public string FfprobePath { get; init; } = "";
        public string RifePath { get; init; } = "";
        public bool TensorRtAvailable { get; init; }
        public string TensorRtDetails { get; init; } = "";
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
            bool manifestSignaturePresent = File.Exists(Path.Combine(toolsDir, "tools.sha256.sig"));
            bool manifestSignatureValid = !manifestSignaturePresent;
            if (manifestPresent)
            {
                var manifestPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string line in File.ReadAllLines(manifestPath))
                {
                    string trimmed = line.Trim();
                    if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("#", StringComparison.Ordinal))
                        continue;

                    string[] parts = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2 || parts[0].Length != 64)
                        continue;

                    string relative = string.Join(" ", parts.Skip(1)).Trim().Replace('/', Path.DirectorySeparatorChar);
                    manifestPaths.Add(relative);
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

                // A manifest that only checks a hand-picked subset is not a
                // complete integrity manifest. Require every shipped base
                // tool/model/script to be represented, while deliberately
                // excluding the optional TensorRT vendor directory and the
                // manifest/signature files themselves.
                foreach (string actualPath in Directory.EnumerateFiles(toolsDir, "*", SearchOption.AllDirectories))
                {
                    string relative = Path.GetRelativePath(toolsDir, actualPath);
                    if (relative.Equals("tools.sha256", StringComparison.OrdinalIgnoreCase)
                        || relative.Equals("tools.sha256.sig", StringComparison.OrdinalIgnoreCase)
                        || relative.StartsWith("tensorrt" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!manifestPaths.Contains(relative))
                        hashFailures.Add(relative + " is not listed in tools.sha256");
                }

                if (manifestSignaturePresent)
                {
                    string? publicKey = Environment.GetEnvironmentVariable("FORGEFLOW_MANIFEST_PUBLIC_KEY");
                    if (string.IsNullOrWhiteSpace(publicKey))
                    {
                        details.Add("Warning: tools.sha256.sig exists but FORGEFLOW_MANIFEST_PUBLIC_KEY is not configured.");
                        manifestSignatureValid = false;
                    }
                    else
                    {
                        manifestSignatureValid = VerifyManifestSignature(manifestPath, Path.Combine(toolsDir, "tools.sha256.sig"), publicKey);
                        details.Add(manifestSignatureValid
                            ? "OK: signed tools.sha256 manifest"
                            : "Manifest signature verification failed.");
                        if (!manifestSignatureValid) hashFailures.Add("tools.sha256.sig invalid");
                    }
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

            TensorRtRuntimeInfo tensorRt = TensorRtRuntime.Detect();
            details.Add(tensorRt.IsAvailable
                ? "OK: " + tensorRt.Message
                : "Optional: " + tensorRt.Message);

            bool ready = missing.Count == 0
                && hashFailures.Count == 0
                && File.Exists(versionPath)
                && versionMatchesAssembly
                && rifeModelPresent
                && (!manifestSignaturePresent || manifestSignatureValid);

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
                ManifestSignaturePresent = manifestSignaturePresent,
                ManifestSignatureValid = manifestSignatureValid,
                Summary = ready
                    ? (manifestPresent ? "Startup ready: tools verified" : "Startup ready: tools found")
                    : "Startup check failed",
                Details = string.Join(Environment.NewLine, details),
                VersionText = versionText,
                FfmpegPath = Path.Combine(toolsDir, "ffmpeg.exe"),
                FfprobePath = Path.Combine(toolsDir, "ffprobe.exe"),
                RifePath = Path.Combine(toolsDir, "rife-ncnn-vulkan.exe"),
                TensorRtAvailable = tensorRt.IsAvailable,
                TensorRtDetails = tensorRt.Message
            };
        }

        private static bool VerifyManifestSignature(string manifestPath, string signaturePath, string publicKey)
        {
            try
            {
                using RSA rsa = RSA.Create();
                if (publicKey.Contains("BEGIN", StringComparison.OrdinalIgnoreCase))
                    rsa.ImportFromPem(publicKey);
                else
                    rsa.FromXmlString(publicKey);
                string encoded = File.ReadAllText(signaturePath).Trim();
                string padded = encoded.Replace('-', '+').Replace('_', '/');
                padded = padded.PadRight(padded.Length + ((4 - padded.Length % 4) % 4), '=');
                byte[] signature = Convert.FromBase64String(padded);
                byte[] content = File.ReadAllBytes(manifestPath);
                return rsa.VerifyData(content, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            }
            catch
            {
                return false;
            }
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
