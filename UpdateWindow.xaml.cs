using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Threading;

namespace ForgeFlow
{
    public partial class UpdateWindow : Window
    {
        private readonly UpdateInfo _update;
        private CancellationTokenSource? _downloadCancellation;
        private bool _installerStarted;

        public bool InstallerStarted => _installerStarted;

        public UpdateWindow(UpdateInfo update)
        {
            InitializeComponent();
            NativeWindowTheme.Apply(this);
            _update = update;

            VersionTextBlock.Text =
                $"Current version: {_update.CurrentVersion}\n" +
                $"Latest version: {_update.LatestVersion}\n" +
                $"File: {_update.DownloadFileName} ({FormatFileSize(_update.DownloadSize)})\n" +
                _update.MandatoryReason;

            ReleaseNotesTextBlock.Text = string.IsNullOrWhiteSpace(_update.ReleaseNotes)
                ? "No release notes."
                : _update.ReleaseNotes;
        }

        private async void InstallButton_Click(object sender, RoutedEventArgs e)
        {
            await DownloadAndRunInstallerAsync();
        }

        private void CopyDetailsButton_Click(object sender, RoutedEventArgs e)
        {
            string details =
                VersionTextBlock.Text + Environment.NewLine + Environment.NewLine +
                DownloadStatusTextBlock.Text + Environment.NewLine +
                DownloadDetailTextBlock.Text + Environment.NewLine + Environment.NewLine +
                ReleaseNotesTextBlock.Text;
            Clipboard.SetText(details);
        }

