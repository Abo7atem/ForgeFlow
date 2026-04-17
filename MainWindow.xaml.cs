using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using ForgeFlow.Security;
using VideoProcessing;

namespace ForgeFlow
{
    public partial class MainWindow : Window
    {
        private static readonly string[] SupportedVideoExtensions =
        {
            ".mp4", ".mov", ".mkv", ".webm", ".avi"
        };

        private CancellationTokenSource? _cts;
        private string? _lastInput;
        private string? _lastOutput;
        private readonly LicenseManager _licenseManager;
        private readonly LicenseGuard _licenseGuard;
        private readonly UpdateChecker _updateChecker;
        private bool _updateGatePassed;

        public MainWindow()
        {
            InitializeComponent();
            _licenseManager = new LicenseManager();
            _licenseGuard = new LicenseGuard(_licenseManager);
            _updateChecker = new UpdateChecker();

            string? tamperMessage = _licenseManager.CheckForDateTamper();
            if (!string.IsNullOrWhiteSpace(tamperMessage))
                MessageBox.Show(this, tamperMessage, "License Warning", MessageBoxButton.OK, MessageBoxImage.Warning);

            Loaded += async (_, _) => await CheckForMandatoryUpdatesAsync(showNoUpdateMessage: false);
        }

        private void BrowseInput_Click(object sender, RoutedEventArgs e)
        {
            var ofd = new OpenFileDialog
            {
                Filter = "Video Files|*.mp4;*.mov;*.mkv;*.webm;*.avi|All Files|*.*"
            };

            if (ofd.ShowDialog() == true)
                SelectInputVideo(ofd.FileName);
        }

        private void BrowseOutput_Click(object sender, RoutedEventArgs e)
        {
            var sfd = new SaveFileDialog
            {
                Filter = "MP4 Video|*.mp4|All Files|*.*",
                DefaultExt = ".mp4",
                FileName = "output.mp4"
            };

            if (sfd.ShowDialog() == true)
                OutputTextBox.Text = sfd.FileName;
        }

