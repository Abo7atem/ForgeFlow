using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Media;
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
        private sealed class UpscaleProfile
        {
            public string Id { get; init; } = "";
            public string DisplayName { get; init; } = "";
            public string Insight { get; init; } = "";
            public string Engine { get; init; } = "none";
            public string ModelDirectoryName { get; init; } = "";
            public string ModelName { get; init; } = "";
            public int Scale { get; init; } = 1;
            public override string ToString() => DisplayName;
        }

        private static readonly string[] SupportedVideoExtensions =
        {
            ".mp4", ".mov", ".mkv", ".webm", ".avi"
        };
        private const string BundledToolsFolderName = "Tools";
        private const string SubjectFocusModelRelativePath = "subject-focus\\modnet.onnx";
        private const string RealEsrganExeName = "realesrgan-ncnn-vulkan.exe";
        private const string RealCuganExeName = "realcugan-ncnn-vulkan.exe";

        private CancellationTokenSource? _cts;
        private string? _lastInput;
        private string? _lastOutput;
        private readonly LicenseManager _licenseManager;
        private readonly LicenseGuard _licenseGuard;
        private readonly UpdateChecker _updateChecker;
        private readonly AppSettings _settings;
        private readonly VideoEditOptions _editOptions = new();
        private bool _updateGatePassed;
        private bool _updateCheckInProgress;
        private ToolCheckResult? _toolStatus;
        private VideoInfo? _selectedVideoInfo;
        private CancellationTokenSource? _videoInfoCts;
        private readonly DispatcherTimer _runnerTimer;
        private readonly DispatcherTimer _inlinePreviewTimer;
        private int _runnerFrame;
        private bool _inlinePreviewPlaying;
        private bool _inlinePreviewSeeking;
        private bool _inlinePreviewMuted;
        private readonly List<UpscaleProfile> _upscaleProfiles = new();
        private bool _uiInitialized;
        private static readonly BitmapImage RunnerImage = LoadEmojiImage("runner.png");
        private static readonly BitmapImage RunnerManImage = LoadEmojiImage("runner_man.png");
        private static readonly BitmapImage PartyImage = LoadEmojiImage("party.png");
        private static readonly BitmapImage DizzyImage = LoadEmojiImage("dizzy.png");
        private static readonly BitmapImage ReliefImage = LoadEmojiImage("relief.png");
        private static readonly BitmapImage StopImage = LoadEmojiImage("stop.png");

        public MainWindow()
        {
            InitializeComponent();
            NativeWindowTheme.Apply(this);
            _licenseManager = new LicenseManager();
            _licenseGuard = new LicenseGuard(_licenseManager);
            _updateChecker = new UpdateChecker();
            _settings = AppSettings.Load();
            _runnerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(170) };
            _runnerTimer.Tick += (_, _) => AdvanceRunnerFrame();
            _inlinePreviewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _inlinePreviewTimer.Tick += (_, _) => RefreshInlinePreviewTimeline();
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
            Glitch30Button.IsEnabled = false;
            CompletionSoundCheckBox.IsChecked = _settings.PlayCompletionSound;
            SummaryCheckBox.IsChecked = _settings.ShowProcessingSummary;
            SelectComboItemByTag(HardwareComboBox, string.IsNullOrWhiteSpace(_settings.HardwareAccelerationMode) ? "cpu" : _settings.HardwareAccelerationMode);
            SelectComboItemByTag(MotionComboBox, _settings.EnableRife ? "on" : "off");
            SelectComboItemByTag(ProcessingOrderComboBox, string.IsNullOrWhiteSpace(_settings.ProcessingOrder) ? "rife-then-upscale" : _settings.ProcessingOrder);
            SelectComboItemByTag(SubjectFocusComboBox, string.IsNullOrWhiteSpace(_settings.SubjectFocusMode) ? "off" : _settings.SubjectFocusMode);
            SubjectFocusStrengthSlider.Value = Math.Clamp(_settings.SubjectFocusStrength, 0, 100);
            SelectComboItemByTag(VisualStyleComboBox, string.IsNullOrWhiteSpace(_settings.VisualStyle) ? "natural" : _settings.VisualStyle);
            VisualStyleStrengthSlider.Value = Math.Clamp(_settings.VisualStyleStrength, 0, 100);
            LoadUpscaleProfiles();
            ToolsStatusTextBlock.Text = "Running startup checks...";
            ToolsStatusTextBlock.Foreground = Brushes.Goldenrod;
            VideoInfoTextBlock.Text = "Verifying tools, integrity manifest, version, and rife-v4.26 model";
            VideoInfoTextBlock.Foreground = Brushes.Gray;
            EstimateTextBlock.Text = "Start will unlock automatically when startup and update checks pass.";
            EstimateTextBlock.Foreground = Brushes.Gray;
            RefreshPresetInsight();
            RefreshVisualStyleInsight();
            RefreshUpscaleInsight();
            RefreshSubjectFocusInsight();
            RefreshOutputPreview();
            _uiInitialized = true;
            ApplyPlanRestrictionsToUi(showMessage: false);
            RefreshStartHint();

            string? tamperMessage = _licenseManager.CheckForDateTamper();
            if (!string.IsNullOrWhiteSpace(tamperMessage))
                MessageBox.Show(this, tamperMessage, "License Warning", MessageBoxButton.OK, MessageBoxImage.Warning);

            Loaded += async (_, _) =>
            {
                VersionTextBlock.Text = "v" + GetAppVersion();
                UpdateLicenseStatus();
                ShowWelcomeWindowIfNeeded();
                UpdateLicenseStatus();
                await RunStartupChecksAsync();
                CleanupOldUpdateInstallers();
                CleanupOldSupportReports();
                CleanupStaleProcessingSessions();
                await CheckForMandatoryUpdatesAsync(showNoUpdateMessage: false);
                RefreshActionButtons();
            };
        }

        private void BrowseInput_Click(object sender, RoutedEventArgs e)
        {
            var ofd = new OpenFileDialog
            {
                Filter = "Video Files|*.mp4;*.mov;*.mkv;*.webm;*.avi|All Files|*.*",
                InitialDirectory = GetExistingDirectoryOrDefault(_settings.LastInputDirectory)
            };

            if (ofd.ShowDialog() == true)
            {
                SaveLastInputDirectory(ofd.FileName);
                SelectInputVideo(ofd.FileName);
            }
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
            {
                OutputTextBox.Text = sfd.FileName;
                SaveLastOutputDirectory(sfd.FileName);
            }
        }

        private void TikTokUploadButton_Click(object sender, RoutedEventArgs e)
        {
            string? candidate = null;
            string output = OutputTextBox.Text.Trim();
            if (!string.IsNullOrWhiteSpace(output))
            {
                string[] expected = GetExpectedOutputPaths(output);
                candidate = expected.Skip(1).FirstOrDefault(File.Exists) ?? expected.FirstOrDefault(File.Exists);
            }

            if (string.IsNullOrWhiteSpace(candidate))
            {
                string input = InputTextBox.Text.Trim();
                candidate = File.Exists(input) ? input : null;
            }

            var uploadWindow = new TikTokUploadWindow(candidate) { Owner = this };
            uploadWindow.ShowDialog();
        }

        private async void StartButton_Click(object sender, RoutedEventArgs e)
        {
            string input = InputTextBox.Text.Trim();
            string output = OutputTextBox.Text.Trim();

            _lastInput = input;
            _lastOutput = output;

            LogsTextBox.Clear();
            SetProgressVisual(0, "🏃", "Ready", Brushes.DeepSkyBlue);
            StageLabel.Text = "Ready";

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

            if (!WarnIfHeavyUpscaleThenRife())
                return;

            if (!ConfirmLargeFrameNvidiaEncoding())
                return;

            if (SummaryCheckBox.IsChecked == true && !ConfirmProcessingSummary(input, output))
                return;

            if (!await EnsureUpdateGateAsync())
                return;

            if (IsFreePlan() && GetRemainingFreeExports() <= 0)
            {
                MessageBox.Show(
                    this,
                    $"Trial ended. ForgeFlow allows {TrialManager.MaxTrialVideos} processing attempts only. Activate a license to continue.\n\nانتهت التجربة. يسمح ForgeFlow بـ {TrialManager.MaxTrialVideos} محاولات معالجة فقط. فعّل الترخيص للمتابعة.",
                    "Trial Ended | انتهت التجربة",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

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

            SetProgressVisual(0, "🏃", "Warming up", Brushes.DeepSkyBlue);
            StartRunnerAnimation();
            StageLabel.Text = "Starting...";
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
                    GenerateTikTok60Ultra = true,

                    // PC output
                    PcFps = 60,
                    PcCrf = 13,
                    PcPreset = "veryslow",
                    PcTune = "film",
                    PcKeyintSeconds = 1,

                    // TikTok base output
                    TikTokEncodeFps = 120,
                    TikTokTargetFps = 30,
                    FinalOutputTimeScale = 1.0,
                    TikTokPreset = "veryslow",
                    TikTokKeyintSeconds = 1,

                    // FIX: set CRF via dedicated properties — no manual TikTokRateControl string
                    TikTokCrf = 15,
                    TikTokMaxRate = "50M",
                    TikTokBufSize = "100M",
                    TikTok60UltraCrf = 12,
                    TikTok60UltraBitrate = "24M",
                    TikTok60UltraMaxRate = "24M",
                    TikTok60UltraBufSize = "48M",
                    TikTok60UltraPreset = "veryslow",
                    EnableTikTokAudioDecoy = true,
                    TikTokAudioDecoyPacketMultiplier = 10,
                    EnableTikTokTurboCopy = true,

                    // Pre-filter: disabled by default
                    EnablePreFilter = false,
                    PreFilterGraph = "nlmeans=s=3:p=3:r=7",

                    // Filters
                    EnableEnhanceFilters = true,

                    // Logging
                    VerboseLogging = false
                };
                ApplySelectedPreset(interpolator);
                ApplySelectedVisualStyle(interpolator);
                ApplySelectedHardwareMode(interpolator);
                ApplySelectedMotion(interpolator);
                ApplySelectedUpscaleProfile(interpolator);
                ApplySelectedSubjectFocus(interpolator);

                AppendLog("Started");
                AppendLog("Input: " + Path.GetFileName(input));
                AppendLog("Output folder: " + (outDir ?? AppContext.BaseDirectory));
                AppendLog("Hardware: " + GetEffectiveHardwareModeDisplayName());
                AppendLog("Motion: " + GetSelectedMotionDisplayName());
                AppendLog("Pipeline: " + GetSelectedProcessingOrderDisplayName());
                AppendLog($"Visual style: {GetSelectedVisualStyleDisplayName()} ({GetSelectedVisualStyleStrengthPercent()}%)");
                AppendLog("Quality boost: " + GetSelectedUpscaleProfile().DisplayName);
                AppendLog($"Subject focus: {GetSelectedSubjectFocusDisplayName()} ({GetSelectedSubjectFocusStrengthPercent()}%)");
                if (!AdvanceLicenseCheckpoint(guardSession, "processing-started")) return;

                var res = await interpolator.InterpolateVideoAsync(
                    inputVideoPath: input,
                    outputVideoPath: output,
                    showConsoleWindows: false,
                    useNvdecForDecoding: string.Equals(GetSelectedHardwareMode(), "nvidia", StringComparison.OrdinalIgnoreCase),
                    progressCallback: msg => Dispatcher.Invoke(() => AppendLog(msg)),
                    cancellationToken: _cts.Token);

                if (!res.Success)
                {
                    FailUI("فشل:\n" + BuildFriendlyProcessingError(res.StandardError, res.StandardOutput));
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
                PlayCompletionSound(success: true);
                UpdateLicenseStatus();

                string[] produced = GetExpectedOutputPaths(output);
                LoadInlineOutputPreview(produced.FirstOrDefault(File.Exists));
                var successWindow = new SuccessWindow(produced[0], produced[1], produced[2])
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
                PlayCompletionSound(success: false);
                MessageBox.Show("تم إلغاء العملية.", "Canceled", MessageBoxButton.OK, MessageBoxImage.Information);
                RetryButton.IsEnabled = true;
            }
            catch (Exception ex)
            {
                AppendLog("CRITICAL: " + ex);
                FailUI("خطأ:\n" + BuildFriendlyProcessingError(ex.Message, ""));
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

        private async void Glitch30Button_Click(object sender, RoutedEventArgs e)
        {
            if (!await EnsureUpdateGateAsync())
                return;

            string input = InputTextBox.Text.Trim();
            string ffmpeg = Path.Combine(GetBundledToolsDirectory(), "ffmpeg.exe");
            if (!File.Exists(input) || !File.Exists(ffmpeg))
            {
                MessageBox.Show(this, "اختر فيديو صالحًا وتأكد من جاهزية FFmpeg أولًا.", "Glitch 30", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string outputDirectory = Path.GetDirectoryName(OutputTextBox.Text.Trim()) ?? Path.GetDirectoryName(input) ?? AppContext.BaseDirectory;
            if (IsProtectedOrTemporaryOutputDirectory(outputDirectory))
            {
                MessageBox.Show(this, "اختر مجلد إخراج عادي مثل Desktop أو Videos.", "Glitch 30", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Directory.CreateDirectory(outputDirectory);
            string output = GetAvailableGlitch30OutputPath(outputDirectory);
            LicenseGuard.GuardSession? session = _licenseGuard.BeginProcessingSession(out GuardDecision decision);
            if (session == null)
            {
                MessageBox.Show(this, decision.Message, "Glitch 30", MessageBoxButton.OK, MessageBoxImage.Warning);
                UpdateLicenseStatus();
                return;
            }

            SetBusy(true);
            RetryButton.IsEnabled = false;
            _cts = new CancellationTokenSource();
            try
            {
                AppendLog("== Glitch 30 ==");
                AppendLog("Input: " + Path.GetFileName(input));
                AppendLog("Output: " + output);
                AppendLog("Mode: FFmpeg stream-copy slow-motion clip (2x time scale).");
                SetProgressVisual(5, "⚡", "Glitch 30", Brushes.Orange);

                if (!_licenseGuard.AdvanceProcessingSession(session, "glitch-configured", out string guardMessage))
                    throw new InvalidOperationException(guardMessage);

                string args = $"-hide_banner -y -nostdin -itsscale 2 -i {QuoteProcessArgument(input)} -map 0 -c:v copy -c:a copy {QuoteProcessArgument(output)}";
                if (!_licenseGuard.AdvanceProcessingSession(session, "glitch-started", out guardMessage))
                    throw new InvalidOperationException(guardMessage);

                int exitCode = await RunGlitchFfmpegAsync(ffmpeg, args, outputDirectory, _cts.Token);
                if (exitCode != 0 || !File.Exists(output) || new FileInfo(output).Length == 0)
                    throw new InvalidOperationException($"FFmpeg failed to create Glitch 30 (exit code {exitCode}).");

                if (!_licenseGuard.AdvanceProcessingSession(session, "glitch-output-created", out guardMessage) ||
                    !_licenseGuard.AdvanceProcessingSession(session, "glitch-output-verified", out guardMessage) ||
                    !_licenseGuard.CompleteProcessingSession(session, out guardMessage))
                {
                    TryDeleteGlitchOutput(output);
                    throw new InvalidOperationException(guardMessage);
                }

                SetProgressVisual(100, "🎉", "Glitch 30 ready", Brushes.LightGreen);
                StageLabel.Text = "Glitch 30 Done ✓";
                AppendLog("Glitch 30 created: " + output);
                UpdateLicenseStatus();
                MessageBox.Show(this, "تم إنشاء ملف Glitch 30 بنجاح:\n" + output, "Glitch 30", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (OperationCanceledException)
            {
                AppendLog("Glitch 30 canceled.");
                SetProgressMood("🛑", "Glitch canceled", Brushes.Goldenrod);
            }
            catch (Exception ex)
            {
                AppendLog("Glitch 30 failed: " + ex.Message);
                FailUI("فشل Glitch 30:\n" + ex.Message);
            }
            finally
            {
                _cts?.Dispose();
                _cts = null;
                SetBusy(false);
            }
        }

        private void EditBeforeEnhanceButton_Click(object sender, RoutedEventArgs e)
        {
            string input = InputTextBox.Text.Trim();
            if (!File.Exists(input)) { MessageBox.Show(this, "اختر فيديو أولًا.", "Edit Workspace"); return; }
            new LiveEditorWindow(input, _editOptions) { Owner = this }.ShowDialog();
        }

        private static async Task<int> RunGlitchFfmpegAsync(string ffmpeg, string arguments, string workingDirectory, CancellationToken cancellationToken)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var process = new Process { StartInfo = startInfo };
            process.Start();
            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode;
        }

        private static string GetAvailableGlitch30OutputPath(string outputDirectory)
        {
            string candidate = Path.Combine(outputDirectory, "GLITCHED30.mp4");
            if (!File.Exists(candidate))
                return candidate;

            for (int index = 2; index < 1000; index++)
            {
                candidate = Path.Combine(outputDirectory, $"GLITCHED30_{index}.mp4");
                if (!File.Exists(candidate))
                    return candidate;
            }

            return Path.Combine(outputDirectory, $"GLITCHED30_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");
        }

        private static string QuoteProcessArgument(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

        private static void TryDeleteGlitchOutput(string output)
        {
            try
            {
                if (File.Exists(output))
                    File.Delete(output);
            }
            catch
            {
            }
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
            var about = new AboutWindow(GetCurrentPlanDisplayText())
            {
                Owner = this
            };
            about.ShowDialog();
        }

        private void SupportButton_Click(object sender, RoutedEventArgs e)
        {
            OpenActivationWindow();
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

        private void ReportIssueButton_Click(object sender, RoutedEventArgs e)
        {
            string reportPath = WriteSupportReport();
            Clipboard.SetText(reportPath);
            OpenFolder(Path.GetDirectoryName(reportPath) ?? AppContext.BaseDirectory);
            MessageBox.Show(
                this,
                "تم إنشاء تقرير الدعم ونسخ مساره إلى الحافظة:\n\n" + reportPath,
                "Support Report",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private void CompletionSoundCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_settings == null)
                return;

            _settings.PlayCompletionSound = CompletionSoundCheckBox.IsChecked == true;
            _settings.Save();
        }

        private void SummaryCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_settings == null)
                return;

            _settings.ShowProcessingSummary = SummaryCheckBox.IsChecked == true;
            _settings.Save();
        }

        private void UpscaleComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_uiInitialized)
                return;

            RefreshUpscaleInsight();
            RefreshOutputPreview();
            RefreshProcessingOrderState();
            if (_settings == null)
                return;

            _settings.SelectedUpscaleProfileId = GetSelectedUpscaleProfile().Id;
            _settings.Save();
        }

        private void HardwareComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_uiInitialized)
                return;

            RefreshOutputPreview();
            if (_settings == null)
                return;

            _settings.HardwareAccelerationMode = GetSelectedHardwareMode();
            _settings.Save();
        }

        private void MotionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_uiInitialized)
                return;

            RefreshUpscaleInsight();
            RefreshOutputPreview();
            RefreshProcessingOrderState();
            if (_settings == null)
                return;

            _settings.EnableRife = IsRifeEnabled();
            _settings.Save();
        }

        private void ProcessingOrderComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_uiInitialized)
                return;

            RefreshUpscaleInsight();
            RefreshSubjectFocusInsight();
            if (_settings == null)
                return;

            _settings.ProcessingOrder = GetSelectedProcessingOrder();
            _settings.Save();
        }

        private void SubjectFocusComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_uiInitialized)
                return;

            RefreshSubjectFocusInsight();
            RefreshOutputPreview();
            if (_settings == null)
                return;

            _settings.SubjectFocusMode = GetSelectedSubjectFocusTag();
            _settings.Save();
        }

        private void SubjectFocusStrengthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (SubjectFocusStrengthTextBlock == null)
                return;

            int strength = GetSelectedSubjectFocusStrengthPercent();
            SubjectFocusStrengthTextBlock.Text = strength + "%";
            RefreshSubjectFocusInsight();

            if (!_uiInitialized || _settings == null)
                return;

            _settings.SubjectFocusStrength = strength;
            _settings.Save();
        }

        private void VisualStyleComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_uiInitialized)
                return;

            RefreshVisualStyleInsight();
            RefreshOutputPreview();
            if (_settings == null)
                return;

            _settings.VisualStyle = GetSelectedVisualStyleTag();
            _settings.Save();
        }

        private void VisualStyleStrengthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (VisualStyleStrengthTextBlock == null)
                return;

            int strength = GetSelectedVisualStyleStrengthPercent();
            VisualStyleStrengthTextBlock.Text = strength + "%";
            RefreshVisualStyleInsight();

            if (!_uiInitialized || _settings == null)
                return;

            _settings.VisualStyleStrength = strength;
            _settings.Save();
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
            SupportButton.IsEnabled = !busy;
            UpdateButton.IsEnabled = !busy && !_updateCheckInProgress;
            Glitch30Button.IsEnabled = !busy && CanRunGlitch30();
            RefreshStartHint();
        }

        private bool CanStartProcessing()
        {
            return (_toolStatus?.Ready ?? false) &&
                   _updateGatePassed &&
                   _cts == null &&
                   HasRunnableInputOutput() &&
                   (IsProActivated() || GetRemainingFreeExports() > 0);
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
            SupportButton.IsEnabled = !busy;
            UpdateButton.IsEnabled = !busy && !_updateCheckInProgress;
            Glitch30Button.IsEnabled = !busy && CanRunGlitch30();
            RefreshStartHint();
        }

        private bool CanRunGlitch30()
        {
            string input = InputTextBox.Text.Trim();
            string ffmpeg = Path.Combine(GetBundledToolsDirectory(), "ffmpeg.exe");
            return _updateGatePassed &&
                   File.Exists(input) &&
                   SupportedVideoExtensions.Contains(Path.GetExtension(input), StringComparer.OrdinalIgnoreCase) &&
                   File.Exists(ffmpeg) &&
                   _licenseGuard.EvaluateAccess().Allowed;
        }


        private void RefreshStartHint()
        {
            string hint = GetStartReadinessHint();
            StartHintTextBlock.Text = hint;
            StartHintTextBlock.Foreground = CanStartProcessing() ? Brushes.LightGreen : Brushes.Gray;
            StartButton.ToolTip = hint;
        }

        private string GetStartReadinessHint()
        {
            if (_cts != null)
                return "Processing is already running.";

            if (_updateCheckInProgress)
                return "Start is waiting for the update check to finish.";

            if (!(_toolStatus?.Ready ?? false))
                return "Start is waiting for verified processing tools. Use Test Tools for details.";

            if (!_updateGatePassed)
                return "Start is waiting for the mandatory update check.";

            string input = InputTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(input))
                return "Choose an input video to unlock Start.";

            if (!File.Exists(input))
                return "Input video was not found. Choose a valid file.";

            if (!SupportedVideoExtensions.Contains(Path.GetExtension(input), StringComparer.OrdinalIgnoreCase))
                return "Input format is not supported. Choose a common video file.";

            string output = OutputTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(output))
                return "Choose an MP4 output path to unlock Start.";

            if (string.IsNullOrWhiteSpace(Path.GetDirectoryName(output)))
                return "Output folder is not valid. Choose an output path.";

            if (!string.Equals(Path.GetExtension(output), ".mp4", StringComparison.OrdinalIgnoreCase))
                return "Output must be an MP4 file.";

            if (PathsEqual(input, output))
                return "Output cannot be the same file as input.";

            if (IsFreePlan() && GetRemainingFreeExports() <= 0)
                return $"Trial ended after {TrialManager.MaxTrialVideos} attempts. Activate a license to continue.";

            return "Ready to process.";
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
            if (!_uiInitialized)
                return;

            string preset = ((PresetComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Balanced").Trim();
            if (string.Equals(preset, "Aesthetic Vivid", StringComparison.OrdinalIgnoreCase))
            {
                SelectComboItemByTag(VisualStyleComboBox, "tiktok-vivid");
                if (VisualStyleStrengthSlider != null && VisualStyleStrengthSlider.Value < 75)
                    VisualStyleStrengthSlider.Value = 80;
            }

            RefreshPresetInsight();
            RefreshVisualStyleInsight();
            RefreshOutputPreview();
        }

        private void LoadUpscaleProfiles()
        {
            _upscaleProfiles.Clear();
            _upscaleProfiles.Add(new UpscaleProfile
            {
                Id = "off",
                DisplayName = "Off",
                Insight = "Quality boost is off.",
                Engine = "none",
                Scale = 1
            });

            string toolsDir = GetBundledToolsDirectory();
            string nanoVsrModelsDir = Path.Combine(toolsDir, "nanovsr");
            string realesrganModelsDir = Path.Combine(toolsDir, "models");
            string realcuganModelsDir = Path.Combine(toolsDir, "models-se");

            string nanoVsrModel = Path.Combine(nanoVsrModelsDir, "nanovsr_644k_t15_directml.onnx");
            if (File.Exists(nanoVsrModel))
            {
                _upscaleProfiles.Add(new UpscaleProfile
                {
                    Id = "nanovsr:nanovsr:nanovsr_644k_t15_directml.onnx",
                    DisplayName = "NanoVSR Video x4",
                    Insight = "Temporal 4x video restoration using 15-frame context and bundled DirectML GPU inference.",
                    Engine = "nanovsr",
                    ModelDirectoryName = "nanovsr",
                    ModelName = "nanovsr_644k_t15_directml.onnx",
                    Scale = 4
                });
            }

            if (Directory.Exists(realesrganModelsDir))
            {
                AddProfileIfPresent(realesrganModelsDir, "realesr-animevideov3-x2", "Anime Video x2", "Real-ESRGAN anime video model with 2x upscale.", "realesrgan", 2);
                AddProfileIfPresent(realesrganModelsDir, "realesr-animevideov3-x3", "Anime Video x3", "Real-ESRGAN anime video model with 3x upscale.", "realesrgan", 3);
                AddProfileIfPresent(realesrganModelsDir, "realesr-animevideov3-x4", "Anime Video x4", "Real-ESRGAN anime video model with 4x upscale.", "realesrgan", 4);
                AddProfileIfPresent(realesrganModelsDir, "realesrgan-x4plus-anime", "Real-ESRGAN Anime x4", "Sharper anime-oriented 4x upscale using Real-ESRGAN.", "realesrgan", 4);
                AddProfileIfPresent(realesrganModelsDir, "realesrnet-x4plus", "Real-ESRNet x4", "Natural-looking 4x upscale with lighter sharpening.", "realesrgan", 4);
                AddProfileIfPresent(realesrganModelsDir, "realesrnet-x4plus-x4", "Real-ESRNet x4 Plus", "Alternative 4x Real-ESRNet profile for softer restoration.", "realesrgan", 4);
            }

            if (Directory.Exists(realcuganModelsDir))
            {
                AddProfileIfPresent(realcuganModelsDir, "up2x-no-denoise", "Real-CUGAN x2 Clean", "2x upscale with minimal denoise for anime lines and UI captures.", "realcugan", 2, "models-se");
                AddProfileIfPresent(realcuganModelsDir, "up3x-no-denoise", "Real-CUGAN x3 Clean", "3x upscale with minimal denoise for anime-style material.", "realcugan", 3, "models-se");
                AddProfileIfPresent(realcuganModelsDir, "up4x-no-denoise", "Real-CUGAN x4 Clean", "4x upscale with minimal denoise for maximum retained texture.", "realcugan", 4, "models-se");
            }

            UpscaleComboBox.Items.Clear();
            foreach (UpscaleProfile profile in _upscaleProfiles)
            {
                UpscaleComboBox.Items.Add(new ComboBoxItem
                {
                    Content = profile.DisplayName,
                    Tag = profile.Id
                });
            }

            string wantedId = string.IsNullOrWhiteSpace(_settings.SelectedUpscaleProfileId)
                ? "off"
                : _settings.SelectedUpscaleProfileId;

            ComboBoxItem? selected = UpscaleComboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag as string, wantedId, StringComparison.OrdinalIgnoreCase));

            UpscaleComboBox.SelectedItem = selected ?? UpscaleComboBox.Items.OfType<ComboBoxItem>().FirstOrDefault();
            RefreshUpscaleInsight();
            RefreshProcessingOrderState();
        }

        private void AddProfileIfPresent(string modelsRoot, string modelName, string displayName, string insight, string engine, int scale, string? explicitModelDirectoryName = null)
        {
            string paramPath = Path.Combine(modelsRoot, modelName + ".param");
            string binPath = Path.Combine(modelsRoot, modelName + ".bin");
            if (!File.Exists(paramPath) || !File.Exists(binPath))
                return;

            string modelDirectoryName = explicitModelDirectoryName
                ?? new DirectoryInfo(modelsRoot).Name;

            _upscaleProfiles.Add(new UpscaleProfile
            {
                Id = engine + ":" + modelDirectoryName + ":" + modelName,
                DisplayName = displayName,
                Insight = insight,
                Engine = engine,
                ModelDirectoryName = modelDirectoryName,
                ModelName = modelName,
                Scale = scale
            });
        }

        private void ApplySelectedUpscaleProfile(VideoInterpolator interpolator)
        {
            UpscaleProfile profile = GetSelectedUpscaleProfile();
            interpolator.EnableUpscale = profile.Engine != "none";
            interpolator.UpscaleScale = profile.Scale;
            interpolator.UpscaleModelName = profile.ModelName;
            interpolator.UpscaleDisplayName = profile.DisplayName;
            interpolator.UpscaleModelDirectory = Path.Combine(GetBundledToolsDirectory(), profile.ModelDirectoryName);
            interpolator.UpscaleExecutablePath = profile.Engine switch
            {
                "realesrgan" => Path.Combine(GetBundledToolsDirectory(), RealEsrganExeName),
                "realcugan" => Path.Combine(GetBundledToolsDirectory(), RealCuganExeName),
                _ => ""
            };
            interpolator.UpscaleEngine = profile.Engine;
            // Let NCNN choose the Vulkan device and tile size from the actual frame/model.
            // A fixed tile can be faster in a tiny benchmark but much slower for x3/x4 footage.
            interpolator.UpscaleGpuId = "auto";
            interpolator.UpscaleTileSize = 0;
            interpolator.UpscaleThreads = "1:2:2";
        }

        private void ApplySelectedMotion(VideoInterpolator interpolator)
        {
            interpolator.EnableRifeInterpolation = IsRifeEnabled();
            interpolator.ProcessUpscaleBeforeRife = string.Equals(GetSelectedProcessingOrder(), "upscale-then-rife", StringComparison.OrdinalIgnoreCase);
        }

        private void ApplySelectedSubjectFocus(VideoInterpolator interpolator)
        {
            SubjectFocusMode mode = GetSelectedSubjectFocusMode();
            interpolator.SubjectFocusMode = mode;
            interpolator.EnableSubjectFocus = mode != SubjectFocusMode.Off;
            interpolator.SubjectFocusStrength = GetSelectedSubjectFocusStrengthPercent() / 100.0;
            interpolator.SubjectFocusModelPath = Path.Combine(GetBundledToolsDirectory(), SubjectFocusModelRelativePath);
        }

        private void ApplySelectedHardwareMode(VideoInterpolator interpolator)
        {
            string mode = GetEffectiveHardwareMode();
            interpolator.HardwareAccelerationMode = mode;
            interpolator.UseNvidiaHevcEncoder = ShouldUseNvidiaHevcEncoder();
        }

        private bool IsProActivated()
        {
            GuardDecision decision = _licenseGuard.EvaluateAccess();
            return decision.Allowed && decision.Mode == LicenseAccessMode.Licensed;
        }

        private bool IsFreePlan()
        {
            return !IsProActivated();
        }

        private int GetRemainingFreeExports()
        {
            var (allowed, remaining, _) = TrialManager.CheckTrial();
            return allowed ? remaining : 0;
        }

        private string GetCurrentPlanDisplayText()
        {
            if (IsProActivated())
                return "Pro activated | unlimited exports | CPU + NVIDIA + AMD | Quality Boost unlocked";

            int remaining = GetRemainingFreeExports();
            return $"Trial | {remaining}/{TrialManager.MaxTrialVideos} attempts left | full features";
        }

        private void ApplyPlanRestrictionsToUi(bool showMessage)
        {
            RefreshPlanOptionLabels();
            RefreshProcessingOrderState();
        }

        private void RefreshPlanOptionLabels()
        {
            HardwareTitleTextBlock.Text = "Hardware";
            MotionTitleTextBlock.Text = "Motion";
            QualityBoostTitleTextBlock.Text = "Quality Boost";
            PipelineOrderTitleTextBlock.Text = "Pipeline Order";
            SubjectFocusTitleTextBlock.Text = "Subject Focus";
            HardwareHintTextBlock.Text = "All modes are available during the 3-attempt trial.\nكل المودات متاحة خلال التجربة ذات 3 محاولات.";
            QualityBoostHintTextBlock.Text = "";
            PipelineHintTextBlock.Text = "";

            SetComboItemContent(HardwareComboBox, "cpu", "CPU", "Software processing.");
            SetComboItemContent(HardwareComboBox, "nvidia", "NVIDIA NVENC/NVDEC", "Use NVIDIA hardware decoding and encoding when supported.");
            SetComboItemContent(HardwareComboBox, "amd", "AMD AMF", "Use AMD hardware decoding and encoding when supported.");
            SetComboItemContent(ProcessingOrderComboBox, "rife-then-upscale", "RIFE then Upscale", "Interpolate first, then upscale the final frames.");
            SetComboItemContent(ProcessingOrderComboBox, "upscale-then-rife", "Upscale then RIFE", "Upscale first, then let RIFE interpolate the larger frames.");

            foreach (ComboBoxItem item in UpscaleComboBox.Items.OfType<ComboBoxItem>())
            {
                string id = item.Tag as string ?? string.Empty;
                UpscaleProfile profile = _upscaleProfiles.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))
                    ?? _upscaleProfiles.First();
                item.Content = profile.DisplayName;
                item.ToolTip = profile.Insight;
            }

            SetComboItemContent(SubjectFocusComboBox, "off", "Off", "No foreground matting.");
            SetComboItemContent(SubjectFocusComboBox, "protect", "Protect Edges", "Use a person mask to keep subject edges cleaner.");
            SetComboItemContent(SubjectFocusComboBox, "enhance", "Enhance Subject", "Sharpen the foreground subject while keeping the background softer.");
            SetComboItemContent(SubjectFocusComboBox, "soften-bg", "Soften Background", "Gently blur the background behind the subject.");
            RefreshSubjectFocusInsight();
        }

        private static void SetComboItemContent(ComboBox comboBox, string tag, string content, string toolTip)
        {
            ComboBoxItem? item = comboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(entry => string.Equals(entry.Tag as string, tag, StringComparison.OrdinalIgnoreCase));
            if (item == null)
                return;

            item.Content = content;
            item.ToolTip = toolTip;
        }

        private void ShowWelcomeWindowIfNeeded()
        {
            if (_settings.HasSeenWelcomeScreen)
                return;

            _settings.HasSeenWelcomeScreen = true;
            _settings.Save();

            var welcome = new WelcomeWindow
            {
                Owner = this
            };

            welcome.ShowDialog();
            if (welcome.OpenUpgrade)
                OpenActivationWindow();
        }

        private UpscaleProfile GetSelectedUpscaleProfile()
        {
            string? id = (UpscaleComboBox.SelectedItem as ComboBoxItem)?.Tag as string;
            return _upscaleProfiles.FirstOrDefault(profile => string.Equals(profile.Id, id, StringComparison.OrdinalIgnoreCase))
                ?? _upscaleProfiles.First();
        }

        private void RefreshUpscaleInsight()
        {
            bool rife = IsRifeEnabled();
            UpscaleProfile profile = _upscaleProfiles.Count == 0
                ? new UpscaleProfile { DisplayName = "Off", Insight = "Quality boost is off." }
                : GetSelectedUpscaleProfile();

            bool upscale = profile.Engine != "none";
            string order = GetSelectedProcessingOrder();
            UpscaleInsightTextBlock.Text = (rife, upscale, order) switch
            {
                (true, true, "upscale-then-rife") => $"{profile.DisplayName} will run first, then RIFE will add smoothness.",
                (true, true, _) => $"RIFE will run first, then {profile.DisplayName} will boost resolution.",
                (true, false, _) => "RIFE is on. Motion will be improved without quality boost.",
                (false, true, _) => $"Only {profile.DisplayName} will run. Resolution changes without motion interpolation.",
                _ => "RIFE and quality boost are both off. Export will keep the source motion and resolution."
            };
            RefreshProcessingOrderState();
        }

        private void RefreshSubjectFocusInsight()
        {
            if (SubjectFocusStrengthSlider == null || SubjectFocusStrengthTextBlock == null)
                return;

            bool enabled = GetSelectedSubjectFocusMode() != SubjectFocusMode.Off;
            SubjectFocusStrengthSlider.IsEnabled = enabled;
            SubjectFocusStrengthTextBlock.Opacity = enabled ? 1.0 : 0.35;
            SubjectFocusStrengthSlider.Opacity = enabled ? 1.0 : 0.35;
            int strength = GetSelectedSubjectFocusStrengthPercent();
            SubjectFocusStrengthTextBlock.Text = strength + "%";
            SubjectFocusHintTextBlock.Text = GetSelectedSubjectFocusMode() switch
            {
                SubjectFocusMode.ProtectEdges => $"Beta: edge protection strength {strength}%.\nبيتا: قوة حماية الحواف {strength}%.",
                SubjectFocusMode.EnhanceSubject => $"Beta: subject clarity strength {strength}%.\nبيتا: قوة توضيح الشخص {strength}%.",
                SubjectFocusMode.SoftenBackground => $"Beta: background softness strength {strength}%.\nبيتا: قوة تنعيم الخلفية {strength}%.",
                _ => ""
            };
        }

        private void RefreshVisualStyleInsight()
        {
            if (VisualStyleStrengthSlider == null || VisualStyleStrengthTextBlock == null || PresetInsightTextBlock == null)
                return;

            bool vivid = !string.Equals(GetSelectedVisualStyleTag(), "natural", StringComparison.OrdinalIgnoreCase);
            VisualStyleStrengthSlider.IsEnabled = vivid;
            VisualStyleStrengthSlider.Opacity = vivid ? 1.0 : 0.35;
            VisualStyleStrengthTextBlock.Opacity = vivid ? 1.0 : 0.35;
            VisualStyleStrengthTextBlock.Text = GetSelectedVisualStyleStrengthPercent() + "%";
        }

        private void RefreshProcessingOrderState()
        {
            ProcessingOrderComboBox.IsEnabled = true;
        }

        private bool IsRifeEnabled()
        {
            return string.Equals((MotionComboBox.SelectedItem as ComboBoxItem)?.Tag as string, "on", StringComparison.OrdinalIgnoreCase);
        }

        private string GetSelectedMotionDisplayName()
        {
            return IsRifeEnabled() ? "RIFE On" : "RIFE Off";
        }

        private string GetSelectedHardwareMode()
        {
            return (HardwareComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "cpu";
        }

        private string GetSelectedHardwareModeDisplayName()
        {
            return GetSelectedHardwareMode() switch
            {
                "nvidia" => "NVIDIA NVENC/NVDEC",
                "amd" => "AMD AMF",
                _ => "CPU"
            };
        }

        private string GetSelectedProcessingOrder()
        {
            return (ProcessingOrderComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "rife-then-upscale";
        }

        private string GetSelectedSubjectFocusTag()
        {
            return (SubjectFocusComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "off";
        }

        private SubjectFocusMode GetSelectedSubjectFocusMode()
        {
            return GetSelectedSubjectFocusTag() switch
            {
                "protect" => SubjectFocusMode.ProtectEdges,
                "enhance" => SubjectFocusMode.EnhanceSubject,
                "soften-bg" => SubjectFocusMode.SoftenBackground,
                _ => SubjectFocusMode.Off
            };
        }

        private int GetSelectedSubjectFocusStrengthPercent()
        {
            return (int)Math.Round(Math.Clamp(SubjectFocusStrengthSlider?.Value ?? 70, 0, 100));
        }

        private string GetSelectedSubjectFocusDisplayName()
        {
            return GetSelectedSubjectFocusMode() switch
            {
                SubjectFocusMode.ProtectEdges => "Protect Edges",
                SubjectFocusMode.EnhanceSubject => "Enhance Subject",
                SubjectFocusMode.SoftenBackground => "Soften Background",
                _ => "Off"
            };
        }

        private string GetSelectedVisualStyleTag()
        {
            return (VisualStyleComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "natural";
        }

        private int GetSelectedVisualStyleStrengthPercent()
        {
            return (int)Math.Round(Math.Clamp(VisualStyleStrengthSlider?.Value ?? 70, 0, 100));
        }

        private string GetSelectedVisualStyleDisplayName()
        {
            return GetSelectedVisualStyleTag() switch
            {
                "tiktok-vivid" => "TikTok Vivid",
                "tiktok-vivid-plus" => "Vivid+",
                _ => "Natural"
            };
        }

        private string GetSelectedProcessingOrderDisplayName()
        {
            return string.Equals(GetSelectedProcessingOrder(), "upscale-then-rife", StringComparison.OrdinalIgnoreCase)
                ? "Upscale then RIFE"
                : "RIFE then Upscale";
        }

        private int GetSelectedUpscaleScale()
        {
            UpscaleProfile profile = GetSelectedUpscaleProfile();
            return Math.Max(1, profile.Scale);
        }

        private (int Width, int Height) GetEstimatedOutputDimensions()
        {
            if (_selectedVideoInfo == null)
                return (0, 0);

            int scale = GetSelectedUpscaleProfile().Engine == "none" ? 1 : GetSelectedUpscaleScale();
            return (_selectedVideoInfo.Width * scale, _selectedVideoInfo.Height * scale);
        }

        private string GetEffectiveHardwareMode()
        {
            string selected = GetSelectedHardwareMode();
            if (!string.Equals(selected, "nvidia", StringComparison.OrdinalIgnoreCase))
                return selected;

            (int width, int height) = GetEstimatedOutputDimensions();
            if (Math.Max(width, height) > 8192)
                return "cpu";

            return selected;
        }

        private bool ShouldUseNvidiaHevcEncoder()
        {
            if (!string.Equals(GetEffectiveHardwareMode(), "nvidia", StringComparison.OrdinalIgnoreCase))
                return false;

            (int width, int height) = GetEstimatedOutputDimensions();
            return width > 4096 || height > 4096;
        }

        private string GetEffectiveHardwareModeDisplayName()
        {
            string selected = GetSelectedHardwareMode();
            string effective = GetEffectiveHardwareMode();
            if (string.Equals(selected, "nvidia", StringComparison.OrdinalIgnoreCase) && !string.Equals(effective, selected, StringComparison.OrdinalIgnoreCase))
                return "CPU (NVENC skipped: frame > 8192)";

            if (ShouldUseNvidiaHevcEncoder())
                return "NVIDIA HEVC NVENC (large frame)";

            return effective switch
            {
                "nvidia" => "NVIDIA NVENC/NVDEC",
                "amd" => "AMD AMF",
                _ => "CPU"
            };
        }

        private bool WarnIfHeavyUpscaleThenRife()
        {
            if (_selectedVideoInfo == null)
                return true;

            bool heavyCase = IsRifeEnabled()
                             && string.Equals(GetSelectedProcessingOrder(), "upscale-then-rife", StringComparison.OrdinalIgnoreCase)
                             && GetSelectedUpscaleProfile().Engine != "none"
                             && GetSelectedUpscaleScale() >= 4;
            if (!heavyCase)
                return true;

            (int width, int height) = GetEstimatedOutputDimensions();
            string message =
                "تحذير قبل البدء:\n\n" +
                "أنت اخترت Upscale then RIFE مع تكبير x4، وهذا قد يضغط VRAM بقوة حتى على الكروت القوية.\n\n" +
                $"الدقة المتوقعة قبل RIFE: {width}x{height}\n" +
                "قد يؤدي هذا إلى أخطاء Vulkan أو بطء شديد.\n\n" +
                "التوصية: استخدم RIFE then Upscale أو خفّض التكبير إلى x2 أو x3.\n\n" +
                "هل تريد المتابعة رغم ذلك؟";

            return MessageBox.Show(this, message, "High VRAM Load Warning", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
        }

        private bool ConfirmLargeFrameNvidiaEncoding()
        {
            if (!ShouldUseNvidiaHevcEncoder())
                return true;

            (int width, int height) = GetEstimatedOutputDimensions();
            string message =
                "الدقة المتوقعة بعد رفع الجودة كبيرة على H.264 NVENC:\n\n" +
                $"{width}x{height}\n\n" +
                "سيستخدم ForgeFlow ترميز NVIDIA HEVC بدل H.264 لتجنب خطأ Invalid argument والرجوع البطيء إلى CPU.\n\n" +
                "هل تريد المتابعة؟";

            return MessageBox.Show(this, message, "Large NVIDIA Encode", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes;
        }

        private static void SelectComboItemByTag(ComboBox comboBox, string wantedTag)
        {
            ComboBoxItem? selected = comboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag as string, wantedTag, StringComparison.OrdinalIgnoreCase));
            if (selected != null)
                comboBox.SelectedItem = selected;
        }

        private static string GetBundledToolsDirectory()
        {
            return Path.Combine(AppContext.BaseDirectory, BundledToolsFolderName);
        }

        private static bool IsDevelopmentBuild()
        {
#if DEBUG
            return true;
#else
            return false;
#endif
        }

        private void ApplyDevelopmentUpdateBypassUi(string message)
        {
            _updateGatePassed = true;
            ApplyToolStatusToUi(_toolStatus ?? RefreshToolsStatus(showDetails: false));
            EstimateTextBlock.Text = message;
            EstimateTextBlock.Foreground = Brushes.Goldenrod;
            RefreshActionButtons();
        }

        private bool EnsureAccessBeforeProcessing()
        {
            GuardDecision decision = _licenseGuard.EvaluateAccess();
            if (decision.Allowed)
            {
                if (decision.Mode == LicenseAccessMode.Trial)
                {
                    return true;
                }

                return true;
            }

            var openActivation = MessageBox.Show(
                this,
                decision.Message + "\n\nهل تريد فتح التفعيل أو المتجر الآن؟",
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

                if (IsDevelopmentBuild())
                {
                    AppendLog("WARNING: Development build bypassed mandatory update gate.");
                    ApplyDevelopmentUpdateBypassUi("Development build: update gate bypassed for local testing.");
                    if (showNoUpdateMessage)
                    {
                        MessageBox.Show(this, "تم تجاوز بوابة التحديث الإجباري لأن هذه نسخة تطوير للتجربة المحلية.", "Development Build", MessageBoxButton.OK, MessageBoxImage.Information);
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

                if (IsDevelopmentBuild())
                {
                    AppendLog("WARNING: Update check failed in development build: " + ex.Message);
                    ApplyDevelopmentUpdateBypassUi("Development build: update check failed, gate bypassed for local testing.");
                    if (showNoUpdateMessage)
                    {
                        MessageBox.Show(this, "تعذر فحص التحديثات، لكن تم السماح بالتشغيل لأن هذه نسخة تطوير محلية.\n\n" + ex.Message, "Development Build", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    return true;
                }

                _updateGatePassed = false;
                ToolsStatusTextBlock.Text = "Update check failed";
                ToolsStatusTextBlock.Foreground = Brushes.Orange;
                EstimateTextBlock.Text = "Start and activation remain locked until mandatory update check succeeds.";
                EstimateTextBlock.Foreground = Brushes.Orange;
                if (showNoUpdateMessage)
                {
                    AppendLog("WARNING: Update check failed: " + ex.Message);
                    MessageBox.Show(
                        this,
                        "تعذر فحص التحديثات حالياً. قد يكون GitHub مشغولاً أو وصل إلى حد الطلبات المؤقت.\n\n" +
                        "انتظر دقيقة ثم اضغط Updates مرة أخرى، أو ثبّت آخر نسخة من صفحة التحديثات.\n\n" +
                        ex.Message,
                        "Updates",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
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
                    interpolator.TikTok60UltraBitrate = "15M";
                    interpolator.TikTok60UltraMaxRate = "15M";
                    interpolator.TikTok60UltraBufSize = "30M";
                    interpolator.PngCompressionLevel = 1;
                    break;
                case "Quality":
                    interpolator.PcCrf = 13;
                    interpolator.PcPreset = "veryslow";
                    interpolator.TikTokCrf = 15;
                    interpolator.TikTokPreset = "veryslow";
                    interpolator.TikTokMaxRate = "50M";
                    interpolator.TikTokBufSize = "100M";
                    interpolator.TikTok60UltraBitrate = "24M";
                    interpolator.TikTok60UltraMaxRate = "24M";
                    interpolator.TikTok60UltraBufSize = "48M";
                    interpolator.PngCompressionLevel = 1;
                    break;
                case "Aesthetic Vivid":
                    interpolator.PcCrf = 12;
                    interpolator.PcPreset = "veryslow";
                    interpolator.TikTokCrf = 14;
                    interpolator.TikTokPreset = "veryslow";
                    interpolator.TikTokMaxRate = "70M";
                    interpolator.TikTokBufSize = "140M";
                    interpolator.TikTok60UltraBitrate = "48M";
                    interpolator.TikTok60UltraMaxRate = "48M";
                    interpolator.TikTok60UltraBufSize = "96M";
                    interpolator.PngCompressionLevel = 1;
                    break;
                default:
                    interpolator.PcCrf = 15;
                    interpolator.PcPreset = "slow";
                    interpolator.TikTokCrf = 17;
                    interpolator.TikTokPreset = "slow";
                    interpolator.TikTokMaxRate = "42M";
                    interpolator.TikTokBufSize = "84M";
                    interpolator.TikTok60UltraBitrate = "15M";
                    interpolator.TikTok60UltraMaxRate = "15M";
                    interpolator.TikTok60UltraBufSize = "30M";
                    interpolator.PngCompressionLevel = 1;
                    break;
            }
        }

        private void ApplySelectedVisualStyle(VideoInterpolator interpolator)
        {
            interpolator.EnhanceFilterStyle = GetSelectedVisualStyleTag();
            interpolator.EnhanceFilterStrength = GetSelectedVisualStyleStrengthPercent() / 100.0;
        }

        private static void OpenUpdateUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return;

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch
            {
            }
        }

        private string GetProRemainingDisplay()
        {
            LicenseInfo license = _licenseManager.CurrentLicense;
            if (license.Type == LicenseType.Lifetime)
                return "Lifetime | مدى الحياة";

            if (!license.ExpiresOn.HasValue)
                return "Active | نشط";

            TimeSpan remaining = license.ExpiresOn.Value - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
                return "Expired | منتهي";

            if (remaining.TotalDays >= 60)
                return $"{Math.Ceiling(remaining.TotalDays / 30)} months left | متبقي {Math.Ceiling(remaining.TotalDays / 30)} أشهر";

            if (remaining.TotalDays >= 2)
                return $"{Math.Ceiling(remaining.TotalDays)} days left | متبقي {Math.Ceiling(remaining.TotalDays)} أيام";

            if (remaining.TotalHours >= 2)
                return $"{Math.Ceiling(remaining.TotalHours)} hours left | متبقي {Math.Ceiling(remaining.TotalHours)} ساعات";

            return "Less than 2 hours left | متبقي أقل من ساعتين";
        }

        private string GetProExpiryDateDisplay()
        {
            LicenseInfo license = _licenseManager.CurrentLicense;
            if (license.Type == LicenseType.Lifetime)
                return "Expires: Never | تاريخ الانتهاء: لا ينتهي";

            if (!license.ExpiresOn.HasValue)
                return "Expires: Unknown | تاريخ الانتهاء: غير معروف";

            return $"Expires: {license.ExpiresOn.Value:yyyy-MM-dd} | تاريخ الانتهاء: {license.ExpiresOn.Value:yyyy-MM-dd}";
        }

        private Brush GetProStatusBrush()
        {
            LicenseInfo license = _licenseManager.CurrentLicense;
            if (license.Type == LicenseType.Lifetime || !license.ExpiresOn.HasValue)
                return Brushes.LightGreen;

            TimeSpan remaining = license.ExpiresOn.Value - DateTime.UtcNow;
            if (remaining.TotalDays < 3)
                return Brushes.OrangeRed;

            if (remaining.TotalDays < 7)
                return Brushes.Gold;

            return Brushes.LightGreen;
        }

        private void UpdateLicenseStatus()
        {
            GuardDecision decision = _licenseGuard.EvaluateAccess();
            if (decision.Allowed && decision.Mode == LicenseAccessMode.Licensed)
            {
                Brush proBrush = GetProStatusBrush();
                bool warning = proBrush != Brushes.LightGreen;
                PlanBadgeTextBlock.Text = warning ? "PRO ENDING SOON" : "PRO ACTIVE";
                PlanBadgeBorder.Background = warning
                    ? new SolidColorBrush(Color.FromRgb(74, 41, 16))
                    : new SolidColorBrush(Color.FromRgb(26, 66, 44));
                PlanBadgeBorder.BorderBrush = warning
                    ? new SolidColorBrush(Color.FromRgb(255, 159, 10))
                    : new SolidColorBrush(Color.FromRgb(48, 209, 88));
                PlanBadgeTextBlock.Foreground = warning
                    ? new SolidColorBrush(Color.FromRgb(255, 223, 179))
                    : new SolidColorBrush(Color.FromRgb(202, 255, 220));
                LicenseStatusTextBlock.Text = "Plan: Pro | " + GetProRemainingDisplay();
                LicenseStatusTextBlock.Foreground = proBrush;
                PlanDetailsTextBlock.Text = GetProExpiryDateDisplay() + " | Unlimited exports | CPU + NVIDIA + AMD | Quality Boost unlocked";
                PlanDetailsTextBlock.Foreground = warning ? proBrush : Brushes.LightGray;
                ActivationButton.Content = "License";
                ApplyPlanRestrictionsToUi(showMessage: false);
                RefreshActionButtons();
                return;
            }

            if (decision.Allowed && decision.Mode == LicenseAccessMode.Trial)
            {
                PlanBadgeTextBlock.Text = "TRIAL";
                PlanBadgeBorder.Background = new SolidColorBrush(Color.FromRgb(44, 32, 19));
                PlanBadgeBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(255, 159, 10));
                PlanBadgeTextBlock.Foreground = new SolidColorBrush(Color.FromRgb(255, 224, 173));
                LicenseStatusTextBlock.Text = $"{decision.Remaining}/{TrialManager.MaxTrialVideos} trials left";
                LicenseStatusTextBlock.Foreground = new SolidColorBrush(Color.FromRgb(255, 179, 64));
                PlanDetailsTextBlock.Text = "Full feature trial";
                PlanDetailsTextBlock.Foreground = new SolidColorBrush(Color.FromRgb(142, 142, 147));
                ActivationButton.Content = "Activate / Buy";
                ApplyPlanRestrictionsToUi(showMessage: false);
                RefreshActionButtons();
                return;
            }

            PlanBadgeTextBlock.Text = "TRIAL ENDED";
            PlanBadgeBorder.Background = new SolidColorBrush(Color.FromRgb(74, 41, 16));
            PlanBadgeBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(255, 159, 10));
            PlanBadgeTextBlock.Foreground = new SolidColorBrush(Color.FromRgb(255, 223, 179));
            LicenseStatusTextBlock.Text = "Activation required";
            LicenseStatusTextBlock.Foreground = System.Windows.Media.Brushes.Orange;
            PlanDetailsTextBlock.Text = $"0/{TrialManager.MaxTrialVideos} trials left";
            PlanDetailsTextBlock.Foreground = Brushes.Orange;
            ActivationButton.Content = "Activate / Buy";
            ApplyPlanRestrictionsToUi(showMessage: false);
            RefreshActionButtons();
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

        private bool ConfirmProcessingSummary(string inputPath, string outputPath)
        {
            string preset = ((PresetComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Balanced").Trim();
            string[] outputs = GetExpectedOutputPaths(outputPath);
            string inputName = Path.GetFileName(inputPath);
            string videoInfo = _selectedVideoInfo?.Summary ?? "Video details: not loaded";
            string estimate = _selectedVideoInfo?.Estimate ?? "Estimate: depends on video and GPU";

            string message =
                "ملخص المعالجة قبل البدء:\n\n" +
                $"الفيديو: {inputName}\n" +
                $"الوضع: {preset}\n" +
                $"التسريع: {GetEffectiveHardwareModeDisplayName()}\n" +
                $"السلاسة: {GetSelectedMotionDisplayName()}\n" +
                $"ستايل الألوان: {GetSelectedVisualStyleDisplayName()} ({GetSelectedVisualStyleStrengthPercent()}%)\n" +
                $"الترتيب: {GetSelectedProcessingOrderDisplayName()}\n" +
                $"رفع الجودة: {GetSelectedUpscaleProfile().DisplayName}\n" +
                $"{videoInfo}\n" +
                $"{estimate}\n\n" +
                "سيتم إنشاء ثلاثة ملفات:\n" +
                "- " + Path.GetFileName(outputs[0]) + "\n" +
                "- " + Path.GetFileName(outputs[1]) + "\n" +
                "- " + Path.GetFileName(outputs[2]) + "\n\n" +
                "هل تريد بدء المعالجة؟";

            return MessageBox.Show(this, message, "Processing Summary", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes;
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

            if (IsProtectedOrTemporaryOutputDirectory(outputDir))
            {
                FailUI("اختر مجلد إخراج عادي مثل Desktop أو Videos. لا تستخدم مجلد النظام أو مجلد ForgeFlow المؤقت.");
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

        private static bool IsProtectedOrTemporaryOutputDirectory(string outputDir)
        {
            try
            {
                string full = Path.GetFullPath(outputDir);
                string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                string temp = Path.GetTempPath();
                string forgeFlowTemp = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ForgeFlow",
                    "TempProcessingData");

                return IsSameOrChildPath(full, windows) ||
                       IsSameOrChildPath(full, temp) ||
                       IsSameOrChildPath(full, forgeFlowTemp);
            }
            catch
            {
                return true;
            }
        }

        private static bool IsSameOrChildPath(string candidate, string parent)
        {
            if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(parent))
                return false;

            string fullCandidate = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string fullParent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return fullCandidate.Equals(fullParent, StringComparison.OrdinalIgnoreCase) ||
                   fullCandidate.StartsWith(fullParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                   fullCandidate.StartsWith(fullParent + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
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
                Path.Combine(outDir, $"{baseName}_TikTok60Ultra{ext}"),
                Path.Combine(outDir, $"{baseName}_TikTok30FPS{ext}")
            };
        }

        private static void CleanupOldUpdateInstallers()
        {
            try
            {
                const int installersToKeep = 2;
                string updateDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ForgeFlow",
                    "Updates");
                if (!Directory.Exists(updateDir))
                    return;

                var installers = Directory.GetFiles(updateDir, "ForgeFlow_Lite_Setup_*.exe")
                    .Select(path => new FileInfo(path))
                    .OrderByDescending(file => file.LastWriteTimeUtc)
                    .Skip(installersToKeep);

                foreach (var installer in installers)
                    TryDeleteFile(installer.FullName);
            }
            catch
            {
            }
        }

        private static void CleanupOldSupportReports()
        {
            try
            {
                const int reportsToKeep = 10;
                string logsDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ForgeFlow",
                    "Logs");
                if (!Directory.Exists(logsDir))
                    return;

                var oldReports = Directory.GetFiles(logsDir, "support-report-*.txt")
                    .Select(path => new FileInfo(path))
                    .OrderByDescending(file => file.LastWriteTimeUtc)
                    .Skip(reportsToKeep);

                foreach (FileInfo report in oldReports)
                    TryDeleteFile(report.FullName);
            }
            catch
            {
            }
        }

        private static void CleanupStaleProcessingSessions()
        {
            try
            {
                const int sessionsToKeep = 2;
                string tempRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ForgeFlow",
                    "TempProcessingData");
                if (!Directory.Exists(tempRoot))
                    return;

                var oldSessions = Directory.GetDirectories(tempRoot, "Session_*")
                    .Select(path => new DirectoryInfo(path))
                    .OrderByDescending(info => info.LastWriteTimeUtc)
                    .Skip(sessionsToKeep);

                foreach (DirectoryInfo session in oldSessions)
                    TryDeleteDirectory(session.FullName);
            }
            catch
            {
            }
        }

        private static void TryDeleteFile(string path)
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

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, true);
            }
            catch
            {
            }
        }

        private static void OpenFolder(string folder)
        {
            if (!Directory.Exists(folder))
                return;

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = folder,
                    UseShellExecute = true
                });
            }
            catch
            {
            }
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
            SaveLastInputDirectory(videoPath);
            AppendLog("Selected: " + Path.GetFileName(videoPath));
            AppendLog("Output: " + OutputTextBox.Text);
            e.Handled = true;
        }

        private async void SelectInputVideo(string videoPath)
        {
            InputTextBox.Text = videoPath;
            LoadInlineInputPreview(videoPath);
            if (ShouldReplaceOutputPath())
                OutputTextBox.Text = GetDefaultOutputPath(videoPath);
            else
                RefreshOutputPreview();

            SaveLastInputDirectory(videoPath);
            SaveLastOutputDirectory(OutputTextBox.Text);
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

        private void SaveLastInputDirectory(string filePath)
        {
            string? directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            {
                _settings.LastInputDirectory = directory;
                _settings.Save();
            }
        }

        private void SaveLastOutputDirectory(string filePath)
        {
            string? directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                _settings.LastOutputDirectory = directory;
                _settings.Save();
            }
        }

        private static string GetExistingDirectoryOrDefault(string? directory)
        {
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
                return directory;

            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            return Directory.Exists(desktop) ? desktop : AppContext.BaseDirectory;
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
                : GetExistingDirectoryOrDefault(_settings.LastOutputDirectory);
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
            string upscaleHint = GetSelectedUpscaleProfile().Engine == "none"
                ? " | quality boost off"
                : $" | boosted via {GetSelectedUpscaleProfile().DisplayName}";
            string motionHint = IsRifeEnabled() ? " | RIFE on" : " | RIFE off";
            string hardwareHint = " | " + GetEffectiveHardwareModeDisplayName();
            string styleHint = " | " + GetSelectedVisualStyleDisplayName();
            OutputPreviewTextBlock.Text =
                "Exports: " + Path.GetFileName(outputs[0]) + " + " + Path.GetFileName(outputs[1]) + " + " + Path.GetFileName(outputs[2]) + sizeHint + upscaleHint + motionHint + styleHint + hardwareHint;
            OutputPreviewTextBlock.Foreground = Brushes.LightGray;
        }

        private void LoadInlineInputPreview(string videoPath)
        {
            StopInlinePreview();
            InlinePreviewInputMedia.Source = new Uri(videoPath);
            InlinePreviewInputMedia.IsMuted = true;
            InlinePreviewInputPlaceholder.Visibility = Visibility.Collapsed;
            InlinePreviewInputStatusText.Text = Path.GetFileName(videoPath);

            InlinePreviewOutputMedia.Stop();
            InlinePreviewOutputMedia.Source = null;
            InlinePreviewOutputPlaceholder.Visibility = Visibility.Visible;
            InlinePreviewOutputStatusText.Text = "بانتظار المعالجة";
            InlinePreviewSeekBar.Value = 0;
            InlinePreviewSeekBar.IsEnabled = false;
            InlinePreviewTimeText.Text = "0:00 / 0:00";
        }

        private void LoadInlineOutputPreview(string? outputPath)
        {
            if (string.IsNullOrWhiteSpace(outputPath) || !File.Exists(outputPath))
                return;

            InlinePreviewOutputMedia.Stop();
            InlinePreviewOutputMedia.Source = new Uri(outputPath);
            InlinePreviewOutputMedia.IsMuted = _inlinePreviewMuted;
            InlinePreviewOutputPlaceholder.Visibility = Visibility.Collapsed;
            InlinePreviewOutputStatusText.Text = Path.GetFileName(outputPath);
        }

        private void InlinePreviewInputMedia_MediaOpened(object sender, RoutedEventArgs e)
        {
            InlinePreviewSeekBar.IsEnabled = InlinePreviewInputMedia.NaturalDuration.HasTimeSpan;
        }

        private void InlinePreviewOutputMedia_MediaOpened(object sender, RoutedEventArgs e)
        {
            InlinePreviewOutputPlaceholder.Visibility = Visibility.Collapsed;
        }

        private void InlinePreviewMedia_MediaEnded(object sender, RoutedEventArgs e)
        {
            StopInlinePreview();
        }

        private void InlinePreviewPlayButton_Click(object sender, RoutedEventArgs e)
        {
            if (_inlinePreviewPlaying)
            {
                InlinePreviewInputMedia.Pause();
                InlinePreviewOutputMedia.Pause();
                _inlinePreviewTimer.Stop();
                _inlinePreviewPlaying = false;
                InlinePreviewPlayButton.Content = "▶";
                return;
            }

            InlinePreviewInputMedia.Play();
            InlinePreviewOutputMedia.Play();
            _inlinePreviewTimer.Start();
            _inlinePreviewPlaying = true;
            InlinePreviewPlayButton.Content = "❚❚";
        }

        private void InlinePreviewStopButton_Click(object sender, RoutedEventArgs e) => StopInlinePreview();

        private void InlinePreviewMuteButton_Click(object sender, RoutedEventArgs e)
        {
            _inlinePreviewMuted = !_inlinePreviewMuted;
            InlinePreviewInputMedia.IsMuted = true;
            InlinePreviewOutputMedia.IsMuted = _inlinePreviewMuted;
            InlinePreviewMuteButton.Content = _inlinePreviewMuted ? "🔇" : "🔊";
        }

        private void InlinePreviewSeekBar_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _inlinePreviewSeeking = true;
        }

        private void InlinePreviewSeekBar_PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _inlinePreviewSeeking = false;
            if (!InlinePreviewInputMedia.NaturalDuration.HasTimeSpan)
                return;

            TimeSpan sourceDuration = InlinePreviewInputMedia.NaturalDuration.TimeSpan;
            TimeSpan position = TimeSpan.FromSeconds(sourceDuration.TotalSeconds * InlinePreviewSeekBar.Value / 100);
            InlinePreviewInputMedia.Position = position;
            if (InlinePreviewOutputMedia.NaturalDuration.HasTimeSpan)
            {
                TimeSpan outputDuration = InlinePreviewOutputMedia.NaturalDuration.TimeSpan;
                InlinePreviewOutputMedia.Position = TimeSpan.FromSeconds(Math.Min(position.TotalSeconds, outputDuration.TotalSeconds));
            }
        }

        private void RefreshInlinePreviewTimeline()
        {
            if (_inlinePreviewSeeking || !InlinePreviewInputMedia.NaturalDuration.HasTimeSpan)
                return;

            TimeSpan duration = InlinePreviewInputMedia.NaturalDuration.TimeSpan;
            if (duration.TotalSeconds <= 0)
                return;

            TimeSpan position = InlinePreviewInputMedia.Position;
            InlinePreviewSeekBar.Value = Math.Clamp(position.TotalSeconds / duration.TotalSeconds * 100, 0, 100);
            InlinePreviewTimeText.Text = $"{FormatPreviewTime(position)} / {FormatPreviewTime(duration)}";

            if (InlinePreviewOutputMedia.NaturalDuration.HasTimeSpan)
            {
                TimeSpan outputDuration = InlinePreviewOutputMedia.NaturalDuration.TimeSpan;
                if (Math.Abs((InlinePreviewOutputMedia.Position - position).TotalMilliseconds) > 350)
                    InlinePreviewOutputMedia.Position = TimeSpan.FromSeconds(Math.Min(position.TotalSeconds, outputDuration.TotalSeconds));
            }
        }

        private void StopInlinePreview()
        {
            InlinePreviewInputMedia.Stop();
            InlinePreviewOutputMedia.Stop();
            _inlinePreviewTimer.Stop();
            _inlinePreviewPlaying = false;
            InlinePreviewPlayButton.Content = "▶";
            InlinePreviewSeekBar.Value = 0;
        }

        private static string FormatPreviewTime(TimeSpan time) => time.TotalHours >= 1
            ? time.ToString(@"h\:mm\:ss", System.Globalization.CultureInfo.InvariantCulture)
            : time.ToString(@"m\:ss", System.Globalization.CultureInfo.InvariantCulture);

        private void RefreshPresetInsight()
        {
            string preset = ((PresetComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Balanced").Trim();
            PresetInsightTextBlock.Text = preset switch
            {
                "Fast" => "Fast is best for demos and urgent delivery with smaller files.",
                "Quality" => "Quality is best for portfolio clips and client-facing masters.",
                "Aesthetic Vivid" => "Aesthetic Vivid targets TikTok-style saturated color, clean sharpness, and high bitrate.",
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
            if (msg.StartsWith("PROGRESS:", StringComparison.Ordinal))
            {
                if (int.TryParse(msg.Replace("PROGRESS:", "").Replace("%", "").Trim(), out int pct))
                {
                    ApplyProgressMood(pct);
                }
                return;
            }

            // ── Stage label update ────────────────────────────────────
            if (msg.StartsWith("STAGE:", StringComparison.Ordinal))
            {
                StageLabel.Text = msg.Replace("STAGE:", "").Trim();
                ApplyStageMood(StageLabel.Text);
                AppendVisibleLog(StageLabel.Text);
                return;
            }

            // ── stderr lines: only show errors ────────────────────────
            if (msg.StartsWith("[stderr]", StringComparison.Ordinal))
            {
                var inner = msg.Replace("[stderr]", "").Trim();
                bool isError = inner.StartsWith("Error", StringComparison.Ordinal) || inner.StartsWith("error", StringComparison.Ordinal)
                            || inner.Contains("No such file")
                            || inner.Contains("Invalid")
                            || inner.Contains("failed")
                            || inner.Contains("Cannot")
                            || inner.StartsWith("Warning", StringComparison.Ordinal);
                if (isError)
                    AppendVisibleLog("  " + inner);
                return;
            }

            // ── RIFE frame count ──────────────────────────────────────
            if (msg.StartsWith("RIFE frames:", StringComparison.Ordinal))
            {
                string clean = msg
                    .Replace("RIFE frames: input=", "Frames: ")
                    .Replace(", output=", " -> ");
                AppendVisibleLog(clean);
                return;
            }

            // ── Skip pre-filter done (already shown in stage) ─────────
            if (msg == "Pre-filter done.") return;

            // ── Everything else ───────────────────────────────────────
            if (IsImportantLogMessage(msg))
            {
                AppendVisibleLog(msg);
            }
        }

        private void AppendVisibleLog(string message)
        {
            const int maxVisibleLogLines = 500;
            LogsTextBox.AppendText(message + Environment.NewLine);
            TrimVisibleLog(maxVisibleLogLines);
            LogsTextBox.ScrollToEnd();
        }

        private void TrimVisibleLog(int maxLines)
        {
            if (LogsTextBox.LineCount <= maxLines)
                return;

            int lineIndexToKeep = Math.Max(0, LogsTextBox.LineCount - maxLines);
            int charIndex = LogsTextBox.GetCharacterIndexFromLineIndex(lineIndexToKeep);
            if (charIndex <= 0)
                return;

            LogsTextBox.Select(0, charIndex);
            LogsTextBox.SelectedText = "";
            LogsTextBox.CaretIndex = LogsTextBox.Text.Length;
        }

        private string WriteSupportReport()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ForgeFlow",
                "Logs");
            Directory.CreateDirectory(dir);

            string path = Path.Combine(dir, $"support-report-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            string input = InputTextBox.Text.Trim();
            string output = OutputTextBox.Text.Trim();
            string preset = ((PresetComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Balanced").Trim();

            string report =
                "ForgeFlow Support Report\r\n" +
                "========================\r\n" +
                $"Created: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\r\n" +
                $"Version: {GetAppVersion()}\r\n" +
                $"OS: {Environment.OSVersion}\r\n" +
                $"Machine: {Environment.MachineName}\r\n" +
                $"User: {Environment.UserName}\r\n" +
                $"License: {LicenseStatusTextBlock.Text}\r\n" +
                $"Preset: {preset}\r\n" +
                $"Hardware: {GetEffectiveHardwareModeDisplayName()}\r\n" +
                $"Motion: {GetSelectedMotionDisplayName()}\r\n" +
                $"Visual style: {GetSelectedVisualStyleDisplayName()} ({GetSelectedVisualStyleStrengthPercent()}%)\r\n" +
                $"Pipeline: {GetSelectedProcessingOrderDisplayName()}\r\n" +
                $"Quality boost: {GetSelectedUpscaleProfile().DisplayName}\r\n" +
                $"Input: {input}\r\n" +
                $"Output: {output}\r\n" +
                $"Video info: {_selectedVideoInfo?.Summary ?? "not loaded"}\r\n" +
                $"Tools: {_toolStatus?.Summary ?? "not checked"}\r\n\r\n" +
                "Log\r\n" +
                "---\r\n" +
                LogsTextBox.Text;

            File.WriteAllText(path, report);
            CleanupOldSupportReports();
            return path;
        }

        private void PlayCompletionSound(bool success)
        {
            if (CompletionSoundCheckBox.IsChecked != true)
                return;

            try
            {
                if (success)
                    SystemSounds.Asterisk.Play();
                else
                    SystemSounds.Exclamation.Play();
            }
            catch
            {
            }
        }

        private static string BuildFriendlyProcessingError(string standardError, string standardOutput)
        {
            string raw = string.IsNullOrWhiteSpace(standardError) ? standardOutput : standardError;
            if (string.IsNullOrWhiteSpace(raw))
                return "فشلت المعالجة بدون رسالة خطأ واضحة. جرّب ملفًا آخر أو افحص الأدوات من زر Test Tools.";

            string lower = raw.ToLowerInvariant();
            string hint = "";
            if (lower.Contains("no such file") || lower.Contains("cannot find") || lower.Contains("not found"))
                hint = "يبدو أن ملفًا مطلوبًا غير موجود. تحقق من مسار الإدخال والأدوات.";
            else if (lower.Contains("invalid data") || lower.Contains("moov atom") || lower.Contains("could not find codec"))
                hint = "يبدو أن الفيديو تالف أو غير مدعوم بالكامل. جرّب تحويله إلى MP4 ثم أعد المحاولة.";
            else if (lower.Contains("permission") || lower.Contains("access is denied") || lower.Contains("denied"))
                hint = "يبدو أن ويندوز منع الوصول إلى ملف أو مجلد. اختر مجلد إخراج آخر وأغلق أي مشغل فيديو يستخدم الملف.";
            else if (lower.Contains("no space") || lower.Contains("not enough space") || lower.Contains("disk full"))
                hint = "المساحة غير كافية. حرر مساحة في مجلد الإخراج والمجلد المؤقت.";
            else if (lower.Contains("vulkan") || lower.Contains("gpu") || lower.Contains("device"))
                hint = "قد تكون المشكلة من تعريف كرت الشاشة أو Vulkan. حدّث تعريف GPU ثم جرّب مرة أخرى.";
            else if (lower.Contains("vkallocatememory") || lower.Contains("vkqueuesubmit") || lower.Contains("vkwaitforfences"))
                hint = "نفدت ذاكرة كرت الشاشة أثناء المعالجة. جرّب RIFE ثم Upscale بدل Upscale ثم RIFE، أو استخدم موديل أخف، أو عطّل التسريع العتادي في الترميز.";

            return string.IsNullOrWhiteSpace(hint)
                ? raw.Trim()
                : hint + "\n\nTechnical details:\n" + raw.Trim();
        }

        private static bool IsImportantLogMessage(string msg)
        {
            if (string.IsNullOrWhiteSpace(msg))
                return false;

            return msg == "Started"
                || msg == "Done."
                || msg.StartsWith("Input:", StringComparison.Ordinal)
                || msg.StartsWith("Output:", StringComparison.Ordinal)
                || msg.StartsWith("Output folder:", StringComparison.Ordinal)
                || msg.StartsWith("Hardware:", StringComparison.Ordinal)
                || msg.StartsWith("Motion:", StringComparison.Ordinal)
                || msg.StartsWith("Pipeline:", StringComparison.Ordinal)
                || msg.StartsWith("Quality boost:", StringComparison.Ordinal)
                || msg.StartsWith("Upscale model:", StringComparison.Ordinal)
                || msg.StartsWith("Upscale acceleration:", StringComparison.Ordinal)
                || msg.StartsWith("Upscale speed:", StringComparison.Ordinal)
                || msg.StartsWith("Upscale:", StringComparison.Ordinal)
                || msg.StartsWith("NVDEC", StringComparison.Ordinal)
                || msg.StartsWith("Selected:", StringComparison.Ordinal)
                || msg.StartsWith("Video 60FPS:", StringComparison.Ordinal)
                || msg.StartsWith("TikTok60Ultra:", StringComparison.Ordinal)
                || msg.StartsWith("TikTok30FPS:", StringComparison.Ordinal)
                || msg.StartsWith("ERROR:", StringComparison.Ordinal)
                || msg.StartsWith("WARNING:", StringComparison.Ordinal)
                || msg.StartsWith("Unsupported file", StringComparison.Ordinal)
                || msg.StartsWith("Canceled", StringComparison.Ordinal)
                || msg.StartsWith("Canceling", StringComparison.Ordinal)
                || msg.StartsWith("CRITICAL:", StringComparison.Ordinal);
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
            PlayCompletionSound(success: false);
            MessageBox.Show(msg, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        protected override void OnClosed(EventArgs e)
        {
            _runnerTimer?.Stop();
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