        private async Task DownloadAndRunInstallerAsync()
        {
            if (!IsTrustedInstallerDownloadUrl(_update.DownloadUrl))
            {
                MessageBox.Show(this, "The update download link is missing or not trusted.", "Update Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            InstallButton.IsEnabled = false;
            CancelButton.Content = "Cancel";
            _downloadCancellation?.Dispose();
            _downloadCancellation = new CancellationTokenSource();
            CancellationToken cancellationToken = _downloadCancellation.Token;

            string downloadFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ForgeFlow",
                "Updates");
            Directory.CreateDirectory(downloadFolder);

            string fileName = string.IsNullOrWhiteSpace(_update.DownloadFileName)
                ? "ForgeFlow_Lite_Setup.exe"
                : Path.GetFileName(_update.DownloadFileName);
            if (!IsSafeInstallerFileName(fileName))
            {
                MessageBox.Show(this, "The update file name is not a trusted ForgeFlow setup file.", "Update Error", MessageBoxButton.OK, MessageBoxImage.Error);
                InstallButton.IsEnabled = true;
                CancelButton.Content = "Exit";
                return;
            }

            string filePath = Path.Combine(downloadFolder, fileName);

            try
            {
                if (File.Exists(filePath))
                    File.Delete(filePath);

                DateTime startedAt = DateTime.UtcNow;
                using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
                client.DefaultRequestHeaders.UserAgent.ParseAdd("ForgeFlow-Lite");

                using HttpResponseMessage response = await client.GetAsync(
                    _update.DownloadUrl,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                response.EnsureSuccessStatusCode();
                if (!IsTrustedInstallerDownloadUrl(response.RequestMessage?.RequestUri?.ToString() ?? ""))
                {
                    DownloadStatusTextBlock.Text = "Update download was blocked.";
                    DownloadDetailTextBlock.Text = "The final download URL is not trusted.";
                    InstallButton.IsEnabled = true;
                    CancelButton.Content = "Exit";
                    return;
                }

                long totalBytes = response.Content.Headers.ContentLength ?? _update.DownloadSize;
                long readTotal = 0;
                await using (Stream input = await response.Content.ReadAsStreamAsync(cancellationToken))
                await using (var output = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                {
                    byte[] buffer = new byte[81920];
                    int read;
                    while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        readTotal += read;

                        if (totalBytes > 0)
                        {
                            double progress = Math.Min(100, readTotal * 100d / totalBytes);
                            DownloadProgressBar.Value = progress;
                            DownloadStatusTextBlock.Text =
                                $"Downloading... {FormatFileSize(readTotal)} / {FormatFileSize(totalBytes)} ({progress:0.0}%)";
                            DownloadDetailTextBlock.Text = BuildDownloadDetail(readTotal, totalBytes, startedAt);
                        }
                        else
                        {
                            DownloadStatusTextBlock.Text = $"Downloading... {FormatFileSize(readTotal)}";
                            DownloadDetailTextBlock.Text = BuildDownloadDetail(readTotal, 0, startedAt);
                        }
                    }

                    await output.FlushAsync(cancellationToken);
                }

                if (!VerifyDownloadedInstaller(filePath, out string verifyError))
                {
                    DownloadStatusTextBlock.Text = "Update verification failed.";
                    DownloadDetailTextBlock.Text = "Installer was not started.";
                    TryDelete(filePath);
                    MessageBox.Show(this, verifyError, "Update Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    InstallButton.IsEnabled = true;
                    CancelButton.Content = "Exit";
                    return;
                }

                DownloadProgressBar.Value = 100;
                DownloadStatusTextBlock.Text = "Download complete. Starting setup...";
                DownloadDetailTextBlock.Text = "Verification passed.";

                Process.Start(new ProcessStartInfo
                {
                    FileName = filePath,
                    Arguments = BuildInstallerArguments(),
                    UseShellExecute = true
                });

                _installerStarted = true;
                Application.Current.Shutdown();
                Close();
            }
            catch (OperationCanceledException)
            {
                TryDelete(filePath);
                DownloadStatusTextBlock.Text = "Download canceled.";
                DownloadDetailTextBlock.Text = "No changes were installed.";
                InstallButton.IsEnabled = true;
                CancelButton.Content = "Exit";
            }
            catch (Exception ex)
            {
                TryDelete(filePath);
                DownloadStatusTextBlock.Text = "Download failed.";
                DownloadDetailTextBlock.Text = "Please try again.";
                MessageBox.Show(this, "Could not download or start the update.\n\n" + ex.Message, "Update Error", MessageBoxButton.OK, MessageBoxImage.Error);
                InstallButton.IsEnabled = true;
                CancelButton.Content = "Exit";
            }
        }

        private bool VerifyDownloadedInstaller(string filePath, out string error)
        {
            error = "";

            if (!File.Exists(filePath))
            {
                error = "The update file was not found after download.";
                return false;
            }

            var file = new FileInfo(filePath);
            if (file.Length <= 0)
            {
                error = "The downloaded update file is empty.";
                return false;
            }

            using (FileStream headerStream = File.OpenRead(filePath))
            {
                if (headerStream.ReadByte() != 'M' || headerStream.ReadByte() != 'Z')
                {
                    error = "The downloaded update file is not a valid Windows executable.";
                    return false;
                }
            }

            if (_update.DownloadSize > 0 && file.Length != _update.DownloadSize)
            {
                error =
                    "The downloaded update file size does not match GitHub.\n\n" +
                    $"Expected: {FormatFileSize(_update.DownloadSize)}\n" +
                    $"Actual: {FormatFileSize(file.Length)}";
                return false;
            }

            if (TryGetExpectedSha256(out string expectedSha256))
            {
                using FileStream stream = File.OpenRead(filePath);
                string actualSha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                if (!string.Equals(expectedSha256, actualSha256, StringComparison.OrdinalIgnoreCase))
                {
                    error =
                        "The downloaded update SHA256 does not match GitHub.\n\n" +
                        $"Expected: {expectedSha256}\n" +
                        $"Actual: {actualSha256}";
                    return false;
                }
            }

            return true;
        }

        private static string BuildInstallerArguments()
        {
            string installDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            int processId = Environment.ProcessId;
            return $"--install-dir {QuoteArgument(installDir)} --wait-pid {processId}";
        }

        private static string QuoteArgument(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private static bool IsTrustedInstallerDownloadUrl(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
                return false;

            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                return false;

            string host = uri.Host.ToLowerInvariant();
            bool trustedHost =
                host == "github.com" ||
                host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase) ||
                host == "creators.sa" ||
                host.EndsWith(".creators.sa", StringComparison.OrdinalIgnoreCase) ||
                host == "objects.githubusercontent.com" ||
                host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);

            bool executablePath = uri.AbsolutePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
            bool trustedRedirectHost =
                host == "creators.sa" ||
                host.EndsWith(".creators.sa", StringComparison.OrdinalIgnoreCase) ||
                host == "objects.githubusercontent.com" ||
                host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);

            return trustedHost && (executablePath || trustedRedirectHost);
        }

        private static bool IsSafeInstallerFileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return false;

            if (fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return false;

            string lower = fileName.ToLowerInvariant();
            return lower.EndsWith(".exe", StringComparison.Ordinal) &&
                   lower.Contains("forgeflow", StringComparison.Ordinal) &&
                   (lower.Contains("setup", StringComparison.Ordinal) || lower.Contains("installer", StringComparison.Ordinal));
        }

        private static string BuildDownloadDetail(long readBytes, long totalBytes, DateTime startedAtUtc)
        {
            double seconds = Math.Max(0.5, (DateTime.UtcNow - startedAtUtc).TotalSeconds);
            long bytesPerSecond = (long)(readBytes / seconds);
            if (totalBytes <= 0 || bytesPerSecond <= 0)
                return $"{FormatFileSize(bytesPerSecond)}/s";

            long remainingBytes = Math.Max(0, totalBytes - readBytes);
            TimeSpan eta = TimeSpan.FromSeconds(remainingBytes / Math.Max(1d, bytesPerSecond));
            return $"{FormatFileSize(bytesPerSecond)}/s | about {FormatTime(eta)} left";
        }

        private static string FormatTime(TimeSpan value)
        {
            if (value.TotalHours >= 1)
                return $"{(int)value.TotalHours}h {value.Minutes}m";
            if (value.TotalMinutes >= 1)
                return $"{(int)value.TotalMinutes}m {value.Seconds}s";
            return $"{Math.Max(0, value.Seconds)}s";
        }

        private bool TryGetExpectedSha256(out string sha256)
        {
            sha256 = "";
            string digest = _update.DownloadDigest.Trim();
            const string prefix = "sha256:";
            if (digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                digest = digest[prefix.Length..];

            if (digest.Length != 64)
                return false;

            if (!digest.All(c => char.IsDigit(c) || c is >= 'a' and <= 'f' || c is >= 'A' and <= 'F'))
                return false;

            sha256 = digest.ToLowerInvariant();
            return true;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            if (InstallButton.IsEnabled)
            {
                Close();
                return;
            }

            _downloadCancellation?.Cancel();
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (!_installerStarted && !InstallButton.IsEnabled)
                _downloadCancellation?.Cancel();

            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            _downloadCancellation?.Dispose();
            base.OnClosed(e);
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
            }
        }

        private static string FormatFileSize(long bytes)
        {
            if (bytes <= 0)
                return "unknown size";

            string[] units = { "B", "KB", "MB", "GB" };
            double value = bytes;
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
