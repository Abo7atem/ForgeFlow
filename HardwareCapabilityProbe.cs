using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ForgeFlow
{
    internal sealed record HardwareCapabilityReport(
        bool NvidiaNvenc,
        bool AmdAmf,
        bool Vulkan,
        int VulkanDeviceId,
        string Details)
    {
        public string Summary => $"GPU probe: NVIDIA NVENC={(NvidiaNvenc ? "OK" : "N/A")}, " +
                                 $"AMD AMF={(AmdAmf ? "OK" : "N/A")}, " +
                                 $"Vulkan/RIFE={(Vulkan ? $"OK (device {VulkanDeviceId})" : "N/A")}. {Details}";
    }

    /// <summary>
    /// Small, real capability probes. Device names alone are not sufficient:
    /// FFmpeg is asked to encode one frame and RIFE is asked to enumerate Vulkan
    /// devices. Results are cached per executable pair for the current session.
    /// </summary>
    internal static class HardwareCapabilityProbe
    {
        private static readonly ConcurrentDictionary<string, HardwareCapabilityReport> Cache = new(StringComparer.OrdinalIgnoreCase);
        public static HardwareCapabilityReport? LastReport { get; private set; }

        public static async Task<HardwareCapabilityReport> ProbeAsync(string ffmpegPath, string rifePath, CancellationToken cancellationToken = default)
        {
            string key = (Path.GetFullPath(ffmpegPath) + "|" + Path.GetFullPath(rifePath)).ToLowerInvariant();
            if (Cache.TryGetValue(key, out HardwareCapabilityReport? cached))
            {
                LastReport = cached;
                return cached;
            }

            ProbeResult nvenc = await RunAsync(ffmpegPath,
                new[] { "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=black:s=256x256:r=1", "-frames:v", "1", "-an", "-c:v", "h264_nvenc", "-f", "null", "-" }, cancellationToken).ConfigureAwait(false);
            ProbeResult amf = await RunAsync(ffmpegPath,
                new[] { "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=black:s=256x256:r=1", "-frames:v", "1", "-an", "-c:v", "h264_amf", "-f", "null", "-" }, cancellationToken).ConfigureAwait(false);
            ProbeResult vulkan = await RunAsync(rifePath, new[] { "-l" }, cancellationToken).ConfigureAwait(false);

            string vulkanText = vulkan.Output;
            // RIFE's -l build prints the device table and may return 1 because
            // it is a listing command rather than an interpolation run.
            bool vulkanUnavailable = vulkanText.Contains("no device", StringComparison.OrdinalIgnoreCase) ||
                                     vulkanText.Contains("no vulkan", StringComparison.OrdinalIgnoreCase) ||
                                     vulkanText.Contains("could not", StringComparison.OrdinalIgnoreCase) ||
                                     vulkanText.Contains("error:", StringComparison.OrdinalIgnoreCase) ||
                                     vulkanText.Contains("failed", StringComparison.OrdinalIgnoreCase);
            bool vulkanOk = !vulkanUnavailable &&
                            (vulkanText.Contains("gpu", StringComparison.OrdinalIgnoreCase) ||
                             vulkanText.Contains("device", StringComparison.OrdinalIgnoreCase) ||
                             vulkanText.Contains("[0 ", StringComparison.OrdinalIgnoreCase));
            int deviceId = ParseFirstDeviceId(vulkanText);
            var report = new HardwareCapabilityReport(
                nvenc.ExitCode == 0,
                amf.ExitCode == 0,
                vulkanOk,
                deviceId,
                $"Capabilities were verified by one-frame encoder probes and RIFE Vulkan enumeration. " +
                $"NVENC: {SummarizeProbe(nvenc, nvenc.ExitCode == 0)} | " +
                $"AMF: {SummarizeProbe(amf, amf.ExitCode == 0)} | " +
                $"Vulkan: {SummarizeText(vulkanText, vulkanOk)}");
            Cache[key] = report;
            LastReport = report;
            return report;
        }

        private static int ParseFirstDeviceId(string output)
        {
            foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = line.TrimStart();
                if (trimmed.StartsWith("[", StringComparison.Ordinal))
                {
                    int close = trimmed.IndexOf(']');
                    if (close > 1 && int.TryParse(trimmed[1..close].Trim(), out int bracketId)) return Math.Max(0, bracketId);
                }
                int colon = trimmed.IndexOf(':');
                if (colon <= 0) continue;
                if (int.TryParse(trimmed[..colon].Trim(), out int id)) return Math.Max(0, id);
            }
            return 0;
        }

        private static string SummarizeProbe(ProbeResult result, bool success)
        {
            return success ? "available" : SummarizeText(result.Output, false);
        }

        private static string SummarizeText(string text, bool success)
        {
            if (success) return "available";
            string compact = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (string.IsNullOrWhiteSpace(compact)) return "no diagnostic returned";
            return compact.Length > 220 ? compact[..220] + "…" : compact;
        }

        private sealed record ProbeResult(int ExitCode, string Output);

        private static async Task<ProbeResult> RunAsync(string executable, string[] arguments, CancellationToken cancellationToken)
        {
            if (!File.Exists(executable)) return new ProbeResult(-1, "missing executable");
            var start = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory
            };
            foreach (string argument in arguments) start.ArgumentList.Add(argument);
            Process? process = null;
            try
            {
                process = Process.Start(start) ?? throw new InvalidOperationException("probe process did not start");
                Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
                Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                return new ProbeResult(process.ExitCode, (await stdout.ConfigureAwait(false)) + "\n" + (await stderr.ConfigureAwait(false)));
            }
            catch (OperationCanceledException)
            {
                try
                {
                    if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
                }
                catch { }
                if (cancellationToken.IsCancellationRequested)
                    throw;
                return new ProbeResult(-1, "probe timeout or canceled");
            }
            catch (Exception ex)
            {
                try
                {
                    if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
                }
                catch { }
                return new ProbeResult(-1, ex.Message);
            }
            finally
            {
                process?.Dispose();
            }
        }
    }
}