        private async void StartButton_Click(object sender, RoutedEventArgs e)
        {
            string input = InputTextBox.Text.Trim();
            string output = OutputTextBox.Text.Trim();

            _lastInput = input;
            _lastOutput = output;

            LogsTextBox.Clear();
            MainProgressBar.Value = 0;
            PercentLabel.Text = "0%";
            StageLabel.Text = "Starting...";

            if (!File.Exists(input))
            {
                FailUI("ملف الإدخال غير موجود.");
                return;
            }

            if (string.IsNullOrWhiteSpace(output))
            {
                FailUI("حدد مسار الإخراج.");
                return;
            }

            if (!await EnsureUpdateGateAsync())
                return;

            if (!EnsureAccessBeforeProcessing())
                return;

            LicenseGuard.GuardSession? guardSession = _licenseGuard.BeginProcessingSession(out GuardDecision startDecision);
            if (guardSession == null)
            {
                FailUI(startDecision.Message);
                return;
            }

            string? outDir = Path.GetDirectoryName(output);
            if (!string.IsNullOrWhiteSpace(outDir) && !Directory.Exists(outDir))
                Directory.CreateDirectory(outDir);

            SetBusy(true);
            RetryButton.IsEnabled = false;

            _cts = new CancellationTokenSource();

            try
            {
                if (!AdvanceLicenseCheckpoint(guardSession, "preflight")) return;

                string toolsDir = Path.Combine(AppContext.BaseDirectory, "Tools");
                string ffmpeg = Path.Combine(toolsDir, "ffmpeg.exe");
                string ffprobe = Path.Combine(toolsDir, "ffprobe.exe");
                string rife = Path.Combine(toolsDir, "rife-ncnn-vulkan.exe");

                if (!File.Exists(ffmpeg)) throw new FileNotFoundException("ffmpeg.exe not found in Tools", ffmpeg);
                if (!File.Exists(ffprobe)) throw new FileNotFoundException("ffprobe.exe not found in Tools", ffprobe);
                if (!File.Exists(rife)) throw new FileNotFoundException("rife-ncnn-vulkan.exe not found in Tools", rife);

                if (!AdvanceLicenseCheckpoint(guardSession, "tools-verified")) return;

                var interpolator = new VideoInterpolator(rife, ffmpeg, ffprobe)
                {
                    // RIFE — FIX: use "0" for RTX 5070 instead of "auto"
                    RifeModelName = "rife-v4.26",
                    RifeThreads = "1:2:2",
                    RifeGpuId = "0",
                    RifeEnableSpatialTta = false,
                    RifeEnableUhdMode = false,

                    // Frames
                    IntermediateFrameFormat = "png",
                    PngCompressionLevel = 1,

                    // Outputs
                    GeneratePc60 = true,
                    GenerateTikTok30 = true,

                    // PC output
                    PcFps = 60,
                    PcCrf = 13,
                    PcPreset = "veryslow",
                    PcTune = "film",
                    PcKeyintSeconds = 1,

                    // TikTok base output
                    TikTokEncodeFps = 120,
                    TikTokTargetFps = 30,
                    TikTokPreset = "veryslow",
                    TikTokKeyintSeconds = 1,

                    // FIX: set CRF via dedicated properties — no manual TikTokRateControl string
                    TikTokCrf = 15,
                    TikTokMaxRate = "50M",
                    TikTokBufSize = "100M",

                    // Pre-filter: disabled by default
                    EnablePreFilter = false,
                    PreFilterGraph = "nlmeans=s=3:p=3:r=7",

                    // Filters
                    EnableEnhanceFilters = true,

                    // Logging
                    VerboseLogging = false
                };

                AppendLog("Started");
                AppendLog("Input: " + Path.GetFileName(input));
                AppendLog("Output folder: " + (outDir ?? AppContext.BaseDirectory));
                if (!AdvanceLicenseCheckpoint(guardSession, "processing-started")) return;

                var res = await interpolator.InterpolateVideoAsync(
                    inputVideoPath: input,
                    outputVideoPath: output,
                    showConsoleWindows: false,
                    useNvdecForDecoding: false,
                    progressCallback: msg => Dispatcher.Invoke(() => AppendLog(msg)),
                    cancellationToken: _cts.Token);

                if (!res.Success)
                {
                    FailUI("فشل:\n" + (string.IsNullOrWhiteSpace(res.StandardError) ? res.StandardOutput : res.StandardError));
                    RetryButton.IsEnabled = true;
                    return;
                }

                if (!AdvanceLicenseCheckpoint(guardSession, "outputs-created")) return;
                AppendLog(res.StandardOutput);
                if (!_licenseGuard.CompleteProcessingSession(guardSession, out string completeMessage))
                {
                    FailUI(completeMessage);
                    RetryButton.IsEnabled = true;
                    return;
                }
                MainProgressBar.Value = 100;
                StageLabel.Text = "Done ✓";
                PercentLabel.Text = "100%";

                MessageBox.Show(
                    "تم بنجاح!\n\n" +
                    "الملفات المنتجة:\n" +
                    "  Video 60FPS.mp4\n" +
                    "  TikTok30FPS.mp4",
                    "OK",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (OperationCanceledException)
            {
                AppendLog("Canceled by user.");
                MessageBox.Show("تم إلغاء العملية.", "Canceled", MessageBoxButton.OK, MessageBoxImage.Information);
                RetryButton.IsEnabled = true;
            }
            catch (Exception ex)
            {
                AppendLog("CRITICAL: " + ex);
                FailUI("خطأ:\n" + ex.Message);
                RetryButton.IsEnabled = true;
            }
            finally
            {
                SetBusy(false);
                _cts?.Dispose();
                _cts = null;
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
        }

        private void RetryButton_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(_lastInput)) InputTextBox.Text = _lastInput;
            if (!string.IsNullOrWhiteSpace(_lastOutput)) OutputTextBox.Text = _lastOutput;
            StartButton_Click(sender, e);
        }

        private async void ActivationButton_Click(object sender, RoutedEventArgs e)
        {
            if (!await EnsureUpdateGateAsync())
                return;

            OpenActivationWindow();
        }

        private async void UpdateButton_Click(object sender, RoutedEventArgs e)
        {
            await CheckForMandatoryUpdatesAsync(showNoUpdateMessage: true);
        }

        private void SocialButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string url)
                OpenUpdateUrl(url);
        }

