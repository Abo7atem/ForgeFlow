using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
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
        private bool _updateCheckInProgress;
        private ToolCheckResult? _toolStatus;
        private VideoInfo? _selectedVideoInfo;
        private CancellationTokenSource? _videoInfoCts;
        private readonly DispatcherTimer _runnerTimer;
        private int _runnerFrame;
        private static readonly BitmapImage RunnerImage = LoadEmojiImage("runner.png");
        private static readonly BitmapImage RunnerManImage = LoadEmojiImage("runner_man.png");
        private static readonly BitmapImage PartyImage = LoadEmojiImage("party.png");
        private static readonly BitmapImage DizzyImage = LoadEmojiImage("dizzy.png");
        private static readonly BitmapImage ReliefImage = LoadEmojiImage("relief.png");
        private static readonly BitmapImage StopImage = LoadEmojiImage("stop.png");

        public MainWindow()
        {
            InitializeComponent();
            _licenseManager = new LicenseManager();
            _licenseGuard = new LicenseGuard(_licenseManager);
            _updateChecker = new UpdateChecker();
            _runnerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(170) };
            _runnerTimer.Tick += (_, _) => AdvanceRunnerFrame();
            InputTextBox.TextChanged += (_, _) =>
            {
                RefreshOutputPreview();
                RefreshActionButtons();
            };
            OutputTextBox.TextChanged += (_, _) =>
            {
                RefreshOutputPreview();
                RefreshActionButtons();
            };
            StartButton.IsEnabled = false;
            ActivationButton.IsEnabled = false;
            ToolsStatusTextBlock.Text = "Running startup checks...";
            ToolsStatusTextBlock.Foreground = Brushes.Goldenrod;
            VideoInfoTextBlock.Text = "Verifying tools, integrity manifest, version, and rife-v4.26 model";
            VideoInfoTextBlock.Foreground = Brushes.Gray;
            EstimateTextBlock.Text = "Start will unlock automatically when startup and update checks pass.";
            EstimateTextBlock.Foreground = Brushes.Gray;

            string? tamperMessage = _licenseManager.CheckForDateTamper();
            if (!string.IsNullOrWhiteSpace(tamperMessage))
                MessageBox.Show(this, tamperMessage, "License Warning", MessageBoxButton.OK, MessageBoxImage.Warning);

            Loaded += async (_, _) =>
            {
                VersionTextBlock.Text = "v" + GetAppVersion();
                UpdateLicenseStatus();
                await RunStartupChecksAsync();
                CleanupOldUpdateInstallers();
                CleanupStaleProcessingSessions();
                await CheckForMandatoryUpdatesAsync(showNoUpdateMessage: false);
                RefreshActionButtons();
            };
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
                InitialDirectory = GetSuggestedOutputDirectory(),
                FileName = GetSuggestedOutputFileName()
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
            SetProgressVisual(0, "🏃", "Warming up", Brushes.DeepSkyBlue);
            StartRunnerAnimation();
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

            if (!ValidateInputOutputPaths(input, output))
                return;

            ToolCheckResult tools = RefreshToolsStatus(showDetails: false);
            if (!tools.Ready)
            {
                FailUI("أدوات المعالجة غير جاهزة.\n\n" + tools.Details);
                return;
            }

            if (!EnsureEnoughDiskSpace(input, output))
                return;

            if (!ConfirmOverwriteOutputs(output))
                return;

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

                if (!AdvanceLicenseCheckpoint(guardSession, "tools-verified")) return;

                var interpolator = new VideoInterpolator(tools.RifePath, tools.FfmpegPath, tools.FfprobePath)
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
                ApplySelectedPreset(interpolator);

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
                StopRunnerAnimation("🎉");
                SetProgressVisual(100, "🎉", "Delivered", Brushes.LightGreen);
                StageLabel.Text = "Done ✓";
                UpdateLicenseStatus();

                string[] produced = GetExpectedOutputPaths(output);
                var successWindow = new SuccessWindow(produced[0], produced[1])
                {
                    Owner = this
                };
                successWindow.ShowDialog();
                if (successWindow.OpenFolderRequested && !string.IsNullOrWhiteSpace(outDir))
                    OpenFolder(outDir);
            }
            catch (OperationCanceledException)
            {
                AppendLog("Canceled by user.");
                StopRunnerAnimation("😮‍💨");
                SetProgressMood("😮‍💨", "Paused", Brushes.Goldenrod);
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
            if (_cts == null)
                return;

            AppendLog("Canceling...");
            StopRunnerAnimation("🛑");
            SetProgressMood("🛑", "Stopping", Brushes.Goldenrod);
            CancelButton.IsEnabled = false;
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

        private void TestToolsButton_Click(object sender, RoutedEventArgs e)
        {
            ToolCheckResult result = RefreshToolsStatus(showDetails: true);
            MessageBox.Show(
                this,
                result.Details,
                result.Ready ? "Tools Ready" : "Tools Check Failed",
                MessageBoxButton.OK,
                result.Ready ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }

        private void AboutButton_Click(object sender, RoutedEventArgs e)
        {
            var about = new AboutWindow(LicenseStatusTextBlock.Text)
            {
                Owner = this
            };
            about.ShowDialog();
        }

        private void ClearLogButton_Click(object sender, RoutedEventArgs e)
        {
            LogsTextBox.Clear();
        }

        private void CopyLogButton_Click(object sender, RoutedEventArgs e)
        {
            string text = LogsTextBox.Text;
            if (!string.IsNullOrWhiteSpace(text))
                Clipboard.SetText(text);
        }

        private void OpenOutputFolderButton_Click(object sender, RoutedEventArgs e)
        {
            string output = OutputTextBox.Text.Trim();
            string folder = Path.GetDirectoryName(output) ?? "";
            if (Directory.Exists(folder))
                OpenFolder(folder);
        }

        private void SetBusy(bool busy)
        {
            StartButton.IsEnabled = !busy && CanStartProcessing();
            CancelButton.IsEnabled = busy;
            ActivationButton.IsEnabled = !busy && !_updateCheckInProgress && _updateGatePassed;
            UpdateButton.IsEnabled = !busy && !_updateCheckInProgress;
        }

        private bool CanStartProcessing()
        {
            return (_toolStatus?.Ready ?? false) &&
                   _updateGatePassed &&
                   _cts == null &&
                   HasRunnableInputOutput();
        }

        private bool HasRunnableInputOutput()
        {
            string input = InputTextBox.Text.Trim();
            string output = OutputTextBox.Text.Trim();
            return File.Exists(input) &&
                   SupportedVideoExtensions.Contains(Path.GetExtension(input), StringComparer.OrdinalIgnoreCase) &&
                   !string.IsNullOrWhiteSpace(Path.GetDirectoryName(output)) &&
                   string.Equals(Path.GetExtension(output), ".mp4", StringComparison.OrdinalIgnoreCase) &&
                   !PathsEqual(input, output);
        }

        private void RefreshActionButtons()
        {
            bool busy = _cts != null;
            StartButton.IsEnabled = !busy && CanStartProcessing();
            CancelButton.IsEnabled = busy;
            ActivationButton.IsEnabled = !busy && !_updateCheckInProgress && _updateGatePassed;
            UpdateButton.IsEnabled = !busy && !_updateCheckInProgress;
        }

        private void SetProgressVisual(double percent, string face, string text, Brush brush)
        {
            double value = Math.Clamp(percent, 0, 100);
            MainProgressBar.Value = value;
            PercentLabel.Text = $"{value:0}%";
            SetProgressMood(face, text, brush);
            PositionProgressMoodBubble();
        }

        private void SetProgressMood(string face, string text, Brush brush)
        {
            if (!_runnerTimer.IsEnabled)
                SetRunnerState(face, showDash: false);
            ProgressMoodText.Text = text;
            ProgressMoodBubble.BorderBrush = brush;
        }

        private void StartRunnerAnimation()
        {
            _runnerFrame = 0;
            RunnerIconImage.Source = RunnerImage;
            RunnerDashImage.Visibility = Visibility.Collapsed;
            RunnerBounceTransform.X = 0;
            RunnerBounceTransform.Y = 0;
            _runnerTimer.Start();
        }

        private void StopRunnerAnimation(string finalIcon)
        {
            _runnerTimer.Stop();
            SetRunnerState(finalIcon, showDash: false);
            RunnerBounceTransform.X = 0;
            RunnerBounceTransform.Y = 0;
        }

        private void AdvanceRunnerFrame()
        {
            _runnerFrame++;
            int frame = _runnerFrame % 4;
            RunnerIconImage.Source = frame < 2 ? RunnerImage : RunnerManImage;
            RunnerDashImage.Visibility = frame is 1 or 3 ? Visibility.Visible : Visibility.Collapsed;
            RunnerBounceTransform.X = (_runnerFrame % 2 == 0) ? 0 : 4;
            RunnerBounceTransform.Y = (_runnerFrame % 3 == 0) ? -2 : 1;
        }

        private void SetRunnerState(string icon, bool showDash)
        {
            RunnerIconImage.Source = icon switch
            {
                "🎉" => PartyImage,
                "😵" => DizzyImage,
                "😮‍💨" => ReliefImage,
                "🛑" => StopImage,
                "🏃‍♂️" or "🏃‍♂️💨" => RunnerManImage,
                _ => RunnerImage
            };
            RunnerDashImage.Visibility = showDash || icon.Contains("💨", StringComparison.Ordinal)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private static BitmapImage LoadEmojiImage(string fileName)
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri($"pack://application:,,,/Assets/Emoji/{fileName}", UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }

        private void PositionProgressMoodBubble()
        {
            if (ProgressMoodTrack.ActualWidth <= 0 || ProgressMoodBubble.ActualWidth <= 0)
                return;

            double maxLeft = Math.Max(0, ProgressMoodTrack.ActualWidth - ProgressMoodBubble.ActualWidth);
            double left = maxLeft * (MainProgressBar.Value / Math.Max(1d, MainProgressBar.Maximum));
            Canvas.SetLeft(ProgressMoodBubble, left);
        }

        private void ProgressMoodTrack_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            PositionProgressMoodBubble();
        }

        private void PresetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            RefreshPresetInsight();
            RefreshOutputPreview();
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
                        $"وضع التجربة: سيتم استخدام محاولة مجانية واحدة.\nالمتبقي بعد هذا الفيديو: {remainingAfterThis} من {TrialManager.MaxTrialVideos}.\n\nهل تريد المتابعة؟",
                        "ForgeFlow Trial",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Information);
                    return confirm == MessageBoxResult.Yes;
                }

                return true;
            }

            var openActivation = MessageBox.Show(
                this,
                decision.Message + "\n\nهل تريد فتح نافذة التفعيل الآن؟",
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
            UpdateLicenseStatus();
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
                    "تعذر التحقق من التحديثات الإجبارية. المعالجة متوقفة حتى ينجح فحص التحديث.",
                    "Update Check Required",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            return ok;
        }

        private async Task<bool> CheckForMandatoryUpdatesAsync(bool showNoUpdateMessage)
        {
            if (_updateCheckInProgress)
                return false;

            try
            {
                _updateCheckInProgress = true;
                RefreshActionButtons();
                ToolsStatusTextBlock.Text = "Checking mandatory updates...";
                ToolsStatusTextBlock.Foreground = Brushes.Goldenrod;
                EstimateTextBlock.Text = "Processing is locked until the update check succeeds.";
                EstimateTextBlock.Foreground = Brushes.Gray;

                UpdateInfo? update = await _updateChecker.CheckForUpdatesAsync();
                if (update == null)
                {
                    _updateGatePassed = true;
                    ApplyToolStatusToUi(_toolStatus ?? RefreshToolsStatus(showDetails: false));
                    if (showNoUpdateMessage)
                    {
                        MessageBox.Show(this, "ForgeFlow Lite is up to date.", "Updates", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    return true;
                }

                if (update.IsBlocked)
                {
                    _updateGatePassed = false;
                    ToolsStatusTextBlock.Text = "This build is disabled";
                    ToolsStatusTextBlock.Foreground = Brushes.OrangeRed;
                    EstimateTextBlock.Text = "Processing and activation are locked by the publisher.";
                    EstimateTextBlock.Foreground = Brushes.Orange;
                    MessageBox.Show(
                        this,
                        "تم إيقاف هذه النسخة من ForgeFlow Lite من قبل المطور.\n\n" +
                        update.MandatoryReason + "\n\n" +
                        "المعالجة والتفعيل متوقفان حتى يتم نشر نسخة مسموحة.",
                        "ForgeFlow Disabled",
                        MessageBoxButton.OK,
                        MessageBoxImage.Stop);
                    return false;
                }

                string message =
                    "يوجد تحديث إجباري لـ ForgeFlow Lite.\n\n" +
                    $"الإصدار الحالي: {update.CurrentVersion}\n" +
                    $"آخر إصدار: {update.LatestVersion}\n" +
                    $"تاريخ النشر: {update.PublishedDate:yyyy-MM-dd}\n\n" +
                    update.MandatoryReason;

                if (!update.HasDirectInstaller)
                {
                    ToolsStatusTextBlock.Text = "Mandatory update required";
                    ToolsStatusTextBlock.Foreground = Brushes.Orange;
                    EstimateTextBlock.Text = "No direct setup file was found. Opening the release page.";
                    EstimateTextBlock.Foreground = Brushes.Orange;
                    MessageBox.Show(
                        this,
                        message + "\n\nلم يتم العثور على ملف تثبيت مباشر داخل الإصدار. سيتم فتح صفحة الإصدار للتنزيل اليدوي.",
                        "Mandatory Update",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    OpenUpdateUrl(update.ReleaseUrl);
                    return false;
                }

                message += "\n\nسيتم تنزيل ملف التثبيت وتشغيله الآن.";

                MessageBox.Show(this, message, "Mandatory Update", MessageBoxButton.OK, MessageBoxImage.Warning);
                var updateWindow = new UpdateWindow(update)
                {
                    Owner = this
                };
                updateWindow.ShowDialog();

                if (updateWindow.InstallerStarted)
                    Application.Current.Shutdown();

                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Update check failed: " + ex.Message);
                _updateGatePassed = false;
                ToolsStatusTextBlock.Text = "Update check failed";
                ToolsStatusTextBlock.Foreground = Brushes.Orange;
                EstimateTextBlock.Text = "Start and activation remain locked until mandatory update check succeeds.";
                EstimateTextBlock.Foreground = Brushes.Orange;
                if (showNoUpdateMessage)
                {
                    AppendLog("WARNING: Update check failed: " + ex.Message);
                    MessageBox.Show(this, "تعذر فحص التحديثات. تحقق من الإنترنت ثم حاول مرة أخرى.\n\n" + ex.Message, "Updates", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                return false;
            }
            finally
            {
                _updateCheckInProgress = false;
                RefreshActionButtons();
            }
        }

        private void ApplySelectedPreset(VideoInterpolator interpolator)
        {
            string preset = ((PresetComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Balanced").Trim();
            switch (preset)
            {
                case "Fast":
                    interpolator.PcCrf = 16;
                    interpolator.PcPreset = "fast";
                    interpolator.TikTokCrf = 18;
                    interpolator.TikTokPreset = "fast";
                    interpolator.TikTokMaxRate = "35M";
                    interpolator.TikTokBufSize = "70M";
                    interpolator.PngCompressionLevel = 1;
                    break;
                case "Quality":
                    interpolator.PcCrf = 13;
                    interpolator.PcPreset = "veryslow";
                    interpolator.TikTokCrf = 15;
                    interpolator.TikTokPreset = "veryslow";
                    interpolator.TikTokMaxRate = "50M";
                    interpolator.TikTokBufSize = "100M";
                    interpolator.PngCompressionLevel = 1;
                    break;
                default:
                    interpolator.PcCrf = 15;
                    interpolator.PcPreset = "slow";
                    interpolator.TikTokCrf = 17;
                    interpolator.TikTokPreset = "slow";
                    interpolator.TikTokMaxRate = "42M";
                    interpolator.TikTokBufSize = "84M";
                    interpolator.PngCompressionLevel = 1;
                    break;
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

        private void UpdateLicenseStatus()
        {
            GuardDecision decision = _licenseGuard.EvaluateAccess();
            if (decision.Allowed && decision.Mode == LicenseAccessMode.Licensed)
            {
                LicenseStatusTextBlock.Text = "Activated";
                LicenseStatusTextBlock.Foreground = System.Windows.Media.Brushes.LightGreen;
                return;
            }

            if (decision.Allowed && decision.Mode == LicenseAccessMode.Trial)
            {
                LicenseStatusTextBlock.Text = $"Trial: {decision.Remaining} of {TrialManager.MaxTrialVideos} remaining";
                LicenseStatusTextBlock.Foreground = System.Windows.Media.Brushes.LightSkyBlue;
                return;
            }

            LicenseStatusTextBlock.Text = "Activation required";
            LicenseStatusTextBlock.Foreground = System.Windows.Media.Brushes.Orange;
        }

        private bool EnsureEnoughDiskSpace(string inputPath, string outputPath)
        {
            try
            {
                long inputSize = new FileInfo(inputPath).Length;
                long requiredBytes = _selectedVideoInfo != null ? VideoInfo.EstimateRequiredBytes(inputSize) : Math.Max(5L * 1024 * 1024 * 1024, inputSize * 8);

                string outputDir = Path.GetDirectoryName(outputPath) ?? AppContext.BaseDirectory;
                string tempDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ForgeFlow",
                    "TempProcessingData");

                long outputFree = 0;
                long tempFree = 0;
                bool outputHasSpace = HasFreeSpace(outputDir, requiredBytes, out outputFree);
                bool tempHasSpace = HasFreeSpace(tempDir, requiredBytes, out tempFree);
                if (!outputHasSpace || !tempHasSpace)
                {
                    string message =
                        "المساحة المتوفرة قد لا تكفي لمعالجة الفيديو.\n\n" +
                        $"المطلوب تقريبا: {FormatBytes(requiredBytes)}\n" +
                        $"مساحة مجلد الإخراج: {FormatBytes(outputFree)}\n" +
                        $"مساحة المجلد المؤقت: {FormatBytes(tempFree)}\n\n" +
                        "هل تريد المتابعة على مسؤوليتك؟";

                    return MessageBox.Show(this, message, "Low Disk Space", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
                }
            }
            catch (Exception ex)
            {
                AppendLog("WARNING: Disk space check skipped: " + ex.Message);
            }

            return true;
        }

        private bool ValidateInputOutputPaths(string inputPath, string outputPath)
        {
            if (inputPath.Length > 240 || outputPath.Length > 240)
            {
                FailUI("مسار الملف طويل جدا. انقل الفيديو إلى مجلد أقصر مثل Desktop ثم حاول مرة أخرى.");
                return false;
            }

            string outputDir = Path.GetDirectoryName(outputPath) ?? "";
            if (string.IsNullOrWhiteSpace(outputDir))
            {
                FailUI("مسار الإخراج غير صحيح.");
                return false;
            }

            if (!SupportedVideoExtensions.Contains(Path.GetExtension(inputPath), StringComparer.OrdinalIgnoreCase))
            {
                FailUI("صيغة الفيديو غير مدعومة. الصيغ المدعومة: MP4, MOV, MKV, WEBM, AVI.");
                return false;
            }

            if (!string.Equals(Path.GetExtension(outputPath), ".mp4", StringComparison.OrdinalIgnoreCase))
            {
                FailUI("مسار الإخراج يجب أن يكون ملف MP4.");
                return false;
            }

            if (PathsEqual(inputPath, outputPath))
            {
                FailUI("مسار الإخراج لا يمكن أن يكون نفس ملف الإدخال.");
                return false;
            }

            return true;
        }

        private static bool HasFreeSpace(string path, long requiredBytes, out long freeBytes)
        {
            Directory.CreateDirectory(path);
            string root = Path.GetPathRoot(Path.GetFullPath(path)) ?? path;
            var drive = new DriveInfo(root);
            freeBytes = drive.AvailableFreeSpace;
            return freeBytes >= requiredBytes;
        }

        private static string FormatBytes(long bytes)
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

        private ToolCheckResult RefreshToolsStatus(bool showDetails)
        {
            _toolStatus = ToolVerifier.Check(AppContext.BaseDirectory, GetAppVersion());
            ApplyToolStatusToUi(_toolStatus);
            if (showDetails)
                AppendLog(_toolStatus.Summary);
            return _toolStatus;
        }

        private async Task RunStartupChecksAsync()
        {
            ToolsStatusTextBlock.Text = "Running startup checks...";
            ToolsStatusTextBlock.Foreground = Brushes.Goldenrod;
            VideoInfoTextBlock.Text = "Checking tools...";
            VideoInfoTextBlock.Foreground = Brushes.Gray;
            EstimateTextBlock.Text = "Please wait...";
            EstimateTextBlock.Foreground = Brushes.Gray;
            StartButton.IsEnabled = false;

            ToolCheckResult result = await Task.Run(() => ToolVerifier.Check(AppContext.BaseDirectory, GetAppVersion()));
            _toolStatus = result;
            ApplyToolStatusToUi(result);
            AppendLog(result.Summary);
            if (!result.Ready)
                AppendLog("WARNING: Startup checks require attention. Use Test Tools for full details.");
        }

        private void ApplyToolStatusToUi(ToolCheckResult result)
        {
            ToolsStatusTextBlock.Text = result.Summary;
            ToolsStatusTextBlock.Foreground = result.Ready ? Brushes.LightGreen : Brushes.Orange;

            string versionState = result.VersionFilePresent
                ? (result.VersionMatchesAssembly
                    ? $"version.txt OK ({result.VersionText})"
                    : $"version.txt mismatch ({result.VersionText} != {GetAppVersion()})")
                : "version.txt missing";

            string manifestState = result.ManifestPresent
                ? "tools.sha256 verified"
                : "tools.sha256 missing";

            string modelState = result.RifeModelPresent
                ? "rife-v4.26 model found"
                : "rife-v4.26 model missing";

            VideoInfoTextBlock.Text = $"{manifestState} | {versionState} | {modelState}";
            VideoInfoTextBlock.Foreground = result.Ready ? Brushes.LightGray : Brushes.Orange;

            EstimateTextBlock.Text = result.Ready
                ? "Ready before Start: ffmpeg, ffprobe, RIFE executable, and model are available."
                : "Start is disabled until startup checks pass. Open Test Tools for exact missing or changed items.";
            EstimateTextBlock.Foreground = result.Ready ? Brushes.LightGray : Brushes.Orange;

            RefreshActionButtons();
        }

        private bool ConfirmOverwriteOutputs(string outputPath)
        {
            var existing = GetExpectedOutputPaths(outputPath)
                .Where(File.Exists)
                .Select(Path.GetFileName)
                .ToArray();

            if (existing.Length == 0)
                return true;

            string message =
                "ملفات الإخراج التالية موجودة مسبقا وسيتم استبدالها:\n\n" +
                string.Join(Environment.NewLine, existing) +
                "\n\nهل تريد المتابعة؟";

            return MessageBox.Show(this, message, "Overwrite Output Files", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
        }

        private static string[] GetExpectedOutputPaths(string outputPath)
        {
            string outDir = Path.GetDirectoryName(outputPath) ?? AppContext.BaseDirectory;
            string baseName = Path.GetFileNameWithoutExtension(outputPath);
            string ext = Path.GetExtension(outputPath);
            if (string.IsNullOrWhiteSpace(ext))
                ext = ".mp4";

            return new[]
            {
                Path.Combine(outDir, $"{baseName}_Video 60FPS{ext}"),
                Path.Combine(outDir, $"{baseName}_TikTok30FPS{ext}")
            };
        }

        private static void CleanupOldUpdateInstallers()
        {
            try
            {
                string updateDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ForgeFlow",
                    "Updates");
                if (!Directory.Exists(updateDir))
                    return;

                var installers = Directory.GetFiles(updateDir, "ForgeFlow_Lite_Setup_*.exe")
                    .Select(path => new FileInfo(path))
                    .OrderByDescending(file => file.LastWriteTimeUtc)
                    .Skip(1);

                foreach (var installer in installers)
                    installer.Delete();
            }
            catch
            {
            }
        }

        private static void CleanupStaleProcessingSessions()
        {
            try
            {
                string tempRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ForgeFlow",
                    "TempProcessingData");
                if (!Directory.Exists(tempRoot))
                    return;

                foreach (string sessionDir in Directory.GetDirectories(tempRoot, "Session_*"))
                {
                    var info = new DirectoryInfo(sessionDir);
                    if (info.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-1))
                        info.Delete(true);
                }
            }
            catch
            {
            }
        }

        private static void OpenFolder(string folder)
        {
            if (!Directory.Exists(folder))
                return;

            Process.Start(new ProcessStartInfo
            {
                FileName = folder,
                UseShellExecute = true
            });
        }

        private void Window_PreviewDragOver(object sender, DragEventArgs e)
        {
            e.Effects = GetDroppedVideoPath(e) == null ? DragDropEffects.None : DragDropEffects.Copy;
            DropOverlay.Visibility = e.Effects == DragDropEffects.Copy ? Visibility.Visible : Visibility.Collapsed;
            e.Handled = true;
        }

        private void Window_PreviewDragEnter(object sender, DragEventArgs e)
        {
            DropOverlay.Visibility = GetDroppedVideoPath(e) == null ? Visibility.Collapsed : Visibility.Visible;
            e.Handled = true;
        }

        private void Window_PreviewDragLeave(object sender, DragEventArgs e)
        {
            DropOverlay.Visibility = Visibility.Collapsed;
            e.Handled = true;
        }

        private void Window_PreviewDrop(object sender, DragEventArgs e)
        {
            DropOverlay.Visibility = Visibility.Collapsed;
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

        private async void SelectInputVideo(string videoPath)
        {
            InputTextBox.Text = videoPath;
            if (ShouldReplaceOutputPath())
                OutputTextBox.Text = GetDefaultOutputPath(videoPath);
            else
                RefreshOutputPreview();

            await LoadSelectedVideoInfoAsync(videoPath);
        }

        private async Task LoadSelectedVideoInfoAsync(string videoPath)
        {
            _videoInfoCts?.Cancel();
            _videoInfoCts?.Dispose();
            _videoInfoCts = new CancellationTokenSource();
            CancellationToken token = _videoInfoCts.Token;

            _selectedVideoInfo = null;
            EstimateTextBlock.Text = "";

            ToolCheckResult tools = _toolStatus ?? RefreshToolsStatus(showDetails: false);
            if (!File.Exists(tools.FfprobePath))
            {
                VideoInfoTextBlock.Text = "Video selected. FFprobe is missing, so details cannot be loaded.";
                VideoInfoTextBlock.Foreground = Brushes.Orange;
                return;
            }

            VideoInfoTextBlock.Text = "Reading video info...";
            VideoInfoTextBlock.Foreground = Brushes.LightSkyBlue;
            try
            {
                VideoInfo? info = await VideoInfoReader.ReadAsync(tools.FfprobePath, videoPath, token);
                if (token.IsCancellationRequested || !PathsEqual(InputTextBox.Text.Trim(), videoPath))
                    return;

                if (info == null)
                {
                    VideoInfoTextBlock.Text = "Could not read video info.";
                    VideoInfoTextBlock.Foreground = Brushes.Orange;
                    return;
                }

                _selectedVideoInfo = info;
                VideoInfoTextBlock.Text = info.Summary;
                EstimateTextBlock.Text = info.Estimate;
                VideoInfoTextBlock.Foreground = Brushes.LightGray;
                RefreshOutputPreview();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                VideoInfoTextBlock.Text = "Could not read video info.";
                EstimateTextBlock.Text = ex.Message;
                VideoInfoTextBlock.Foreground = Brushes.Orange;
            }
        }

        private static string GetDefaultOutputPath(string inputPath)
        {
            string directory = Path.GetDirectoryName(inputPath) ?? AppContext.BaseDirectory;
            string name = Path.GetFileNameWithoutExtension(inputPath);
            return Path.Combine(directory, SanitizeFileName(name) + "_ForgeFlow.mp4");
        }

        private string GetSuggestedOutputDirectory()
        {
            string input = InputTextBox.Text.Trim();
            if (File.Exists(input))
                return Path.GetDirectoryName(input) ?? AppContext.BaseDirectory;

            string output = OutputTextBox.Text.Trim();
            string? outputDir = Path.GetDirectoryName(output);
            return !string.IsNullOrWhiteSpace(outputDir) && Directory.Exists(outputDir)
                ? outputDir
                : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        }

        private string GetSuggestedOutputFileName()
        {
            string input = InputTextBox.Text.Trim();
            if (File.Exists(input))
                return Path.GetFileName(GetDefaultOutputPath(input));

            string output = OutputTextBox.Text.Trim();
            return string.IsNullOrWhiteSpace(Path.GetFileName(output))
                ? "ForgeFlow_Output.mp4"
                : Path.GetFileName(output);
        }

        private bool ShouldReplaceOutputPath()
        {
            string output = OutputTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(output))
                return true;

            string fileName = Path.GetFileName(output);
            return fileName.Equals("output.mp4", StringComparison.OrdinalIgnoreCase) ||
                   fileName.Equals("ForgeFlow_Output.mp4", StringComparison.OrdinalIgnoreCase) ||
                   fileName.EndsWith("_ForgeFlow.mp4", StringComparison.OrdinalIgnoreCase);
        }

        private void RefreshOutputPreview()
        {
            string output = OutputTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(output) || string.IsNullOrWhiteSpace(Path.GetDirectoryName(output)))
            {
                OutputPreviewTextBlock.Text = "Select a video to preview the two customer-ready exports.";
                OutputPreviewTextBlock.Foreground = Brushes.Gray;
                return;
            }

            string input = InputTextBox.Text.Trim();
            if (!string.Equals(Path.GetExtension(output), ".mp4", StringComparison.OrdinalIgnoreCase))
            {
                OutputPreviewTextBlock.Text = "Output must be an MP4 path so both exports are predictable.";
                OutputPreviewTextBlock.Foreground = Brushes.Orange;
                return;
            }

            if (File.Exists(input) && PathsEqual(input, output))
            {
                OutputPreviewTextBlock.Text = "Output cannot overwrite the original input video.";
                OutputPreviewTextBlock.Foreground = Brushes.Orange;
                return;
            }

            string[] outputs = GetExpectedOutputPaths(output);
            string sizeHint = _selectedVideoInfo == null
                ? ""
                : $" | source {VideoInfo.FormatBytes(_selectedVideoInfo.SizeBytes)}, temp about {VideoInfo.FormatBytes(VideoInfo.EstimateRequiredBytes(_selectedVideoInfo.SizeBytes))}";
            OutputPreviewTextBlock.Text =
                "Exports: " + Path.GetFileName(outputs[0]) + " + " + Path.GetFileName(outputs[1]) + sizeHint;
            OutputPreviewTextBlock.Foreground = Brushes.LightGray;
        }

        private void RefreshPresetInsight()
        {
            string preset = ((PresetComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Balanced").Trim();
            PresetInsightTextBlock.Text = preset switch
            {
                "Fast" => "Fast is best for demos and urgent delivery with smaller files.",
                "Quality" => "Quality is best for portfolio clips and client-facing masters.",
                _ => "Balanced keeps a practical mix of quality, size, and processing time."
            };
        }

        private static bool PathsEqual(string first, string second)
        {
            if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second))
                return false;

            try
            {
                return string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return string.Equals(first.Trim(), second.Trim(), StringComparison.OrdinalIgnoreCase);
            }
        }

        private static string SanitizeFileName(string name)
        {
            foreach (char invalid in Path.GetInvalidFileNameChars())
                name = name.Replace(invalid, '_');
            return string.IsNullOrWhiteSpace(name) ? "ForgeFlow_Output" : name.Trim();
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

        private static string GetAppVersion()
        {
            Version? version = Assembly.GetExecutingAssembly().GetName().Version;
            return version == null ? "1.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
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
                    ApplyProgressMood(pct);
                }
                return;
            }

            // ── Stage label update ────────────────────────────────────
            if (msg.StartsWith("STAGE:"))
            {
                StageLabel.Text = msg.Replace("STAGE:", "").Trim();
                ApplyStageMood(StageLabel.Text);
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
                || msg.StartsWith("Canceling")
                || msg.StartsWith("CRITICAL:");
        }

        private void ApplyProgressMood(int percent)
        {
            string face;
            string text;
            Brush brush;

            if (percent < 8)
            {
                face = "🏃";
                text = "Launching";
                brush = Brushes.DeepSkyBlue;
            }
            else if (percent < 35)
            {
                face = "🏃💨";
                text = "Running";
                brush = Brushes.DodgerBlue;
            }
            else if (percent < 70)
            {
                face = "🏃‍♂️💨";
                text = "Pushing";
                brush = Brushes.Orange;
            }
            else if (percent < 96)
            {
                face = "🏃‍♂️";
                text = "Almost";
                brush = Brushes.Gold;
            }
            else
            {
                face = "🎉";
                text = "Finishing";
                brush = Brushes.LightGreen;
            }

            SetProgressVisual(percent, face, text, brush);
        }

        private void ApplyStageMood(string stage)
        {
            if (stage.Contains("extract", StringComparison.OrdinalIgnoreCase) ||
                stage.Contains("frame", StringComparison.OrdinalIgnoreCase))
            {
                SetProgressMood("🏃", "Preparing", Brushes.DodgerBlue);
            }
            else if (stage.Contains("rife", StringComparison.OrdinalIgnoreCase) ||
                     stage.Contains("interpol", StringComparison.OrdinalIgnoreCase))
            {
                SetProgressMood("🏃💨", "Sprint", Brushes.Orange);
            }
            else if (stage.Contains("encode", StringComparison.OrdinalIgnoreCase) ||
                     stage.Contains("mux", StringComparison.OrdinalIgnoreCase))
            {
                SetProgressMood("🏃‍♂️", "Polishing", Brushes.Gold);
            }
            else if (stage.Contains("done", StringComparison.OrdinalIgnoreCase))
            {
                StopRunnerAnimation("🎉");
                SetProgressVisual(100, "🎉", "Delivered", Brushes.LightGreen);
            }
        }

        private void FailUI(string msg)
        {
            AppendLog("ERROR: " + msg);
            StopRunnerAnimation("😵");
            SetProgressMood("😵", "Needs fix", Brushes.OrangeRed);
            MessageBox.Show(msg, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        protected override void OnClosed(EventArgs e)
        {
            _runnerTimer.Stop();
            _videoInfoCts?.Cancel();
            _videoInfoCts?.Dispose();
            _updateChecker.Dispose();
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            base.OnClosed(e);
        }
    }
}