        private void SetBusy(bool busy)
        {
            StartButton.IsEnabled = !busy;
            CancelButton.IsEnabled = busy;
            ActivationButton.IsEnabled = !busy;
            UpdateButton.IsEnabled = !busy;
        }

        private bool EnsureAccessBeforeProcessing()
        {
            GuardDecision decision = _licenseGuard.EvaluateAccess();
            if (decision.Allowed)
            {
                if (decision.Mode == LicenseAccessMode.Trial)
                {
                    int remainingAfterThis = Math.Max(0, decision.Remaining - 1);
                    var confirm = MessageBox.Show(
                        this,
                        $"Trial mode: this will use one free run.\nRemaining after this video: {remainingAfterThis} of {TrialManager.MaxTrialVideos}.\n\nContinue?",
                        "ForgeFlow Trial",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Information);
                    return confirm == MessageBoxResult.Yes;
                }

                return true;
            }

            var openActivation = MessageBox.Show(
                this,
                decision.Message + "\n\nDo you want to open activation now?",
                "Activation Required",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (openActivation == MessageBoxResult.Yes)
            {
                OpenActivationWindow();
                return _licenseGuard.EvaluateAccess().Allowed;
            }

            return false;
        }

        private void OpenActivationWindow()
        {
            var window = new LicenseWindow(_licenseManager)
            {
                Owner = this
            };
            window.ShowDialog();
        }

        private bool AdvanceLicenseCheckpoint(LicenseGuard.GuardSession? session, string checkpoint)
        {
            if (_licenseGuard.AdvanceProcessingSession(session, checkpoint, out string message))
                return true;

            _cts?.Cancel();
            FailUI(message);
            RetryButton.IsEnabled = true;
            return false;
        }

        private async Task<bool> EnsureUpdateGateAsync()
        {
            if (_updateGatePassed)
                return true;

            bool ok = await CheckForMandatoryUpdatesAsync(showNoUpdateMessage: true);
            if (!ok)
            {
                MessageBox.Show(
                    this,
                    "ForgeFlow Lite could not verify mandatory updates. Processing is blocked until the update check succeeds.",
                    "Update Check Required",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            return ok;
        }

        private async Task<bool> CheckForMandatoryUpdatesAsync(bool showNoUpdateMessage)
        {
            try
            {
                UpdateInfo? update = await _updateChecker.CheckForUpdatesAsync();
                if (update == null)
                {
                    _updateGatePassed = true;
                    if (showNoUpdateMessage)
                    {
                        MessageBox.Show(this, "ForgeFlow Lite is up to date.", "Updates", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    return true;
                }

                string message =
                    "A mandatory ForgeFlow Lite update is required.\n\n" +
                    $"Current version: {update.CurrentVersion}\n" +
                    $"Latest version: {update.LatestVersion}\n" +
                    $"Published: {update.PublishedDate:yyyy-MM-dd}\n\n" +
                    update.MandatoryReason + "\n\n" +
                    "The download page will open now. The app will close until the update is installed.";

                MessageBox.Show(this, message, "Mandatory Update", MessageBoxButton.OK, MessageBoxImage.Warning);
                OpenUpdateUrl(update.DownloadUrl);
                Application.Current.Shutdown();
                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Update check failed: " + ex.Message);
                _updateGatePassed = false;
                if (showNoUpdateMessage)
                {
                    MessageBox.Show(this, "Could not check for updates. Please try again later.", "Updates", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                return false;
            }
        }

        private static void OpenUpdateUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return;

            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }

        private void Window_PreviewDragOver(object sender, DragEventArgs e)
        {
            e.Effects = GetDroppedVideoPath(e) == null ? DragDropEffects.None : DragDropEffects.Copy;
            e.Handled = true;
        }

        private void Window_PreviewDrop(object sender, DragEventArgs e)
        {
            string? videoPath = GetDroppedVideoPath(e);
            if (videoPath == null)
            {
                AppendLog("Unsupported file. Drop a video file.");
                e.Handled = true;
                return;
            }

            SelectInputVideo(videoPath);
            AppendLog("Selected: " + Path.GetFileName(videoPath));
            AppendLog("Output: " + OutputTextBox.Text);
            e.Handled = true;
        }

        private void SelectInputVideo(string videoPath)
        {
            InputTextBox.Text = videoPath;
            OutputTextBox.Text = GetDefaultOutputPath(videoPath);
        }

        private static string GetDefaultOutputPath(string inputPath)
        {
            string directory = Path.GetDirectoryName(inputPath) ?? AppContext.BaseDirectory;
            string name = Path.GetFileNameWithoutExtension(inputPath);
            return Path.Combine(directory, name + "_ForgeFlow.mp4");
        }

        private static string? GetDroppedVideoPath(DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop))
                return null;

            if (e.Data.GetData(DataFormats.FileDrop) is not string[] files)
                return null;

            return files.FirstOrDefault(IsSupportedVideoFile);
        }

        private static bool IsSupportedVideoFile(string path)
        {
            return File.Exists(path) &&
                   SupportedVideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
        }

        private void AppendLog(string line)
        {
            var msg = line ?? "";

            if (msg.Contains('\n') || msg.Contains('\r'))
            {
                foreach (string part in msg.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries))
                    AppendLog(part);
                return;
            }

            // ── Progress bar update (silent) ──────────────────────────
            if (msg.StartsWith("PROGRESS:"))
            {
                if (int.TryParse(msg.Replace("PROGRESS:", "").Replace("%", "").Trim(), out int pct))
                {
                    MainProgressBar.Value = pct;
                    PercentLabel.Text = $"{pct}%";
                }
                return;
            }

            // ── Stage label update ────────────────────────────────────
            if (msg.StartsWith("STAGE:"))
            {
                StageLabel.Text = msg.Replace("STAGE:", "").Trim();
                LogsTextBox.AppendText(StageLabel.Text + Environment.NewLine);
                LogsTextBox.ScrollToEnd();
                return;
            }

            // ── stderr lines: only show errors ────────────────────────
            if (msg.StartsWith("[stderr]"))
            {
                var inner = msg.Replace("[stderr]", "").Trim();
                bool isError = inner.StartsWith("Error") || inner.StartsWith("error")
                            || inner.Contains("No such file")
                            || inner.Contains("Invalid")
                            || inner.Contains("failed")
                            || inner.Contains("Cannot")
                            || inner.StartsWith("Warning");
                if (isError)
                    LogsTextBox.AppendText("  " + inner + Environment.NewLine);
                LogsTextBox.ScrollToEnd();
                return;
            }

            // ── RIFE frame count ──────────────────────────────────────
            if (msg.StartsWith("RIFE frames:"))
            {
                string clean = msg
                    .Replace("RIFE frames: input=", "Frames: ")
                    .Replace(", output=", " -> ");
                LogsTextBox.AppendText(clean + Environment.NewLine);
                LogsTextBox.ScrollToEnd();
                return;
            }

            // ── Skip pre-filter done (already shown in stage) ─────────
            if (msg == "Pre-filter done.") return;

            // ── Everything else ───────────────────────────────────────
            if (IsImportantLogMessage(msg))
            {
                LogsTextBox.AppendText(msg + Environment.NewLine);
                LogsTextBox.ScrollToEnd();
            }
        }

        private static bool IsImportantLogMessage(string msg)
        {
            if (string.IsNullOrWhiteSpace(msg))
                return false;

            return msg == "Started"
                || msg == "Done."
                || msg.StartsWith("Input:")
                || msg.StartsWith("Output:")
                || msg.StartsWith("Output folder:")
                || msg.StartsWith("Selected:")
                || msg.StartsWith("Video 60FPS:")
                || msg.StartsWith("TikTok30FPS:")
                || msg.StartsWith("ERROR:")
                || msg.StartsWith("WARNING:")
                || msg.StartsWith("Unsupported file")
                || msg.StartsWith("Canceled")
                || msg.StartsWith("CRITICAL:");
        }

        private void FailUI(string msg)
        {
            AppendLog("ERROR: " + msg);
            MessageBox.Show(msg, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        protected override void OnClosed(EventArgs e)
        {
            _updateChecker.Dispose();
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            base.OnClosed(e);
        }
    }
}
