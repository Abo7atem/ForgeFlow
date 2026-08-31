using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace ForgeFlow
{
    internal sealed class LiveEditorWindow : Window
    {
        private static readonly SolidColorBrush Panel = new(Color.FromRgb(31, 31, 34));
        private static readonly SolidColorBrush PanelDark = new(Color.FromRgb(20, 20, 22));
        private static readonly SolidColorBrush Accent = new(Color.FromRgb(0, 145, 210));
        private readonly string _sourcePath;
        private readonly VideoEditOptions _options;
        private readonly EditTimelineModel _model = new();
        private readonly MediaElement _media = new() { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Stop, Stretch = Stretch.Fill, ScrubbingEnabled = true, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        private readonly Dictionary<Guid, MediaElement> _audioPlayers = new();
        private readonly Dictionary<Guid, string> _loadedAudioSources = new();
        private readonly Canvas _videoCanvas = new() { ClipToBounds = false, IsHitTestVisible = false };
        private readonly Canvas _overlayCanvas = new() { Background = Brushes.Transparent, ClipToBounds = true };
        private readonly Canvas _tracksCanvas = new() { Background = PanelDark, ClipToBounds = true, MinHeight = 290 };
        private readonly ScrollViewer _timelineScroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, CanContentScroll = false };
        private readonly ScrollViewer _trackLabelsScroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, CanContentScroll = false };
        private readonly Grid _trackLabels = new() { Background = new SolidColorBrush(Color.FromRgb(37, 37, 41)) };
        private readonly List<string> _visibleTracks = new();
        private readonly Grid _monitorHost = new() { Background = new SolidColorBrush(Color.FromRgb(14, 14, 16)), ClipToBounds = true };
        private readonly Grid _programFrame = new() { Background = Brushes.Black, ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        private readonly Slider _previewZoom = new() { Minimum = 45, Maximum = 100, Value = 96, Width = 120, IsMoveToPointEnabled = true };
        private readonly ComboBox _canvasPreset = new() { Width = 140, Margin = new Thickness(6, 0, 6, 0) };
        private readonly TextBlock _projectInfo = new() { Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
        private readonly StackPanel _mediaBin = new();
        private readonly HashSet<string> _projectMedia = new(StringComparer.OrdinalIgnoreCase);
        private readonly Slider _timeline = new() { Minimum = 0, IsMoveToPointEnabled = true };
        private readonly Slider _zoom = new() { Minimum = 35, Maximum = 900, Value = 85, Width = 170, IsMoveToPointEnabled = true };
        private readonly Slider _trackHeight = new() { Minimum = 46, Maximum = 96, Value = 62, Width = 88, IsMoveToPointEnabled = true };
        private readonly ComboBox _frameRatePreset = new() { Width = 122, Margin = new Thickness(6, 0, 8, 0) };
        private readonly TextBlock _sequenceBadge = new() { Foreground = Brushes.LightSkyBlue, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
        private readonly TextBlock _time = new() { Foreground = Brushes.DeepSkyBlue, FontFamily = new FontFamily("Consolas"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(10, 0, 10, 0) };
        private readonly TextBlock _status = new() { Foreground = Brushes.LightGray, VerticalAlignment = VerticalAlignment.Center };
        private readonly StackPanel _properties = new() { Margin = new Thickness(12) };
        private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(40) };
        private readonly Dictionary<Guid, FrameworkElement> _overlayElements = new();
        private readonly CheckBox _linked = new() { Content = "ربط V1 + A1", IsChecked = true, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 8, 0) };
        private bool _playing;
        private bool _scrubbing;
        private bool _dragging;
        private Guid? _dragClipId;
        private Point _dragOffset;
        private Point _overlayGestureStart;
        private double _overlayOriginX;
        private double _overlayOriginY;
        private double _overlayOriginWidth;
        private double _overlayOriginHeight;
        private double _overlayOriginFontSize;
        private enum OverlayGesture { Move, UniformScale, WidthScale, HeightScale }
        private OverlayGesture _overlayGesture;
        private TrimContext? _trimContext;
        private MoveContext? _moveContext;
        private readonly HashSet<string> _lockedTracks = new();
        private readonly HashSet<string> _hiddenTracks = new();
        private readonly HashSet<string> _mutedAudioTracks = new();
        private bool _audioMuted;
        private int _sourceWidth = 1920;
        private int _sourceHeight = 1080;
        private int _activeVideoWidth = 1920;
        private int _activeVideoHeight = 1080;
        private int _projectWidth = 1920;
        private int _projectHeight = 1080;
        private double _sourceFrameRate = 30;
        private double _projectFrameRate = 30;
        private double _videoScaleX = 1;
        private double _videoScaleY = 1;
        private double _videoOffsetX;
        private double _videoOffsetY;
        private Guid? _activeVideoTransformId;
        private bool _draggingVideo;
        private Point _videoDragStart;
        private double _videoDragOriginX;
        private double _videoDragOriginY;
        private bool _lensEnabled;
        private double _lensX = 0.5;
        private double _lensY = 0.5;
        private double _lensZoom = 4;
        private double _lensSize = 0.28;
        private Border? _lensVisual;
        private Guid? _renderedLensClipId;
        private bool _draggingLens;
        private bool _resizingLens;
        private Point _lensGestureStart;
        private double _lensGestureOriginSize;
        private enum EditorTool { Selection, Hand }
        private EditorTool _activeTool = EditorTool.Selection;
        private readonly Dictionary<EditorTool, Button> _toolButtons = new();
        private readonly List<double> _markers = new();
        private bool _snappingEnabled = true;
        private bool _handPanning;
        private Point _handPanStart;
        private double _handPanOrigin;
        private double _playbackRate = 1;
        private Button? _snapButton;
        private bool _initialMediaLoaded;
        private string? _loadedVideoSource;
        private double? _pendingVideoPosition;
        private bool _resumeAfterVideoLoad;
        private bool _handlingPlaybackBoundary;

        private sealed class TrimContext
        {
            public required Guid ClipId { get; init; }
            public required bool StartEdge { get; init; }
            public required Border Visual { get; init; }
            public required double OriginalStart { get; init; }
            public required double OriginalEnd { get; init; }
            public double Pixels { get; set; }
        }

        private sealed class MoveContext
        {
            public required Guid ClipId { get; init; }
            public required Border Visual { get; init; }
            public required double OriginalStart { get; init; }
            public required double OriginalEnd { get; init; }
            public double Pixels { get; set; }
        }

        public LiveEditorWindow(string path, VideoEditOptions options)
        {
            _sourcePath = path;
            _options = options;
            _projectMedia.Add(System.IO.Path.GetFullPath(path));
            Title = "ForgeFlow Editor";
            Width = 1540; Height = 940; MinWidth = 1180; MinHeight = 760;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            WindowState = WindowState.Maximized;
            Background = PanelDark; Foreground = Brushes.White;
            Focusable = true; AllowDrop = true;
            NativeWindowTheme.Apply(this);
            Icon = new BitmapImage(new Uri("pack://application:,,,/Assets/ForgeFlow.ico"));
            Resources[typeof(Button)] = CreateEditorButtonStyle();
            Resources[typeof(TextBox)] = CreateEditorTextBoxStyle();
            Resources[typeof(ComboBox)] = CreateEditorComboBoxStyle();
            Resources[typeof(ComboBoxItem)] = CreateEditorComboBoxItemStyle();
            Resources[typeof(Slider)] = CreateEditorSliderStyle();
            Content = BuildLayout();

            _media.MediaOpened += MediaOpened;
            _media.MediaFailed += (_, args) => _status.Text = "تعذر فتح الفيديو في المعاينة: " + (args.ErrorException?.Message ?? "خطأ وسائط غير معروف");
            _media.MediaEnded += (_, _) =>
            {
                if (!_playing || _handlingPlaybackBoundary) return;
                HandlePlaybackBoundary();
            };
            Loaded += async (_, _) =>
            {
                string ffprobe = System.IO.Path.Combine(AppContext.BaseDirectory, "Tools", "ffprobe.exe");
                VideoInfo? info = await VideoInfoReader.ReadAsync(ffprobe, _sourcePath);
                if (info is { Fps: > 0 })
                {
                    _sourceFrameRate = info.Fps;
                    _projectFrameRate = NormalizeProjectFrameRate(info.Fps);
                    UpdateSequenceBadge();
                }
                _loadedVideoSource = System.IO.Path.GetFullPath(_sourcePath);
                _media.Source = new Uri(_loadedVideoSource, UriKind.Absolute);
                _media.Play();
            };
            _clock.Tick += (_, _) => Tick();
            _timeline.PreviewMouseLeftButtonDown += (_, _) => _scrubbing = true;
            _timeline.PreviewMouseLeftButtonUp += (_, _) => { _scrubbing = false; Seek(_timeline.Value); };
            _timeline.ValueChanged += (_, _) => { if (_scrubbing) Seek(_timeline.Value); UpdatePlayhead(); };
            _tracksCanvas.SizeChanged += (_, _) => RenderTimeline();
            _tracksCanvas.MouseLeftButtonDown += TrackSurfaceClick;
            _tracksCanvas.PreviewMouseMove += TrackSurfaceMove;
            _tracksCanvas.PreviewMouseLeftButtonUp += TrackSurfaceUp;
            _timelineScroll.PreviewMouseWheel += TimelineMouseWheel;
            _timelineScroll.ScrollChanged += (_, e) =>
            {
                if (Math.Abs(e.VerticalChange) > 0.01) _trackLabelsScroll.ScrollToVerticalOffset(e.VerticalOffset);
            };
            _zoom.ValueChanged += (_, _) =>
            {
                RenderTimeline();
                ScrollPlayheadIntoView();
                _status.Text = $"تكبير التايم لاين: {(int)_zoom.Value}px لكل ثانية";
            };
            _trackHeight.ValueChanged += (_, _) =>
            {
                RenderTimeline();
                _status.Text = $"ارتفاع المسارات: {(int)_trackHeight.Value}px";
            };
            _overlayCanvas.SizeChanged += (_, _) => PositionOverlays();
            _overlayCanvas.MouseLeftButtonDown += VideoSurfaceDown;
            _overlayCanvas.MouseMove += VideoSurfaceMove;
            _overlayCanvas.MouseLeftButtonUp += VideoSurfaceUp;
            _overlayCanvas.MouseWheel += VideoSurfaceWheel;
            _monitorHost.SizeChanged += (_, _) => UpdateProgramFrameSize();
            _previewZoom.ValueChanged += (_, _) => UpdateProgramFrameSize();
            DragOver += EditorDragOver;
            Drop += EditorDrop;
            PreviewKeyDown += HandleKeys;
            Closed += (_, _) =>
            {
                _clock.Stop(); _media.Stop();
                foreach (MediaElement player in _audioPlayers.Values) player.Stop();
            };
        }

        private UIElement BuildLayout()
        {
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(46) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(5) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(400) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(34) });
            root.Children.Add(BuildToolbar());
            UIElement workspace = BuildWorkspace(); Grid.SetRow(workspace, 1); root.Children.Add(workspace);
            var horizontalSplitter = new GridSplitter { Height = 5, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, Background = new SolidColorBrush(Color.FromRgb(50, 50, 54)), ResizeDirection = GridResizeDirection.Rows, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
            Grid.SetRow(horizontalSplitter, 2); root.Children.Add(horizontalSplitter);
            UIElement timeline = BuildTimeline(); Grid.SetRow(timeline, 3); root.Children.Add(timeline);
            var statusBar = new Border { Background = new SolidColorBrush(Color.FromRgb(25, 25, 28)), BorderBrush = Brushes.Black, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(12, 0, 12, 0), Child = _status };
            Grid.SetRow(statusBar, 4); root.Children.Add(statusBar);
            return root;
        }

        private UIElement BuildToolbar()
        {
            var dock = new DockPanel { Background = Panel, LastChildFill = false };
            var title = new TextBlock { Text = "ForgeFlow  •  تحرير", FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(14, 0, 18, 0), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(title, Dock.Left); dock.Children.Add(title);
            dock.Children.Add(new TextBlock
            {
                Text = "محرر الفيديو الاحترافي",
                Foreground = Brushes.Gray,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0)
            });
            _canvasPreset.Items.Clear();
            _canvasPreset.Items.Add(new ComboBoxItem { Content = "مقاس المصدر", Tag = "source" });
            _canvasPreset.Items.Add(new ComboBoxItem { Content = "طولي 9:16", Tag = "portrait" });
            _canvasPreset.Items.Add(new ComboBoxItem { Content = "عرضي 16:9", Tag = "landscape" });
            _canvasPreset.Items.Add(new ComboBoxItem { Content = "مربع 1:1", Tag = "square" });
            _canvasPreset.SelectedIndex = 0;
            _canvasPreset.SelectionChanged += (_, _) =>
            {
                ApplyCanvasPreset();
                if (_canvasPreset.IsDropDownOpen) _canvasPreset.IsDropDownOpen = false;
            };
            _frameRatePreset.Items.Add(new ComboBoxItem { Content = "مطابقة المصدر", Tag = "source" });
            _frameRatePreset.Items.Add(new ComboBoxItem { Content = "30 FPS", Tag = "30" });
            _frameRatePreset.Items.Add(new ComboBoxItem { Content = "60 FPS", Tag = "60" });
            _frameRatePreset.SelectedIndex = 0;
            _frameRatePreset.SelectionChanged += (_, _) =>
            {
                if (_frameRatePreset.SelectedItem is ComboBoxItem item)
                {
                    string value = item.Tag?.ToString() ?? "source";
                    _projectFrameRate = value == "30" ? 30 : value == "60" ? 60 : NormalizeProjectFrameRate(_sourceFrameRate);
                    UpdateSequenceBadge();
                    _status.Text = $"معدل إطارات المشروع: {_projectFrameRate:0.##} FPS";
                }
            };
            var export = ToolButton("تصدير المشروع  MP4", ExportClicked);
            export.Background = new SolidColorBrush(Color.FromRgb(225, 105, 0)); export.Foreground = Brushes.White;
            export.MinWidth = 170; export.Height = 36; export.FontWeight = FontWeights.SemiBold;
            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            actions.Children.Add(new TextBlock { Text = "مقاس المشروع", Foreground = Brushes.LightGray, VerticalAlignment = VerticalAlignment.Center });
            actions.Children.Add(_canvasPreset);
            actions.Children.Add(new TextBlock { Text = "الإطارات", Foreground = Brushes.LightGray, VerticalAlignment = VerticalAlignment.Center });
            actions.Children.Add(_frameRatePreset);
            actions.Children.Add(_sequenceBadge);
            actions.Children.Add(export);
            DockPanel.SetDock(actions, Dock.Right); dock.Children.Add(actions);
            return dock;
        }

        private UIElement BuildWorkspace()
        {
            var work = new Grid { Margin = new Thickness(8) };
            work.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(225) });
            work.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
            work.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            work.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
            work.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(270) });

            var project = new StackPanel { Margin = new Thickness(10) };
            project.Children.Add(PanelTitle("Project / الوسائط"));
            Button importMedia = ToolButton("＋ استيراد وسائط", ImportMediaClicked);
            importMedia.Margin = new Thickness(0, 10, 0, 8);
            importMedia.HorizontalAlignment = HorizontalAlignment.Stretch;
            project.Children.Add(importMedia);
            project.Children.Add(new ScrollViewer
            {
                Content = _mediaBin, MinHeight = 110, MaxHeight = 260,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            });
            project.Children.Add(_projectInfo);
            project.Children.Add(new Border
            {
                Margin = new Thickness(0, 14, 0, 0), Padding = new Thickness(10),
                BorderBrush = new SolidColorBrush(Color.FromRgb(70, 70, 76)), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Child = new TextBlock { Text = "اسحب فيديو أو صوتًا أو صورة إلى التايم لاين. الفيديو يُدرج على V1 مع صوته على A1 ويزيح المقاطع التالية تلقائيًا.", Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap }
            });
            var projectBorder = new Border { Background = Panel, Child = project };
            work.Children.Add(projectBorder);

            var leftSplitter = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Stretch, Background = new SolidColorBrush(Color.FromRgb(50, 50, 54)), ResizeDirection = GridResizeDirection.Columns, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
            Grid.SetColumn(leftSplitter, 1); work.Children.Add(leftSplitter);

            _programFrame.Children.Clear();
            _videoCanvas.Children.Clear();
            _videoCanvas.Children.Add(_media);
            _programFrame.Children.Add(_videoCanvas);
            _programFrame.Children.Add(_overlayCanvas);
            _monitorHost.Children.Clear();
            _monitorHost.Children.Add(_programFrame);
            var previewControls = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            previewControls.Children.Add(new TextBlock { Text = "حجم شاشة المعاينة", Foreground = Brushes.LightGray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            previewControls.Children.Add(new TextBlock { Text = "−", Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center });
            previewControls.Children.Add(_previewZoom);
            previewControls.Children.Add(new TextBlock { Text = "+", Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center });
            var monitorPanel = new Grid();
            monitorPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            monitorPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(34) });
            monitorPanel.Children.Add(_monitorHost);
            Grid.SetRow(previewControls, 1); monitorPanel.Children.Add(previewControls);
            var monitorBorder = new Border { Background = PanelDark, BorderBrush = new SolidColorBrush(Color.FromRgb(58, 58, 62)), BorderThickness = new Thickness(1), Child = monitorPanel };
            Grid.SetColumn(monitorBorder, 2); work.Children.Add(monitorBorder);

            var rightSplitter = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Stretch, Background = new SolidColorBrush(Color.FromRgb(50, 50, 54)), ResizeDirection = GridResizeDirection.Columns, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
            Grid.SetColumn(rightSplitter, 3); work.Children.Add(rightSplitter);
            var propertiesBorder = new Border { Background = Panel, Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _properties } };
            Grid.SetColumn(propertiesBorder, 4); work.Children.Add(propertiesBorder);
            ShowProperties();
            RefreshMediaBin();
            return work;
        }

        private UIElement BuildTimeline()
        {
            var root = new Grid { Background = Panel, Margin = new Thickness(8, 0, 8, 8) };
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(38) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var commandArea = new DockPanel { Margin = new Thickness(6, 4, 6, 4), LastChildFill = true };
            var editTools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            editTools.Children.Add(ToolModeButton(EditorTool.Selection, "➤", "أداة التحديد (V)"));
            editTools.Children.Add(CompactToolButton("✂ C", "قص المقطع المحدد عند رأس التشغيل (C)", (_, _) => Split()));
            editTools.Children.Add(ToolModeButton(EditorTool.Hand, "✋", "أداة اليد (H) — اسحب التايم لاين أفقيًا"));
            editTools.Children.Add(TimelineDivider());
            editTools.Children.Add(CompactToolButton("↶", "تراجع (Ctrl+Z)", (_, _) => { if (_model.Undo()) RefreshEditor(); }));
            editTools.Children.Add(CompactToolButton("↷", "إعادة (Ctrl+Shift+Z)", (_, _) => { if (_model.Redo()) RefreshEditor(); }));
            editTools.Children.Add(CompactToolButton("Q◀", "Ripple Trim إلى رأس التشغيل (Q)", (_, _) => RippleTrim(true)));
            editTools.Children.Add(CompactToolButton("▶W", "Ripple Trim بعد رأس التشغيل (W)", (_, _) => RippleTrim(false)));
            editTools.Children.Add(CompactToolButton("⌫", "حذف المقطع وإغلاق الفراغ (Delete)", (_, _) => DeleteSelected()));
            _snapButton = CompactToolButton("🧲 S", "تشغيل/إيقاف الجذب (S)", (_, _) =>
            {
                _snappingEnabled = !_snappingEnabled;
                if (_snapButton != null) UpdateToggleButton(_snapButton, _snappingEnabled);
                _status.Text = _snappingEnabled ? "الجذب إلى الحواف ورأس التشغيل مفعّل." : "الجذب متوقف.";
            });
            if (_snapButton != null) UpdateToggleButton(_snapButton, true);
            editTools.Children.Add(_snapButton);
            editTools.Children.Add(CompactToolButton("◆ M", "إضافة علامة (M)", (_, _) => AddMarker()));
            editTools.Children.Add(CompactToolButton("⌨", "دليل اختصارات المونتاج", (_, _) => ShowShortcutGuide()));
            editTools.Children.Add(CompactToolButton("🔎 عدسة", "إضافة عدسة زمنية إلى L1", (_, _) => AddLensEffect()));
            editTools.Children.Add(CompactToolButton("◉ Blur", "إضافة Gaussian Blur زمني", (_, _) => AddBlurEffect()));
            Button linkButton = null!;
            linkButton = CompactToolButton("⛓", "ربط/فصل V1 مع A1", (_, _) =>
            {
                _linked.IsChecked = _linked.IsChecked != true;
                UpdateToggleButton(linkButton, _linked.IsChecked == true);
                _status.Text = _linked.IsChecked == true ? "تم ربط الفيديو بصوته الأصلي V1 + A1." : "تم فصل A1 عن V1 ويمكن قص الصوت أو تحريكه مستقلًا.";
            });
            UpdateToggleButton(linkButton, true);
            editTools.Children.Add(linkButton);
            DockPanel.SetDock(editTools, Dock.Left); commandArea.Children.Add(editTools);

            var transport = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            transport.Children.Add(CompactToolButton("|◀", "الانتقال إلى القطع السابق (Up)", (_, _) => GoToEdit(false)));
            transport.Children.Add(CompactToolButton("◀", "إطار للخلف (Left)", (_, _) => Seek(_timeline.Value - 1.0 / 30)));
            transport.Children.Add(CompactToolButton("▶", "تشغيل/إيقاف (Space)", (_, _) => Toggle()));
            transport.Children.Add(CompactToolButton("▶|", "الانتقال إلى القطع التالي (Down)", (_, _) => GoToEdit(true)));
            transport.Children.Add(TimelineDivider());
            transport.Children.Add(CompactToolButton("+ فيديو", "إدراج فيديو على V1/A1 عند رأس التشغيل", AddVideo));
            transport.Children.Add(CompactToolButton("+ صورة", "إضافة صورة أو شعار", AddLogo));
            transport.Children.Add(CompactToolButton("T نص", "إضافة طبقة نص", AddText));
            transport.Children.Add(CompactToolButton("♫ صوت", "إضافة مسار صوت", AddAudio));
            commandArea.Children.Add(transport);
            root.Children.Add(commandArea);

            var scrub = new Grid();
            scrub.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
            scrub.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var timePanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0) };
            timePanel.Children.Add(_time);
            scrub.Children.Add(timePanel);
            var scrubRight = new Grid();
            scrubRight.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            scrubRight.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _timeline.Margin = new Thickness(4, 0, 8, 0);
            scrubRight.Children.Add(_timeline);
            var scaleControls = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 8, 0) };
            scaleControls.Children.Add(new TextBlock { Text = "المقياس", Foreground = Brushes.Gray, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
            _zoom.Width = 112;
            scaleControls.Children.Add(_zoom);
            scaleControls.Children.Add(CompactToolButton("ملاءمة", "إظهار كامل المشروع داخل التايم لاين (\\)", (_, _) => FitTimeline()));
            scaleControls.Children.Add(new TextBlock { Text = "ارتفاع", Foreground = Brushes.Gray, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7, 0, 4, 0) });
            scaleControls.Children.Add(_trackHeight);
            Grid.SetColumn(scaleControls, 1); scrubRight.Children.Add(scaleControls);
            Grid.SetColumn(scrubRight, 1); scrub.Children.Add(scrubRight);
            Grid.SetRow(scrub, 1); root.Children.Add(scrub);

            var tracks = new Grid();
            tracks.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
            tracks.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _trackLabelsScroll.Content = _trackLabels;
            tracks.Children.Add(_trackLabelsScroll);
            _timelineScroll.Content = _tracksCanvas;
            Grid.SetColumn(_timelineScroll, 1); tracks.Children.Add(_timelineScroll);
            Grid.SetRow(tracks, 2); root.Children.Add(tracks);
            SetTool(EditorTool.Selection);
            return root;
        }

        private Button ToolModeButton(EditorTool tool, string glyph, string tooltip)
        {
            Button button = CompactToolButton(glyph, tooltip, (_, _) => SetTool(tool));
            button.MinWidth = 38;
            _toolButtons[tool] = button;
            return button;
        }

        private static Button CompactToolButton(string text, string tooltip, RoutedEventHandler handler)
        {
            Button button = ToolButton(text, handler);
            button.MinWidth = 34;
            button.Height = 32;
            button.Padding = new Thickness(8, 0, 8, 0);
            button.Margin = new Thickness(2, 0, 2, 0);
            button.ToolTip = tooltip;
            return button;
        }

        private static Border TimelineDivider() => new()
        {
            Width = 1, Height = 24, Margin = new Thickness(5, 0, 5, 0),
            Background = new SolidColorBrush(Color.FromRgb(70, 70, 75)),
            VerticalAlignment = VerticalAlignment.Center
        };

        private static void UpdateToggleButton(Button button, bool enabled)
        {
            button.Background = enabled
                ? new SolidColorBrush(Color.FromRgb(0, 112, 165))
                : new SolidColorBrush(Color.FromRgb(54, 54, 59));
            button.BorderBrush = enabled ? Brushes.DeepSkyBlue : new SolidColorBrush(Color.FromRgb(76, 76, 82));
        }

        private void SetTool(EditorTool tool)
        {
            _activeTool = tool;
            foreach ((EditorTool key, Button button) in _toolButtons)
                UpdateToggleButton(button, key == tool);
            _tracksCanvas.Cursor = tool switch
            {
                EditorTool.Hand => Cursors.Hand,
                _ => Cursors.Arrow
            };
            _status.Text = tool switch
            {
                EditorTool.Hand => "أداة اليد H: اسحب التايم لاين يمينًا ويسارًا.",
                _ => "أداة التحديد V: تحديد المقاطع وتحريكها وقص حوافها."
            };
        }

        private UIElement BuildTrackHeader(string track, string labelText)
        {
            var grid = new Grid { Margin = new Thickness(5, 2, 5, 2) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            var label = new TextBlock { Text = labelText, Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.White, FontWeight = FontWeights.SemiBold };
            grid.Children.Add(label);
            Button visibility = null!;
            bool audioTrack = track.StartsWith("A", StringComparison.Ordinal);
            visibility = ToolButton(audioTrack ? "🔊" : "◉", (_, _) =>
            {
                if (track == "A1")
                {
                    _audioMuted = !_audioMuted;
                    _model.SetAudioTrackMuted(track, _audioMuted);
                    _media.IsMuted = _audioMuted;
                    visibility.Content = _audioMuted ? "🔇" : "🔊";
                }
                else if (audioTrack)
                {
                    if (!_mutedAudioTracks.Add(track)) _mutedAudioTracks.Remove(track);
                    _model.SetAudioTrackMuted(track, _mutedAudioTracks.Contains(track));
                    visibility.Content = _mutedAudioTracks.Contains(track) ? "🔇" : "🔊";
                    SyncExtraAudio(_timeline.Value, _playing);
                }
                else
                {
                    if (!_hiddenTracks.Add(track)) _hiddenTracks.Remove(track);
                    visibility.Content = _hiddenTracks.Contains(track) ? "⊘" : "◉";
                    if (track == "V1") _media.Visibility = _hiddenTracks.Contains("V1") ? Visibility.Hidden : Visibility.Visible;
                    UpdateOverlayVisibility();
                }
            });
            visibility.MinWidth = 28; visibility.Width = 28; visibility.Padding = new Thickness(0); visibility.ToolTip = audioTrack ? "كتم المسار الصوتي" : "إظهار/إخفاء المسار";
            Grid.SetColumn(visibility, 1); grid.Children.Add(visibility);
            Button lockButton = null!;
            lockButton = ToolButton("🔓", (_, _) =>
            {
                if (!_lockedTracks.Add(track)) _lockedTracks.Remove(track);
                lockButton.Content = _lockedTracks.Contains(track) ? "🔒" : "🔓";
                _status.Text = _lockedTracks.Contains(track) ? $"تم قفل المسار {track}" : $"تم فتح المسار {track}";
            });
            lockButton.MinWidth = 28; lockButton.Width = 28; lockButton.Padding = new Thickness(0); lockButton.ToolTip = "قفل/فتح المسار";
            Grid.SetColumn(lockButton, 2); grid.Children.Add(lockButton);
            return grid;
        }

        private static Thumb CreateTrimThumb(Guid clipId, bool startEdge, bool selected) => new()
        {
            Tag = clipId,
            Width = 8,
            HorizontalAlignment = startEdge ? HorizontalAlignment.Left : HorizontalAlignment.Right,
            Cursor = Cursors.SizeWE,
            Background = selected ? Brushes.Orange : Brushes.Transparent,
            Template = CreateEditorThumbTemplate(2),
            ToolTip = startEdge ? "اسحب لقص بداية المقطع" : "اسحب لقص نهاية المقطع"
        };

        private static Thumb CreateMoveThumb(Guid clipId) => new()
        {
            Tag = clipId,
            Margin = new Thickness(8, 0, 8, 0),
            Cursor = Cursors.SizeAll,
            Background = Brushes.Transparent,
            Template = CreateEditorThumbTemplate(0),
            ToolTip = "اسحب المقطع لتحريكه على التايم لاين"
        };

        private void WireMoveThumb(Thumb thumb, Guid clipId, Border visual)
        {
            thumb.PreviewMouseLeftButtonDown += (_, e) =>
            {
                _model.Select(clipId);
                ShowProperties();
                HighlightSelectedOverlay();
                e.Handled = false;
            };
            thumb.DragStarted += (_, _) =>
            {
                EditTimelineModel.Clip? clip = _model.Clips.FirstOrDefault(c => c.Id == clipId);
                if (clip == null || _lockedTracks.Contains(clip.Track))
                {
                    _status.Text = clip == null ? "المقطع غير موجود." : $"المسار {clip.Track} مقفل.";
                    _moveContext = null;
                    return;
                }
                _model.Select(clipId);
                _moveContext = new MoveContext
                {
                    ClipId = clipId,
                    Visual = visual,
                    OriginalStart = clip.Start,
                    OriginalEnd = clip.End
                };
                visual.BorderBrush = Brushes.Orange;
                visual.BorderThickness = new Thickness(2);
            };
            thumb.DragDelta += (_, e) =>
            {
                if (_moveContext == null || _moveContext.ClipId != clipId) return;
                _moveContext.Pixels += e.HorizontalChange;
                PreviewMove();
            };
            thumb.DragCompleted += (_, _) => CompleteMove();
        }

        private void PreviewMove()
        {
            if (_moveContext == null || _model.Duration <= 0) return;
            double length = _moveContext.OriginalEnd - _moveContext.OriginalStart;
            double proposed = _moveContext.OriginalStart + PixelsToSeconds(_moveContext.Pixels);
            proposed = Math.Clamp(proposed, 0, Math.Max(0, _model.Duration - length));
            proposed = SnapTime(proposed, _moveContext.ClipId, length);
            Canvas.SetLeft(_moveContext.Visual, TimeToPixels(proposed));
            _status.Text = $"تحريك المقطع: {FormatTime(proposed)}  •  اترك Shift لإلغاء الجذب";
        }

        private void CompleteMove()
        {
            if (_moveContext == null) return;
            double length = _moveContext.OriginalEnd - _moveContext.OriginalStart;
            double proposed = _moveContext.OriginalStart + PixelsToSeconds(_moveContext.Pixels);
            proposed = Math.Clamp(proposed, 0, Math.Max(0, _model.Duration - length));
            proposed = SnapTime(proposed, _moveContext.ClipId, length);
            bool changed = _model.MoveSelected(proposed, _linked.IsChecked == true);
            _moveContext = null;
            if (changed) { RefreshEditor(); _status.Text = "تم تحريك المقطع. Ctrl+Z للتراجع."; }
            else RenderTimeline();
        }

        private double SnapTime(double proposedStart, Guid movingId, double length)
        {
            if (!_snappingEnabled || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return proposedStart;
            EditTimelineModel.Clip? moving = _model.Clips.FirstOrDefault(c => c.Id == movingId);
            if (moving == null) return proposedStart;
            double threshold = PixelsToSeconds(9);
            var candidates = new List<double> { 0, _model.Duration, _timeline.Value };
            candidates.AddRange(_model.Clips.Where(c => c.Id != movingId &&
                !(c.Kind is EditTimelineModel.ClipKind.Video or EditTimelineModel.ClipKind.Audio &&
                  Math.Abs(c.Start - moving.Start) < 0.001 && Math.Abs(c.End - moving.End) < 0.001))
                .SelectMany(c => new[] { c.Start, c.End }));
            double best = proposedStart;
            double bestDistance = threshold;
            foreach (double candidate in candidates)
            {
                double startDistance = Math.Abs(candidate - proposedStart);
                if (startDistance < bestDistance) { best = candidate; bestDistance = startDistance; }
                double endDistance = Math.Abs(candidate - (proposedStart + length));
                if (endDistance < bestDistance) { best = candidate - length; bestDistance = endDistance; }
            }
            return Math.Clamp(best, 0, Math.Max(0, _model.Duration - length));
        }

        private double PixelsToSeconds(double pixels) => pixels * _model.Duration / Math.Max(1, _tracksCanvas.Width);
        private double TimeToPixels(double seconds) => seconds / Math.Max(0.01, _model.Duration) * _tracksCanvas.Width;

        private void WireTrimThumb(Thumb thumb, Guid clipId, bool startEdge, Border visual)
        {
            thumb.DragStarted += (_, _) =>
            {
                EditTimelineModel.Clip? clip = _model.Clips.FirstOrDefault(c => c.Id == clipId);
                if (clip == null || _lockedTracks.Contains(clip.Track))
                {
                    _status.Text = clip == null ? "المقطع غير موجود." : $"المسار {clip.Track} مقفل.";
                    _trimContext = null; return;
                }
                _model.Select(clipId);
                _trimContext = new TrimContext { ClipId = clipId, StartEdge = startEdge, Visual = visual, OriginalStart = clip.Start, OriginalEnd = clip.End };
            };
            thumb.DragDelta += (_, e) =>
            {
                if (_trimContext == null || _trimContext.ClipId != clipId) return;
                _trimContext.Pixels += e.HorizontalChange;
                PreviewTrim();
            };
            thumb.DragCompleted += (_, _) => CompleteTrim();
        }

        private void PreviewTrim()
        {
            if (_trimContext == null || _model.Duration <= 0) return;
            double seconds = PixelsToSeconds(_trimContext.Pixels);
            double boundary = _trimContext.StartEdge
                ? Math.Clamp(_trimContext.OriginalStart + seconds, _trimContext.OriginalStart, _trimContext.OriginalEnd - 0.04)
                : Math.Clamp(_trimContext.OriginalEnd + seconds, _trimContext.OriginalStart + 0.04, _trimContext.OriginalEnd);
            double left = (_trimContext.StartEdge ? boundary : _trimContext.OriginalStart) / _model.Duration * _tracksCanvas.Width;
            double right = (_trimContext.StartEdge ? _trimContext.OriginalEnd : boundary) / _model.Duration * _tracksCanvas.Width;
            Canvas.SetLeft(_trimContext.Visual, left);
            _trimContext.Visual.Width = Math.Max(8, right - left);
            _status.Text = $"{(_trimContext.StartEdge ? "قص البداية" : "قص النهاية")}: {FormatTime(boundary)}";
        }

        private void CompleteTrim()
        {
            if (_trimContext == null) return;
            double seconds = PixelsToSeconds(_trimContext.Pixels);
            double boundary = _trimContext.StartEdge
                ? Math.Clamp(_trimContext.OriginalStart + seconds, _trimContext.OriginalStart, _trimContext.OriginalEnd - 0.04)
                : Math.Clamp(_trimContext.OriginalEnd + seconds, _trimContext.OriginalStart + 0.04, _trimContext.OriginalEnd);
            bool changed = _model.TrimSelected(boundary, _trimContext.StartEdge, _linked.IsChecked == true);
            _trimContext = null;
            if (changed) { RefreshEditor(); _status.Text = "تم قص طرف المقطع. Ctrl+Z للتراجع."; }
            else RenderTimeline();
        }

        private void MediaOpened(object? sender, RoutedEventArgs e)
        {
            if (!_media.NaturalDuration.HasTimeSpan) return;
            _media.Pause();
            _activeVideoWidth = Math.Max(1, _media.NaturalVideoWidth);
            _activeVideoHeight = Math.Max(1, _media.NaturalVideoHeight);
            UpdateVideoBaseSize();
            UpdateVideoTransform();
            if (_initialMediaLoaded)
            {
                if (_pendingVideoPosition is double pending)
                    _media.Position = TimeSpan.FromSeconds(Math.Max(0, pending));
                bool resume = _resumeAfterVideoLoad;
                _pendingVideoPosition = null;
                _resumeAfterVideoLoad = false;
                if (resume) _media.Play();
                return;
            }
            _initialMediaLoaded = true;
            _sourceWidth = _activeVideoWidth;
            _sourceHeight = _activeVideoHeight;
            ApplyCanvasPreset();
            _model.Load(_media.NaturalDuration.TimeSpan.TotalSeconds, _sourcePath);
            _timeline.Maximum = _model.Duration;
            _time.Text = FormatTime(0) + " / " + FormatTime(_model.Duration);
            _status.Text = "جاهز للتحرير — تم إنشاء مساري V1 و A1 ويمكن تقسيمهما معًا أو إلغاء الربط.";
            RefreshEditor();
            _media.Play(); _media.Pause();
        }

        private void RefreshEditor()
        {
            _timeline.Maximum = _model.Duration;
            if (_timeline.Value > _model.Duration) Seek(_model.Duration);
            _time.Text = FormatTime(Math.Min(_timeline.Value, _model.Duration)) + " / " + FormatTime(_model.Duration);
            HashSet<Guid> existingAudio = _model.Clips.Where(c => c.Kind == EditTimelineModel.ClipKind.Audio && c.Track != "A1").Select(c => c.Id).ToHashSet();
            foreach (Guid staleId in _audioPlayers.Keys.Where(id => !existingAudio.Contains(id)).ToList())
            {
                MediaElement player = _audioPlayers[staleId];
                player.Stop();
                _programFrame.Children.Remove(player);
                _audioPlayers.Remove(staleId);
                _loadedAudioSources.Remove(staleId);
            }
            RenderTimeline();
            RenderOverlays();
            ShowProperties();
            if (_initialMediaLoaded) Seek(Math.Min(_timeline.Value, _model.Duration));
        }

        private void ApplyCanvasPreset()
        {
            string preset = (_canvasPreset.SelectedItem as ComboBoxItem)?.Tag as string ?? "source";
            (_projectWidth, _projectHeight) = preset switch
            {
                "portrait" => (1080, 1920),
                "landscape" => (1920, 1080),
                "square" => (1080, 1080),
                _ => (_sourceWidth, _sourceHeight)
            };
            _media.Stretch = Stretch.Fill;
            _projectInfo.Text = $"المصدر: {_sourceWidth}×{_sourceHeight}\nالمشروع: {_projectWidth}×{_projectHeight}\nسيملأ الفيديو الإطار بالكامل بلا هوامش سوداء.";
            UpdateSequenceBadge();
            UpdateProgramFrameSize();
            PositionOverlays();
            _status.Text = $"مقاس المشروع: {_projectWidth}×{_projectHeight}";
        }

        private void UpdateProgramFrameSize()
        {
            double availableWidth = Math.Max(1, _monitorHost.ActualWidth - 18);
            double availableHeight = Math.Max(1, _monitorHost.ActualHeight - 18);
            double aspect = _projectWidth / (double)Math.Max(1, _projectHeight);
            double width = availableWidth;
            double height = width / aspect;
            if (height > availableHeight)
            {
                height = availableHeight;
                width = height * aspect;
            }
            double scale = _previewZoom.Value / 100.0;
            _programFrame.Width = Math.Max(40, width * scale);
            _programFrame.Height = Math.Max(40, height * scale);
            UpdateVideoBaseSize();
            UpdateVideoTransform();
        }

        private void UpdateVideoBaseSize()
        {
            double frameWidth = Math.Max(1, _programFrame.Width);
            double frameHeight = Math.Max(1, _programFrame.Height);
            double sourceAspect = _activeVideoWidth / (double)Math.Max(1, _activeVideoHeight);
            double frameAspect = frameWidth / frameHeight;
            if (sourceAspect >= frameAspect)
            {
                _media.Height = frameHeight;
                _media.Width = frameHeight * sourceAspect;
            }
            else
            {
                _media.Width = frameWidth;
                _media.Height = frameWidth / sourceAspect;
            }
            Canvas.SetLeft(_media, (frameWidth - _media.Width) / 2.0);
            Canvas.SetTop(_media, (frameHeight - _media.Height) / 2.0);
        }

        private void RenderTimeline()
        {
            if (_model.Duration <= 0) return;
            List<string> layout = GetTimelineTracks();
            if (!_visibleTracks.SequenceEqual(layout))
            {
                _visibleTracks.Clear();
                _visibleTracks.AddRange(layout);
                RebuildTrackHeaders();
            }
            double viewport = Math.Max(400, _timelineScroll.ViewportWidth);
            _tracksCanvas.Width = Math.Max(viewport, _model.Duration * _zoom.Value);
            _tracksCanvas.Children.Clear();
            double width = _tracksCanvas.Width;
            double rowHeight = _trackHeight.Value;
            double height = Math.Max(290, _visibleTracks.Count * rowHeight);
            _tracksCanvas.Height = height;
            _trackLabels.Height = height;
            for (int i = 1; i < _visibleTracks.Count; i++)
                _tracksCanvas.Children.Add(new Line { X1 = 0, X2 = width, Y1 = rowHeight * i, Y2 = rowHeight * i, Stroke = new SolidColorBrush(Color.FromRgb(52, 52, 56)), StrokeThickness = 1 });
            int rulerStep = _zoom.Value >= 150 ? 1 : _zoom.Value >= 75 ? 2 : 5;
            for (int second = 0; second <= _model.Duration; second += rulerStep)
            {
                double x = second / _model.Duration * width;
                _tracksCanvas.Children.Add(new Line { X1 = x, X2 = x, Y1 = 0, Y2 = 7, Stroke = Brushes.Gray, StrokeThickness = 1 });
                var ruler = new TextBlock { Text = TimeSpan.FromSeconds(second).ToString(@"m\:ss"), Foreground = Brushes.DimGray, FontSize = 10, IsHitTestVisible = false };
                Canvas.SetLeft(ruler, x + 3); Canvas.SetTop(ruler, 1); _tracksCanvas.Children.Add(ruler);
            }
            foreach (double markerTime in _markers.Where(value => value <= _model.Duration))
            {
                double markerX = markerTime / _model.Duration * width;
                var markerLine = new Line { X1 = markerX, X2 = markerX, Y1 = 0, Y2 = height, Stroke = new SolidColorBrush(Color.FromArgb(150, 255, 193, 7)), StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 4 }, IsHitTestVisible = false };
                _tracksCanvas.Children.Add(markerLine);
                var markerHead = new Polygon { Fill = Brushes.Gold, Points = new PointCollection { new(markerX - 5, 1), new(markerX + 5, 1), new(markerX, 10) }, IsHitTestVisible = false, ToolTip = FormatTime(markerTime) };
                _tracksCanvas.Children.Add(markerHead);
            }
            foreach (EditTimelineModel.Clip clip in _model.Clips)
            {
                int row = _visibleTracks.IndexOf(clip.Track);
                if (row < 0) continue;
                double left = clip.Start / _model.Duration * width;
                double clipWidth = Math.Max(8, (clip.End - clip.Start) / _model.Duration * width);
                var text = new TextBlock
                {
                    Text = clip.Kind switch
                    {
                        EditTimelineModel.ClipKind.Video => "🎬 " + System.IO.Path.GetFileName(clip.Source),
                        EditTimelineModel.ClipKind.Audio => clip.Track != "A1" ? "♫ " + System.IO.Path.GetFileName(clip.Source) : "▥▥▥  الصوت الأصلي",
                        EditTimelineModel.ClipKind.Image => "▣ " + System.IO.Path.GetFileName(clip.Source),
                        EditTimelineModel.ClipKind.Lens => $"🔎 عدسة {clip.LensZoom:0.0}×",
                        EditTimelineModel.ClipKind.Blur => $"◉ Gaussian Blur {clip.EffectStrength:0}",
                        _ => "T  " + clip.Text
                    },
                    TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Brushes.White,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0)
                };
                text.IsHitTestVisible = false;
                var clipGrid = new Grid { ClipToBounds = true };
                var border = new Border
                {
                    Tag = clip.Id, Child = clipGrid,
                    Width = clipWidth, Height = Math.Max(24, rowHeight - 8),
                    Background = ClipBrush(clip.Kind),
                    BorderBrush = clip.Id == _model.SelectedId ? Brushes.Orange : Brushes.DodgerBlue,
                    BorderThickness = new Thickness(clip.Id == _model.SelectedId ? 2 : 1),
                    CornerRadius = new CornerRadius(2),
                    Cursor = _activeTool == EditorTool.Hand ? Cursors.Hand : Cursors.SizeAll
                };
                var moveThumb = CreateMoveThumb(clip.Id);
                moveThumb.IsHitTestVisible = _activeTool == EditorTool.Selection;
                clipGrid.Children.Add(CreateClipTexture(clip.Kind, clipWidth, rowHeight - 8));
                clipGrid.Children.Add(moveThumb);
                clipGrid.Children.Add(text);
                var leftTrim = CreateTrimThumb(clip.Id, true, clip.Id == _model.SelectedId);
                var rightTrim = CreateTrimThumb(clip.Id, false, clip.Id == _model.SelectedId);
                leftTrim.IsHitTestVisible = rightTrim.IsHitTestVisible = _activeTool == EditorTool.Selection;
                leftTrim.HorizontalAlignment = HorizontalAlignment.Left;
                rightTrim.HorizontalAlignment = HorizontalAlignment.Right;
                clipGrid.Children.Add(leftTrim); clipGrid.Children.Add(rightTrim);
                WireTrimThumb(leftTrim, clip.Id, true, border);
                WireTrimThumb(rightTrim, clip.Id, false, border);
                WireMoveThumb(moveThumb, clip.Id, border);
                border.MouseLeftButtonDown += ClipClicked;
                Canvas.SetLeft(border, left); Canvas.SetTop(border, row * rowHeight + 4);
                _tracksCanvas.Children.Add(border);
            }
            UpdatePlayhead();
        }

        private static FrameworkElement CreateClipTexture(EditTimelineModel.ClipKind kind, double width, double height)
        {
            var canvas = new Canvas { Width = width, Height = height, IsHitTestVisible = false, Opacity = 0.38 };
            if (kind == EditTimelineModel.ClipKind.Audio)
            {
                double center = height * 0.58;
                int count = Math.Max(4, (int)(width / 7));
                for (int i = 0; i < count; i++)
                {
                    double x = i * 7 + 2;
                    double amplitude = 3 + Math.Abs(Math.Sin(i * 0.71) * Math.Cos(i * 0.19)) * Math.Max(5, height * 0.28);
                    canvas.Children.Add(new Line { X1 = x, X2 = x, Y1 = center - amplitude, Y2 = center + amplitude, Stroke = Brushes.LightCyan, StrokeThickness = 1 });
                }
            }
            else if (kind == EditTimelineModel.ClipKind.Video)
            {
                for (double x = 3; x < width; x += 22)
                    canvas.Children.Add(new Line { X1 = x, X2 = x, Y1 = 3, Y2 = height - 3, Stroke = new SolidColorBrush(Color.FromArgb(110, 255, 255, 255)), StrokeThickness = 1 });
            }
            return canvas;
        }

        private List<string> GetTimelineTracks()
        {
            IEnumerable<string> effects = _model.Clips.Where(c => c.Kind == EditTimelineModel.ClipKind.Blur).Select(c => c.Track).Distinct().OrderBy(TrackNumber);
            IEnumerable<string> lenses = _model.Clips.Where(c => c.Kind == EditTimelineModel.ClipKind.Lens).Select(c => c.Track).Distinct().OrderBy(TrackNumber);
            IEnumerable<string> texts = _model.Clips.Where(c => c.Kind == EditTimelineModel.ClipKind.Text).Select(c => c.Track).Distinct().OrderBy(TrackNumber);
            IEnumerable<string> images = _model.Clips.Where(c => c.Kind == EditTimelineModel.ClipKind.Image).Select(c => c.Track).Distinct().OrderBy(TrackNumber);
            IEnumerable<string> audio = _model.Clips.Where(c => c.Kind == EditTimelineModel.ClipKind.Audio && c.Track != "A1").Select(c => c.Track).Distinct().OrderBy(TrackNumber);
            return effects.Concat(lenses).Concat(texts).Concat(images).Concat(new[] { "V1", "A1" }).Concat(audio).ToList();
        }

        private void RebuildTrackHeaders()
        {
            _trackLabels.Children.Clear();
            _trackLabels.RowDefinitions.Clear();
            for (int i = 0; i < _visibleTracks.Count; i++)
            {
                _trackLabels.RowDefinitions.Add(new RowDefinition { Height = new GridLength(_trackHeight.Value) });
                string track = _visibleTracks[i];
                string label = track switch
                {
                    "V1" => "V1  الفيديو الرئيسي",
                    "A1" => "A1  صوت الفيديو",
                    _ when track.StartsWith("A", StringComparison.Ordinal) => track + "  صوت / موسيقى",
                    _ when track.StartsWith("I", StringComparison.Ordinal) => track + "  صورة / شعار",
                    _ when track.StartsWith("L", StringComparison.Ordinal) => track + "  عدسة تكبير",
                    _ when track.StartsWith("E", StringComparison.Ordinal) => track + "  مؤثر Blur",
                    _ => track + "  نص"
                };
                UIElement header = BuildTrackHeader(track, label);
                Grid.SetRow(header, i);
                _trackLabels.Children.Add(header);
            }
        }

        private static int TrackNumber(string track)
        {
            return track.Length > 1 && int.TryParse(track[1..], out int number) ? number : int.MaxValue;
        }

        private void UpdatePlayhead()
        {
            if (_tracksCanvas.ActualWidth <= 0 || _model.Duration <= 0) return;
            foreach (Line old in _tracksCanvas.Children.OfType<Line>().Where(l => Equals(l.Tag, "playhead")).ToList())
                _tracksCanvas.Children.Remove(old);
            double x = _timeline.Value / _model.Duration * _tracksCanvas.ActualWidth;
            var playhead = new Line { Tag = "playhead", X1 = x, X2 = x, Y1 = 0, Y2 = Math.Max(290, _tracksCanvas.ActualHeight), Stroke = Brushes.OrangeRed, StrokeThickness = 2, IsHitTestVisible = false };
            _tracksCanvas.Children.Add(playhead);
        }

        private void TrackSurfaceClick(object sender, MouseButtonEventArgs e)
        {
            if (_model.Duration <= 0 || _tracksCanvas.ActualWidth <= 0) return;
            if (_activeTool == EditorTool.Hand)
            {
                _handPanning = true;
                _handPanStart = e.GetPosition(this);
                _handPanOrigin = _timelineScroll.HorizontalOffset;
                _tracksCanvas.CaptureMouse();
                e.Handled = true;
                return;
            }
            double at = Math.Clamp(e.GetPosition(_tracksCanvas).X / _tracksCanvas.ActualWidth * _model.Duration, 0, _model.Duration);
            Seek(at); _model.Select(null); RenderTimeline(); ShowProperties(); Focus();
        }

        private void TrackSurfaceMove(object sender, MouseEventArgs e)
        {
            if (!_handPanning || e.LeftButton != MouseButtonState.Pressed) return;
            double delta = e.GetPosition(this).X - _handPanStart.X;
            _timelineScroll.ScrollToHorizontalOffset(Math.Max(0, _handPanOrigin - delta));
            e.Handled = true;
        }

        private void TrackSurfaceUp(object sender, MouseButtonEventArgs e)
        {
            if (!_handPanning) return;
            _handPanning = false;
            _tracksCanvas.ReleaseMouseCapture();
            e.Handled = true;
        }

        private void ClipClicked(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border { Tag: Guid id })
            {
                if (_activeTool == EditorTool.Hand)
                {
                    _handPanning = true;
                    _handPanStart = e.GetPosition(this);
                    _handPanOrigin = _timelineScroll.HorizontalOffset;
                    _tracksCanvas.CaptureMouse();
                    e.Handled = true;
                    return;
                }
                _model.Select(id);
                RenderTimeline(); ShowProperties(); HighlightSelectedOverlay();
                e.Handled = true; Focus();
            }
        }

        private void RenderOverlays()
        {
            _overlayCanvas.Children.Clear();
            _overlayElements.Clear();
            foreach (EditTimelineModel.Clip clip in _model.Clips.Where(c => c.Kind is EditTimelineModel.ClipKind.Image or EditTimelineModel.ClipKind.Text))
            {
                FrameworkElement content;
                if (clip.Kind == EditTimelineModel.ClipKind.Image)
                    content = new Image { Source = new BitmapImage(new Uri(clip.Source)), Stretch = Stretch.Fill, IsHitTestVisible = false };
                else
                    content = new TextBlock
                    {
                        Text = clip.Text,
                        FontSize = clip.FontSize,
                        FontFamily = new FontFamily(clip.FontFamily),
                        Foreground = ParseBrush(clip.Color),
                        TextWrapping = TextWrapping.NoWrap,
                        Background = Brushes.Transparent,
                        Padding = new Thickness(0),
                        ClipToBounds = false,
                        IsHitTestVisible = false
                    };
                var element = new Grid { Tag = clip.Id, Background = Brushes.Transparent, Cursor = Cursors.SizeAll };
                element.Children.Add(content);
                element.Tag = clip.Id;
                element.MouseLeftButtonDown += OverlayDown;
                element.MouseRightButtonDown += OverlayResizeDown;
                element.MouseMove += OverlayMove;
                element.MouseLeftButtonUp += OverlayUp;
                element.MouseRightButtonUp += OverlayUp;
                element.PreviewMouseWheel += OverlayWheel;
                _overlayElements[clip.Id] = element;
                _overlayCanvas.Children.Add(element);
            }
            PositionOverlays();
            UpdateOverlayVisibility();
            RenderLens();
        }

        private void PositionOverlays()
        {
            double w = _overlayCanvas.ActualWidth, h = _overlayCanvas.ActualHeight;
            if (w <= 0 || h <= 0) return;
            foreach (var pair in _overlayElements)
            {
                EditTimelineModel.Clip? clip = _model.Clips.FirstOrDefault(c => c.Id == pair.Key);
                if (clip == null) continue;
                FrameworkElement element = pair.Value;
                element.Width = Math.Max(28, clip.Width * w);
                element.Height = Math.Max(22, clip.Height * h);
                Canvas.SetLeft(element, clip.X * w); Canvas.SetTop(element, clip.Y * h);
                ApplySelectionBorder(element, clip.Id == _model.SelectedId);
            }
            UpdateVideoTransform();
            UpdateLensVisual();
        }

        private void VideoSurfaceDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource != _overlayCanvas || e.ChangedButton != MouseButton.Left) return;
            EditTimelineModel.Clip? video = _model.Clips.FirstOrDefault(c => c.Track == "V1" && _timeline.Value >= c.Start && _timeline.Value < c.End);
            if (video == null) return;
            _model.Select(video.Id);
            LoadVideoTransform(video);
            _draggingVideo = true;
            _videoDragStart = e.GetPosition(_overlayCanvas);
            _videoDragOriginX = _videoOffsetX;
            _videoDragOriginY = _videoOffsetY;
            _overlayCanvas.CaptureMouse();
            ShowProperties();
            _status.Text = "تحريك الفيديو داخل الإطار — استخدم عجلة الماوس للتكبير والتصغير.";
            e.Handled = true;
        }

        private void VideoSurfaceMove(object sender, MouseEventArgs e)
        {
            if (!_draggingVideo || e.LeftButton != MouseButtonState.Pressed) return;
            Point current = e.GetPosition(_overlayCanvas);
            double overflowX = Math.Max(1, (_media.Width * _videoScaleX - _programFrame.Width) / 2.0);
            double overflowY = Math.Max(1, (_media.Height * _videoScaleY - _programFrame.Height) / 2.0);
            _videoOffsetX = Math.Clamp(_videoDragOriginX + (current.X - _videoDragStart.X) / overflowX, -1, 1);
            _videoOffsetY = Math.Clamp(_videoDragOriginY + (current.Y - _videoDragStart.Y) / overflowY, -1, 1);
            UpdateVideoTransform();
        }

        private void VideoSurfaceUp(object sender, MouseButtonEventArgs e)
        {
            if (!_draggingVideo) return;
            _draggingVideo = false;
            _overlayCanvas.ReleaseMouseCapture();
            if (_activeVideoTransformId is Guid id)
                _model.UpdateVideoTransform(id, _videoScaleX, _videoScaleY, _videoOffsetX, _videoOffsetY);
            ShowProperties();
            e.Handled = true;
        }

        private void VideoSurfaceWheel(object sender, MouseWheelEventArgs e)
        {
            if (e.OriginalSource != _overlayCanvas) return;
            if (_model.SelectedClip is { Kind: EditTimelineModel.ClipKind.Blur } blur)
            {
                _model.BeginUndoGroup();
                double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? 3 : 1;
                _model.UpdateBlur(blur.Id, blur.EffectStrength + (e.Delta > 0 ? step : -step));
                ApplyTimedEffects();
                ShowProperties();
                RenderTimeline();
                _status.Text = $"قوة Gaussian Blur: {blur.EffectStrength:0}";
                e.Handled = true;
                return;
            }
            double factor = e.Delta > 0 ? 1.08 : 1 / 1.08;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) _videoScaleX = Math.Clamp(_videoScaleX * factor, 1, 4);
            else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) _videoScaleY = Math.Clamp(_videoScaleY * factor, 1, 4);
            else
            {
                _videoScaleX = Math.Clamp(_videoScaleX * factor, 1, 4);
                _videoScaleY = Math.Clamp(_videoScaleY * factor, 1, 4);
            }
            if (_activeVideoTransformId is Guid id)
                _model.UpdateVideoTransform(id, _videoScaleX, _videoScaleY, _videoOffsetX, _videoOffsetY);
            UpdateVideoTransform();
            ShowProperties();
            _status.Text = $"حجم الفيديو: {_videoScaleX:0.00}× عرض، {_videoScaleY:0.00}× طول — Shift للعرض وCtrl للطول.";
            e.Handled = true;
        }

        private void UpdateVideoTransform()
        {
            _media.RenderTransformOrigin = new Point(0.5, 0.5);
            var transforms = new TransformGroup();
            transforms.Children.Add(new ScaleTransform(_videoScaleX, _videoScaleY));
            double overflowX = Math.Max(0, _media.Width * _videoScaleX - Math.Max(1, _programFrame.Width));
            double overflowY = Math.Max(0, _media.Height * _videoScaleY - Math.Max(1, _programFrame.Height));
            transforms.Children.Add(new TranslateTransform(_videoOffsetX * overflowX / 2.0, _videoOffsetY * overflowY / 2.0));
            _media.RenderTransform = transforms;
        }

        private void LoadVideoTransform(EditTimelineModel.Clip clip)
        {
            _activeVideoTransformId = clip.Id;
            _videoScaleX = clip.HasVideoTransform ? clip.VideoScaleX : 1;
            _videoScaleY = clip.HasVideoTransform ? clip.VideoScaleY : 1;
            _videoOffsetX = clip.HasVideoTransform ? clip.VideoOffsetX : 0;
            _videoOffsetY = clip.HasVideoTransform ? clip.VideoOffsetY : 0;
            UpdateVideoTransform();
        }

        private void RenderLens()
        {
            if (_lensVisual != null) _overlayCanvas.Children.Remove(_lensVisual);
            _lensVisual = null;
            EditTimelineModel.Clip? clip = ActiveLensClip();
            _renderedLensClipId = clip?.Id;
            _lensEnabled = clip != null;
            if (clip == null || _overlayCanvas.ActualWidth <= 0 || _overlayCanvas.ActualHeight <= 0) return;
            _lensX = clip.X;
            _lensY = clip.Y;
            _lensSize = clip.Width;
            _lensZoom = clip.LensZoom;
            var brush = new VisualBrush(_media)
            {
                Stretch = Stretch.Fill,
                ViewboxUnits = BrushMappingMode.RelativeToBoundingBox,
                AlignmentX = AlignmentX.Center,
                AlignmentY = AlignmentY.Center
            };
            var lens = new Border
            {
                Background = brush,
                BorderBrush = new SolidColorBrush(Color.FromArgb(220, 235, 235, 235)),
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(999),
                Cursor = Cursors.SizeAll,
                ToolTip = "اسحب العدسة للتحريك • Shift+اسحب للتحجيم • العجلة للتكبير والتصغير"
            };
            lens.MouseLeftButtonDown += LensDown;
            lens.MouseMove += LensMove;
            lens.MouseLeftButtonUp += LensUp;
            lens.MouseWheel += LensWheel;
            _lensVisual = lens;
            _overlayCanvas.Children.Add(lens);
            UpdateLensVisual();
        }

        private EditTimelineModel.Clip? ActiveLensClip() => _model.Clips
            .Where(c => c.Kind == EditTimelineModel.ClipKind.Lens && _timeline.Value >= c.Start && _timeline.Value < c.End && !_hiddenTracks.Contains(c.Track))
            .OrderBy(c => c.Track)
            .LastOrDefault();

        private void UpdateLensVisual()
        {
            if (_lensVisual == null) return;
            double canvasWidth = Math.Max(1, _overlayCanvas.ActualWidth);
            double canvasHeight = Math.Max(1, _overlayCanvas.ActualHeight);
            double diameter = Math.Max(70, _lensSize * Math.Min(canvasWidth, canvasHeight));
            _lensVisual.Width = diameter;
            _lensVisual.Height = diameter;
            Canvas.SetLeft(_lensVisual, Math.Clamp(_lensX * canvasWidth - diameter / 2, 0, Math.Max(0, canvasWidth - diameter)));
            Canvas.SetTop(_lensVisual, Math.Clamp(_lensY * canvasHeight - diameter / 2, 0, Math.Max(0, canvasHeight - diameter)));
            if (_lensVisual.Background is VisualBrush brush)
            {
                double viewWidth = diameter / _lensZoom / canvasWidth;
                double viewHeight = diameter / _lensZoom / canvasHeight;
                brush.Viewbox = new Rect(
                    Math.Clamp(_lensX - viewWidth / 2, 0, 1 - viewWidth),
                    Math.Clamp(_lensY - viewHeight / 2, 0, 1 - viewHeight),
                    viewWidth, viewHeight);
            }
        }

        private void LensDown(object sender, MouseButtonEventArgs e)
        {
            if (_renderedLensClipId is Guid id)
            {
                _model.Select(id);
                _model.BeginUndoGroup();
                RenderTimeline();
                ShowProperties();
            }
            _draggingLens = true;
            _resizingLens = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            _lensGestureStart = e.GetPosition(_overlayCanvas);
            _lensGestureOriginSize = _lensSize;
            if (sender is UIElement element) element.CaptureMouse();
            e.Handled = true;
        }

        private void LensMove(object sender, MouseEventArgs e)
        {
            if (!_draggingLens || e.LeftButton != MouseButtonState.Pressed) return;
            Point p = e.GetPosition(_overlayCanvas);
            if (_resizingLens)
            {
                double delta = ((p.X - _lensGestureStart.X) + (p.Y - _lensGestureStart.Y)) / (2 * Math.Max(1, Math.Min(_overlayCanvas.ActualWidth, _overlayCanvas.ActualHeight)));
                _lensSize = Math.Clamp(_lensGestureOriginSize + delta, 0.08, 0.60);
            }
            else
            {
                _lensX = Math.Clamp(p.X / Math.Max(1, _overlayCanvas.ActualWidth), 0, 1);
                _lensY = Math.Clamp(p.Y / Math.Max(1, _overlayCanvas.ActualHeight), 0, 1);
            }
            if (_renderedLensClipId is Guid id)
                _model.UpdateLens(id, _lensX, _lensY, _lensSize, _lensZoom);
            UpdateLensVisual();
            e.Handled = true;
        }

        private void LensUp(object sender, MouseButtonEventArgs e)
        {
            _draggingLens = false;
            _resizingLens = false;
            if (sender is UIElement element) element.ReleaseMouseCapture();
            ShowProperties();
            e.Handled = true;
        }

        private void LensWheel(object sender, MouseWheelEventArgs e)
        {
            double factor = e.Delta > 0 ? 1.08 : 1 / 1.08;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                _lensZoom = Math.Clamp(_lensZoom * factor, 2, 6);
            else
                _lensSize = Math.Clamp(_lensSize * factor, 0.08, 0.60);
            if (_renderedLensClipId is Guid id)
                _model.UpdateLens(id, _lensX, _lensY, _lensSize, _lensZoom);
            UpdateLensVisual();
            ShowProperties();
            RenderTimeline();
            _status.Text = $"العدسة: {_lensZoom:0.0}× — الحجم {_lensSize * 100:0}% (Ctrl+العجلة لقوة التكبير).";
            e.Handled = true;
        }

        private void OverlayDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: Guid id } element) return;
            _model.Select(id); _model.BeginUndoGroup(); _dragClipId = id; _dragging = true;
            Point p = e.GetPosition(_overlayCanvas);
            _dragOffset = new Point(p.X - Canvas.GetLeft(element), p.Y - Canvas.GetTop(element));
            _overlayGestureStart = p;
            if (_model.Clips.FirstOrDefault(c => c.Id == id) is { } clip)
            {
                _overlayOriginX = clip.X;
                _overlayOriginY = clip.Y;
                _overlayOriginWidth = clip.Width;
                _overlayOriginHeight = clip.Height;
                _overlayOriginFontSize = clip.FontSize;
            }
            Point local = e.GetPosition(element);
            double edge = Math.Clamp(Math.Min(element.ActualWidth, element.ActualHeight) * 0.12, 12, 28);
            bool nearRight = local.X >= element.ActualWidth - edge;
            bool nearBottom = local.Y >= element.ActualHeight - edge;
            _overlayGesture = nearRight && nearBottom ? OverlayGesture.UniformScale
                : nearRight ? OverlayGesture.WidthScale
                : nearBottom ? OverlayGesture.HeightScale
                : Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? OverlayGesture.UniformScale
                : Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? OverlayGesture.WidthScale
                : Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) ? OverlayGesture.HeightScale
                : OverlayGesture.Move;
            element.CaptureMouse(); RenderTimeline(); ShowProperties(); HighlightSelectedOverlay();
            _status.Text = _overlayGesture switch
            {
                OverlayGesture.UniformScale => "تحجيم حر: اسحب من أي مكان داخل العنصر.",
                OverlayGesture.WidthScale => "توسيع العرض: Ctrl+سحب من أي مكان داخل العنصر.",
                OverlayGesture.HeightScale => "توسيع الطول: Alt+سحب من أي مكان داخل العنصر.",
                _ => "تحريك حر: اسحب العنصر من أي مكان. Shift+سحب للتحجيم."
            };
            e.Handled = true;
        }

        private void OverlayResizeDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: Guid id } element) return;
            _model.Select(id); _model.BeginUndoGroup(); _dragClipId = id; _dragging = true;
            Point p = e.GetPosition(_overlayCanvas);
            _overlayGestureStart = p;
            if (_model.Clips.FirstOrDefault(c => c.Id == id) is { } clip)
            {
                _overlayOriginX = clip.X;
                _overlayOriginY = clip.Y;
                _overlayOriginWidth = clip.Width;
                _overlayOriginHeight = clip.Height;
                _overlayOriginFontSize = clip.FontSize;
            }
            _overlayGesture = OverlayGesture.UniformScale;
            element.CaptureMouse(); RenderTimeline(); ShowProperties();
            _status.Text = "تكبير وتصغير بالزر الأيمن: اسحب من أي مكان داخل الصورة أو النص.";
            e.Handled = true;
        }

        private void OverlayMove(object sender, MouseEventArgs e)
        {
            if (sender is not FrameworkElement element) return;
            if (!_dragging || _dragClipId == null)
            {
                Point local = e.GetPosition(element);
                double edge = Math.Clamp(Math.Min(element.ActualWidth, element.ActualHeight) * 0.12, 12, 28);
                bool nearRight = local.X >= element.ActualWidth - edge;
                bool nearBottom = local.Y >= element.ActualHeight - edge;
                element.Cursor = nearRight && nearBottom ? Cursors.SizeNWSE
                    : nearRight ? Cursors.SizeWE
                    : nearBottom ? Cursors.SizeNS
                    : Cursors.SizeAll;
                return;
            }
            if (e.LeftButton != MouseButtonState.Pressed && e.RightButton != MouseButtonState.Pressed) return;
            Point p = e.GetPosition(_overlayCanvas);
            if (_model.Clips.FirstOrDefault(c => c.Id == _dragClipId) is { } clip)
            {
                double canvasWidth = Math.Max(1, _overlayCanvas.ActualWidth);
                double canvasHeight = Math.Max(1, _overlayCanvas.ActualHeight);
                if (_overlayGesture == OverlayGesture.Move)
                {
                    double left = Math.Clamp(p.X - _dragOffset.X, 0, Math.Max(0, canvasWidth - element.ActualWidth));
                    double top = Math.Clamp(p.Y - _dragOffset.Y, 0, Math.Max(0, canvasHeight - element.ActualHeight));
                    clip.X = left / canvasWidth;
                    clip.Y = top / canvasHeight;
                }
                else
                {
                    double dx = (p.X - _overlayGestureStart.X) / canvasWidth;
                    double dy = (p.Y - _overlayGestureStart.Y) / canvasHeight;
                    double newWidth = _overlayOriginWidth;
                    double newHeight = _overlayOriginHeight;
                    if (_overlayGesture == OverlayGesture.UniformScale)
                    {
                        double delta = (dx + dy) / 2.0;
                        double ratio = _overlayOriginHeight / Math.Max(0.001, _overlayOriginWidth);
                        newWidth = _overlayOriginWidth + delta;
                        newHeight = newWidth * ratio;
                    }
                    else if (_overlayGesture == OverlayGesture.WidthScale) newWidth = _overlayOriginWidth + dx;
                    else newHeight = _overlayOriginHeight + dy;
                    clip.Width = Math.Clamp(newWidth, 0.03, 1 - _overlayOriginX);
                    clip.Height = Math.Clamp(newHeight, 0.03, 1 - _overlayOriginY);
                    if (clip.Kind == EditTimelineModel.ClipKind.Text)
                    {
                        double scale = Math.Sqrt(Math.Max(0.01,
                            clip.Width / Math.Max(0.001, _overlayOriginWidth) *
                            clip.Height / Math.Max(0.001, _overlayOriginHeight)));
                        clip.FontSize = Math.Clamp(_overlayOriginFontSize * scale, 8, 240);
                    }
                }
                PositionOverlays();
            }
            e.Handled = true;
        }

        private void OverlayUp(object sender, MouseButtonEventArgs e)
        {
            _dragging = false; _dragClipId = null;
            if (sender is UIElement element) element.ReleaseMouseCapture();
            ShowProperties();
            e.Handled = true;
        }

        private void OverlayWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: Guid id } || _model.Clips.FirstOrDefault(c => c.Id == id) is not { } clip) return;
            _model.Select(id);
            _model.BeginUndoGroup();
            double factor = e.Delta > 0 ? 1.08 : 1 / 1.08;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) clip.Width = Math.Clamp(clip.Width * factor, 0.03, 1 - clip.X);
            else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) clip.Height = Math.Clamp(clip.Height * factor, 0.03, 1 - clip.Y);
            else
            {
                clip.Width = Math.Clamp(clip.Width * factor, 0.03, 1 - clip.X);
                clip.Height = Math.Clamp(clip.Height * factor, 0.03, 1 - clip.Y);
                if (clip.Kind == EditTimelineModel.ClipKind.Text)
                    clip.FontSize = Math.Clamp(clip.FontSize * factor, 8, 240);
            }
            PositionOverlays(); RenderTimeline(); ShowProperties();
            _status.Text = "عجلة الماوس للحجم الكامل • Shift للعرض فقط • Ctrl للطول فقط.";
            e.Handled = true;
        }

        private void HighlightSelectedOverlay()
        {
            foreach (var pair in _overlayElements) ApplySelectionBorder(pair.Value, pair.Key == _model.SelectedId);
        }

        private static void ApplySelectionBorder(FrameworkElement element, bool selected)
        {
            if (element is Grid grid)
            {
                grid.ToolTip = selected
                    ? "سحب: تحريك • سحب الحافة: عرض/طول • سحب الزاوية أو الزر الأيمن من أي مكان: تحجيم كامل • العجلة: تكبير/تصغير"
                    : null;
            }
        }

        private void UpdateOverlayVisibility()
        {
            double at = _timeline.Value;
            HashSet<Guid> active = _model.ActiveOverlays(at).Select(c => c.Id).ToHashSet();
            foreach (var pair in _overlayElements)
            {
                EditTimelineModel.Clip? clip = _model.Clips.FirstOrDefault(c => c.Id == pair.Key);
                bool trackVisible = clip != null && !_hiddenTracks.Contains(clip.Track);
                pair.Value.Visibility = active.Contains(pair.Key) && trackVisible ? Visibility.Visible : Visibility.Collapsed;
            }
            ApplyTimedEffects();
        }

        private void ApplyTimedEffects()
        {
            EditTimelineModel.Clip? activeLens = ActiveLensClip();
            if (activeLens?.Id != _renderedLensClipId) RenderLens();

            double at = _timeline.Value;
            EditTimelineModel.Clip? blur = _model.Clips
                .Where(c => c.Kind == EditTimelineModel.ClipKind.Blur && at >= c.Start && at < c.End && !_hiddenTracks.Contains(c.Track))
                .OrderBy(c => c.Track)
                .LastOrDefault();
            double radius = blur?.EffectStrength ?? 0;
            if (radius <= 0.01)
            {
                _media.Effect = null;
            }
            else if (_media.Effect is BlurEffect existing)
            {
                existing.Radius = radius;
            }
            else
            {
                _media.Effect = new BlurEffect
                {
                    Radius = radius,
                    KernelType = KernelType.Gaussian,
                    RenderingBias = RenderingBias.Quality
                };
            }
        }

        private void ShowProperties()
        {
            _properties.Children.Clear();
            _properties.Children.Add(PanelTitle("Effect Controls / الخصائص"));
            if (_model.SelectedClip is not { } clip)
            {
                _properties.Children.Add(new TextBlock { Text = "اختر مقطعًا من التايم لاين أو عنصرًا من شاشة المعاينة.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, Margin = new Thickness(0, 12, 0, 0) });
                return;
            }
            _properties.Children.Add(PropertyLine("المسار", clip.Track));
            _properties.Children.Add(PropertyLine("البداية", FormatTime(clip.Start)));
            _properties.Children.Add(PropertyLine("النهاية", FormatTime(clip.End)));
            _properties.Children.Add(PropertyLine("المدة", FormatTime(clip.End - clip.Start)));
            if (clip.Track == "V1")
            {
                _properties.Children.Add(new TextBlock { Text = "تحويل الفيديو", Margin = new Thickness(0, 16, 0, 5), Foreground = Brushes.White, FontWeight = FontWeights.SemiBold });
                _properties.Children.Add(new TextBlock { Text = "تكبير العرض  1× — 4×", Foreground = Brushes.LightGray });
                var scaleX = new Slider { Minimum = 1, Maximum = 4, Value = clip.HasVideoTransform ? clip.VideoScaleX : 1, TickFrequency = 0.05 };
                _properties.Children.Add(scaleX);
                _properties.Children.Add(new TextBlock { Text = "تكبير الطول  1× — 4×", Foreground = Brushes.LightGray, Margin = new Thickness(0, 7, 0, 0) });
                var scaleY = new Slider { Minimum = 1, Maximum = 4, Value = clip.HasVideoTransform ? clip.VideoScaleY : 1, TickFrequency = 0.05 };
                _properties.Children.Add(scaleY);
                var applyVideo = ToolButton("تطبيق حجم الفيديو", (_, _) =>
                {
                    _model.UpdateVideoTransform(clip.Id, scaleX.Value, scaleY.Value, clip.VideoOffsetX, clip.VideoOffsetY);
                    if (_activeVideoTransformId == clip.Id) LoadVideoTransform(clip);
                    _status.Text = "تم تطبيق حجم مستقل على هذا المقطع.";
                });
                var resetVideo = ToolButton("إعادة الحجم", (_, _) =>
                {
                    _model.UpdateVideoTransform(clip.Id, 1, 1, 0, 0);
                    if (_activeVideoTransformId == clip.Id) LoadVideoTransform(clip);
                    ShowProperties();
                });
                var videoButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
                videoButtons.Children.Add(applyVideo); videoButtons.Children.Add(resetVideo);
                _properties.Children.Add(videoButtons);
                _properties.Children.Add(new TextBlock { Text = "بالماوس: اسحب الفيديو لتحريكه، عجلة الماوس للتكبير، Shift+العجلة للعرض، Ctrl+العجلة للطول.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, Margin = new Thickness(0, 8, 0, 0) });
            }
            if (clip.Kind == EditTimelineModel.ClipKind.Lens)
            {
                _properties.Children.Add(new TextBlock { Text = "عدسة التكبير", Margin = new Thickness(0, 16, 0, 5), Foreground = Brushes.White, FontWeight = FontWeights.SemiBold });
                var lensZoom = new Slider { Minimum = 2, Maximum = 6, Value = clip.LensZoom, TickFrequency = 0.25 };
                var lensZoomValue = new TextBlock { Text = $"{clip.LensZoom:0.0}×", Foreground = Brushes.DeepSkyBlue, Margin = new Thickness(0, 2, 0, 4) };
                _properties.Children.Add(new TextBlock { Text = "قوة العدسة 2× — 6×", Foreground = Brushes.LightGray });
                _properties.Children.Add(lensZoom); _properties.Children.Add(lensZoomValue);
                var lensSize = new Slider { Minimum = 8, Maximum = 60, Value = clip.Width * 100, TickFrequency = 1 };
                var lensSizeValue = new TextBlock { Text = $"{clip.Width * 100:0}%", Foreground = Brushes.DeepSkyBlue, Margin = new Thickness(0, 2, 0, 4) };
                _properties.Children.Add(new TextBlock { Text = "حجم العدسة", Foreground = Brushes.LightGray, Margin = new Thickness(0, 7, 0, 0) });
                _properties.Children.Add(lensSize); _properties.Children.Add(lensSizeValue);
                lensZoom.PreviewMouseLeftButtonDown += (_, _) => _model.BeginUndoGroup();
                lensSize.PreviewMouseLeftButtonDown += (_, _) => _model.BeginUndoGroup();
                void ApplyLensLive()
                {
                    _model.UpdateLens(clip.Id, clip.X, clip.Y, lensSize.Value / 100.0, lensZoom.Value);
                    lensZoomValue.Text = $"{clip.LensZoom:0.0}×";
                    lensSizeValue.Text = $"{clip.Width * 100:0}%";
                    if (_renderedLensClipId == clip.Id)
                    {
                        _lensX = clip.X; _lensY = clip.Y; _lensSize = clip.Width; _lensZoom = clip.LensZoom;
                        UpdateLensVisual();
                    }
                    RenderTimeline();
                }
                lensZoom.ValueChanged += (_, _) => ApplyLensLive();
                lensSize.ValueChanged += (_, _) => ApplyLensLive();
                _properties.Children.Add(new TextBlock
                {
                    Text = "اسحب جسم العدسة من أي مكان لتحريكها، وShift+اسحب لتحجيمها. عجلة الماوس للحجم وCtrl+العجلة لقوة التكبير. حرّك الطبقة أو طرفيها في L1 للتحكم في وقت ظهورها.",
                    TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, Margin = new Thickness(0, 10, 0, 0)
                });
            }
            if (clip.Kind == EditTimelineModel.ClipKind.Blur)
            {
                _properties.Children.Add(new TextBlock { Text = "Gaussian Blur", Margin = new Thickness(0, 16, 0, 5), Foreground = Brushes.White, FontWeight = FontWeights.SemiBold });
                var strength = new Slider { Minimum = 0, Maximum = 50, Value = clip.EffectStrength, TickFrequency = 1 };
                var strengthValue = new TextBlock { Text = $"القوة: {clip.EffectStrength:0}", Foreground = Brushes.DeepSkyBlue, Margin = new Thickness(0, 2, 0, 4) };
                _properties.Children.Add(strength); _properties.Children.Add(strengthValue);
                strength.PreviewMouseLeftButtonDown += (_, _) => _model.BeginUndoGroup();
                strength.ValueChanged += (_, _) =>
                {
                    _model.UpdateBlur(clip.Id, strength.Value);
                    strengthValue.Text = $"القوة: {clip.EffectStrength:0}";
                    ApplyTimedEffects();
                    RenderTimeline();
                };
                strength.PreviewMouseWheel += (_, e) =>
                {
                    double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? 3 : 1;
                    strength.Value = Math.Clamp(strength.Value + (e.Delta > 0 ? step : -step), strength.Minimum, strength.Maximum);
                    e.Handled = true;
                };
                _properties.Children.Add(new TextBlock
                {
                    Text = "غيّر القوة بالسكرول فوق شاشة المعاينة أو فوق شريط القوة. Ctrl+السكرول للتغيير السريع. حرّك طبقة E1 أو اسحب طرفيها لتحديد زمن التأثير.",
                    TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, Margin = new Thickness(0, 10, 0, 0)
                });
            }
            if (clip.Kind == EditTimelineModel.ClipKind.Text)
            {
                _properties.Children.Add(new TextBlock { Text = "النص", Margin = new Thickness(0, 14, 0, 4), Foreground = Brushes.LightGray });
                var text = new TextBox { Text = clip.Text, MinHeight = 70, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
                _properties.Children.Add(text);
                _properties.Children.Add(new TextBlock { Text = "حجم الخط", Margin = new Thickness(0, 10, 0, 4), Foreground = Brushes.LightGray });
                var size = new Slider { Minimum = 12, Maximum = 120, Value = clip.FontSize, TickFrequency = 2 };
                _properties.Children.Add(size);
                _properties.Children.Add(new TextBlock { Text = "نوع الخط", Margin = new Thickness(0, 10, 0, 4), Foreground = Brushes.LightGray });
                var font = new ComboBox();
                foreach (string family in new[] { "Arial", "Segoe UI", "Tahoma", "Calibri", "Georgia", "Times New Roman", "Courier New" }) font.Items.Add(family);
                font.SelectedItem = clip.FontFamily;
                if (font.SelectedIndex < 0) font.SelectedIndex = 0;
                _properties.Children.Add(font);
                _properties.Children.Add(new TextBlock { Text = "لون النص", Margin = new Thickness(0, 10, 0, 4), Foreground = Brushes.LightGray });
                var color = new ComboBox { SelectedValuePath = "Tag" };
                color.Items.Add(new ComboBoxItem { Content = "أبيض", Tag = "#FFFFFF" });
                color.Items.Add(new ComboBoxItem { Content = "أسود", Tag = "#000000" });
                color.Items.Add(new ComboBoxItem { Content = "أصفر", Tag = "#FFD400" });
                color.Items.Add(new ComboBoxItem { Content = "أحمر", Tag = "#FF3030" });
                color.Items.Add(new ComboBoxItem { Content = "أزرق", Tag = "#29A8FF" });
                color.SelectedValue = clip.Color;
                if (color.SelectedIndex < 0) color.SelectedIndex = 0;
                _properties.Children.Add(color);
                text.GotKeyboardFocus += (_, _) => _model.BeginUndoGroup();
                text.TextChanged += (_, _) =>
                {
                    clip.Text = text.Text;
                    RenderOverlays();
                    _status.Text = "تحديث النص مباشر.";
                };
                size.PreviewMouseLeftButtonDown += (_, _) => _model.BeginUndoGroup();
                size.ValueChanged += (_, _) =>
                {
                    clip.FontSize = size.Value;
                    RenderOverlays();
                };
                font.SelectionChanged += (_, _) =>
                {
                    _model.BeginUndoGroup();
                    clip.FontFamily = font.SelectedItem as string ?? "Arial";
                    RenderOverlays();
                };
                color.SelectionChanged += (_, _) =>
                {
                    _model.BeginUndoGroup();
                    clip.Color = color.SelectedValue as string ?? "#FFFFFF";
                    RenderOverlays();
                };
                _properties.Children.Add(new TextBlock { Text = "تظهر الكتابة والحجم والخط واللون فورًا في شاشة المعاينة.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, Margin = new Thickness(0, 10, 0, 0) });
            }
            if (clip.Kind == EditTimelineModel.ClipKind.Audio)
            {
                _properties.Children.Add(new TextBlock { Text = "تحكم الصوت", Margin = new Thickness(0, 14, 0, 4), Foreground = Brushes.White, FontWeight = FontWeights.SemiBold });
                _properties.Children.Add(new TextBlock { Text = "مستوى الصوت  0% — 200%", Margin = new Thickness(0, 8, 0, 4), Foreground = Brushes.LightGray });
                var volume = new Slider { Minimum = 0, Maximum = 200, Value = clip.Volume * 100, TickFrequency = 10 };
                var volumeValue = new TextBlock { Text = $"{volume.Value:0}%", Foreground = Brushes.DeepSkyBlue, Margin = new Thickness(0, 2, 0, 4) };
                _properties.Children.Add(volume); _properties.Children.Add(volumeValue);

                _properties.Children.Add(new TextBlock { Text = "سرعة الصوت  0.5× — 2.0×", Margin = new Thickness(0, 8, 0, 4), Foreground = Brushes.LightGray });
                var speed = new Slider { Minimum = 0.5, Maximum = 2, Value = clip.Speed, TickFrequency = 0.05 };
                var speedValue = new TextBlock { Text = $"{speed.Value:0.00}×", Foreground = Brushes.DeepSkyBlue, Margin = new Thickness(0, 2, 0, 4) };
                _properties.Children.Add(speed); _properties.Children.Add(speedValue);
                var muted = new CheckBox { Content = "كتم هذا المقطع", IsChecked = clip.IsMuted, Foreground = Brushes.White, Margin = new Thickness(0, 8, 0, 4) };
                _properties.Children.Add(muted);
                void ApplyAudioLive()
                {
                    _model.UpdateAudio(clip.Id, volume.Value / 100.0, speed.Value, muted.IsChecked == true, false);
                    volumeValue.Text = $"{volume.Value:0}%";
                    speedValue.Text = $"{speed.Value:0.00}×";
                    SyncPrimaryAudio(_timeline.Value);
                    SyncExtraAudio(_timeline.Value, _playing);
                    if (clip.Track == "A2") RenderTimeline();
                    _status.Text = $"الصوت {volume.Value:0}% — السرعة {speed.Value:0.00}×";
                }
                foreach (Slider slider in new[] { volume, speed })
                {
                    slider.PreviewMouseLeftButtonDown += (_, _) => _model.BeginUndoGroup();
                    slider.PreviewKeyDown += (_, _) => _model.BeginUndoGroup();
                    slider.ValueChanged += (_, _) => ApplyAudioLive();
                }
                muted.Click += (_, _) =>
                {
                    _model.BeginUndoGroup();
                    ApplyAudioLive();
                };
                _properties.Children.Add(new TextBlock { Text = "يتغير مستوى الصوت والسرعة والكتم فورًا أثناء المعاينة.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, Margin = new Thickness(0, 10, 0, 0) });
                if (clip.Track == "A2")
                    _properties.Children.Add(new TextBlock { Text = System.IO.Path.GetFileName(clip.Source), TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, Margin = new Thickness(0, 10, 0, 0) });
            }
            if (clip.Kind is EditTimelineModel.ClipKind.Image or EditTimelineModel.ClipKind.Text)
            {
                _properties.Children.Add(new TextBlock { Text = "الحجم والمكان", Margin = new Thickness(0, 16, 0, 5), Foreground = Brushes.White, FontWeight = FontWeights.SemiBold });
                _properties.Children.Add(new TextBlock { Text = "الموضع الأفقي", Foreground = Brushes.LightGray });
                var x = new Slider { Minimum = 0, Maximum = 100, Value = clip.X * 100, TickFrequency = 1 };
                _properties.Children.Add(x);
                _properties.Children.Add(new TextBlock { Text = "الموضع العمودي", Foreground = Brushes.LightGray, Margin = new Thickness(0, 7, 0, 0) });
                var y = new Slider { Minimum = 0, Maximum = 100, Value = clip.Y * 100, TickFrequency = 1 };
                _properties.Children.Add(y);
                _properties.Children.Add(new TextBlock { Text = "العرض", Foreground = Brushes.LightGray, Margin = new Thickness(0, 7, 0, 0) });
                var width = new Slider { Minimum = 3, Maximum = 100, Value = clip.Width * 100, TickFrequency = 1 };
                _properties.Children.Add(width);
                _properties.Children.Add(new TextBlock { Text = "الارتفاع", Foreground = Brushes.LightGray, Margin = new Thickness(0, 7, 0, 0) });
                var height = new Slider { Minimum = 3, Maximum = 100, Value = clip.Height * 100, TickFrequency = 1 };
                _properties.Children.Add(height);
                void ApplyLayoutLive()
                {
                    clip.Width = width.Value / 100.0;
                    clip.Height = height.Value / 100.0;
                    clip.X = Math.Clamp(x.Value / 100.0, 0, 1 - clip.Width);
                    clip.Y = Math.Clamp(y.Value / 100.0, 0, 1 - clip.Height);
                    PositionOverlays();
                    _status.Text = "تحديث الحجم والمكان مباشر.";
                }
                foreach (Slider slider in new[] { x, y, width, height })
                {
                    slider.PreviewMouseLeftButtonDown += (_, _) => _model.BeginUndoGroup();
                    slider.ValueChanged += (_, _) => ApplyLayoutLive();
                }
                _properties.Children.Add(new TextBlock { Text = "بالماوس فقط: اسحب العنصر للتحريك، اسحب حافته لتغيير العرض أو الطول، واسحب الزاوية أو استخدم الزر الأيمن من أي مكان للتحجيم الكامل. عجلة الماوس تكبّر وتصغّر مباشرة.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, Margin = new Thickness(0, 14, 0, 0) });
            }
        }

        private void Toggle()
        {
            if (_model.Duration <= 0) return;
            if (_playing)
            {
                _media.Pause();
                foreach (MediaElement player in _audioPlayers.Values) player.Pause();
                _clock.Stop();
            }
            else
            {
                double playbackEnd = GetVideoPlaybackEnd();
                if (playbackEnd > 0 && _timeline.Value >= playbackEnd - 0.01)
                {
                    EditTimelineModel.Clip? first = GetVideoClips().FirstOrDefault();
                    Seek(first?.Start ?? 0);
                }
                _playbackRate = 1;
                _media.SpeedRatio = 1;
                _media.Play(); SyncExtraAudio(_timeline.Value, true); _clock.Start();
            }
            _playing = !_playing;
        }

        private void Tick()
        {
            if (_scrubbing || !_media.NaturalDuration.HasTimeSpan) return;
            List<EditTimelineModel.Clip> clips = GetVideoClips();
            EditTimelineModel.Clip? current = clips.FirstOrDefault(c => _timeline.Value >= c.Start && _timeline.Value < c.End);
            if (current == null)
            {
                EditTimelineModel.Clip? next = clips.FirstOrDefault(c => c.Start > _timeline.Value);
                if (next != null) { Seek(next.Start); if (_playing) _media.Play(); }
                else CompletePlayback(GetVideoPlaybackEnd());
                return;
            }
            if (_pendingVideoPosition != null) return;
            double sourcePosition = _media.Position.TotalSeconds;
            double sourceEnd = current.SourceOffset + (current.End - current.Start);
            if (sourcePosition < current.SourceOffset - 0.08)
            {
                Seek(current.Start);
                if (_playing) _media.Play();
                return;
            }
            if (sourcePosition >= sourceEnd - 0.003)
            {
                EditTimelineModel.Clip? next = clips.FirstOrDefault(c => c.Start >= current.End - 0.002 && c.Id != current.Id);
                bool continuousCut = next != null
                    && Math.Abs(next.Start - current.End) <= 0.003
                    && string.Equals(System.IO.Path.GetFullPath(next.Source), System.IO.Path.GetFullPath(current.Source), StringComparison.OrdinalIgnoreCase)
                    && Math.Abs(next.SourceOffset - sourceEnd) <= 0.035;
                if (continuousCut)
                {
                    LoadVideoTransform(next!);
                    double positionAcrossCut = next!.Start + Math.Max(0, sourcePosition - next.SourceOffset);
                    UpdatePlaybackPosition(Math.Clamp(positionAcrossCut, next.Start, next.End));
                    return;
                }
                if (!AdvanceToNextTimelineClip(current)) CompletePlayback(current.End);
                return;
            }
            double position = current.Start + (sourcePosition - current.SourceOffset);
            UpdatePlaybackPosition(Math.Clamp(position, current.Start, current.End));
        }

        private void UpdatePlaybackPosition(double position)
        {
            _timeline.Value = position;
            _time.Text = FormatTime(_timeline.Value) + " / " + FormatTime(_model.Duration);
            UpdateOverlayVisibility(); UpdatePlayhead();
            SyncExtraAudioIfNeeded(_timeline.Value);
            if (_playing) ScrollPlayheadIntoView();
        }

        private void Seek(double seconds)
        {
            seconds = Math.Clamp(seconds, 0, Math.Max(0, _model.Duration));
            List<EditTimelineModel.Clip> clips = _model.Clips.Where(c => c.Kind == EditTimelineModel.ClipKind.Video).OrderBy(c => c.Start).ToList();
            EditTimelineModel.Clip? clip = clips.FirstOrDefault(c => seconds >= c.Start && seconds < c.End);
            if (clip == null && Math.Abs(seconds - _model.Duration) < 0.001) clip = clips.LastOrDefault();
            if (clip == null)
            {
                clip = clips.FirstOrDefault(c => c.Start > seconds);
                if (clip != null) seconds = clip.Start;
            }
            if (clip != null)
            {
                LoadVideoTransform(clip);
                double offset = Math.Clamp(seconds - clip.Start, 0, Math.Max(0, clip.End - clip.Start - 0.001));
                double sourcePosition = clip.SourceOffset + offset;
                string source = System.IO.Path.GetFullPath(clip.Source);
                if (!string.Equals(_loadedVideoSource, source, StringComparison.OrdinalIgnoreCase))
                {
                    _resumeAfterVideoLoad = _playing;
                    _pendingVideoPosition = sourcePosition;
                    _loadedVideoSource = source;
                    _media.Source = new Uri(source, UriKind.Absolute);
                    _media.Play();
                }
                else _media.Position = TimeSpan.FromSeconds(sourcePosition);
            }
            _timeline.Value = seconds;
            _time.Text = FormatTime(seconds) + " / " + FormatTime(_model.Duration);
            UpdateOverlayVisibility(); UpdatePlayhead();
            SyncPrimaryAudio(seconds);
            SyncExtraAudio(seconds, _playing);
        }

        private void SyncPrimaryAudio(double timelineSeconds)
        {
            EditTimelineModel.Clip? audio = _model.Clips.FirstOrDefault(c => c.Track == "A1" && c.Kind == EditTimelineModel.ClipKind.Audio && timelineSeconds >= c.Start && timelineSeconds < c.End);
            _media.Volume = audio == null ? 1 : Math.Min(1, audio.Volume);
            _media.IsMuted = _audioMuted || audio?.IsMuted == true;
        }

        private void SyncExtraAudio(double timelineSeconds, bool play)
        {
            List<EditTimelineModel.Clip> active = _model.Clips
                .Where(c => c.Track != "A1" && c.Kind == EditTimelineModel.ClipKind.Audio && timelineSeconds >= c.Start && timelineSeconds < c.End)
                .ToList();
            HashSet<Guid> activeIds = active.Select(c => c.Id).ToHashSet();
            foreach (var pair in _audioPlayers.Where(pair => !activeIds.Contains(pair.Key))) pair.Value.Pause();
            foreach (EditTimelineModel.Clip clip in active)
            {
                MediaElement player = EnsureAudioPlayer(clip);
                player.Volume = Math.Min(1, clip.Volume);
                player.IsMuted = clip.IsMuted || _mutedAudioTracks.Contains(clip.Track);
                player.SpeedRatio = clip.Speed;
                double sourcePosition = clip.SourceOffset + (timelineSeconds - clip.Start) * clip.Speed;
                player.Position = TimeSpan.FromSeconds(Math.Max(0, sourcePosition));
                if (play) player.Play(); else player.Pause();
            }
        }

        private void SyncExtraAudioIfNeeded(double timelineSeconds)
        {
            List<EditTimelineModel.Clip> active = _model.Clips
                .Where(c => c.Track != "A1" && c.Kind == EditTimelineModel.ClipKind.Audio && timelineSeconds >= c.Start && timelineSeconds < c.End)
                .ToList();
            HashSet<Guid> activeIds = active.Select(c => c.Id).ToHashSet();
            foreach (var pair in _audioPlayers.Where(pair => !activeIds.Contains(pair.Key))) pair.Value.Pause();
            foreach (EditTimelineModel.Clip clip in active)
            {
                MediaElement player = EnsureAudioPlayer(clip);
                double expected = clip.SourceOffset + (timelineSeconds - clip.Start) * clip.Speed;
                player.Volume = Math.Min(1, clip.Volume);
                player.IsMuted = clip.IsMuted || _mutedAudioTracks.Contains(clip.Track);
                player.SpeedRatio = clip.Speed;
                if (Math.Abs(player.Position.TotalSeconds - expected) > 0.35)
                    player.Position = TimeSpan.FromSeconds(Math.Max(0, expected));
                if (_playing) player.Play();
            }
        }

        private MediaElement EnsureAudioPlayer(EditTimelineModel.Clip clip)
        {
            if (!_audioPlayers.TryGetValue(clip.Id, out MediaElement? player))
            {
                player = new MediaElement
                {
                    LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Stop,
                    ScrubbingEnabled = true, Opacity = 0, Width = 1, Height = 1,
                    IsHitTestVisible = false
                };
                _audioPlayers[clip.Id] = player;
                _programFrame.Children.Insert(0, player);
            }
            string fullPath = System.IO.Path.GetFullPath(clip.Source);
            if (!_loadedAudioSources.TryGetValue(clip.Id, out string? loaded) || !string.Equals(loaded, fullPath, StringComparison.OrdinalIgnoreCase))
            {
                _loadedAudioSources[clip.Id] = fullPath;
                player.Source = new Uri(fullPath, UriKind.Absolute);
                player.Play(); player.Pause();
            }
            return player;
        }

        private List<EditTimelineModel.Clip> GetVideoClips() => TimelinePlaybackNavigator.VideoClips(_model.Clips);

        private double GetVideoPlaybackEnd() => TimelinePlaybackNavigator.PlaybackEnd(_model.Clips);

        private void HandlePlaybackBoundary()
        {
            if (_handlingPlaybackBoundary) return;
            _handlingPlaybackBoundary = true;
            try
            {
                EditTimelineModel.Clip? current = TimelinePlaybackNavigator.Current(_model.Clips, _timeline.Value);
                if (!AdvanceToNextTimelineClip(current))
                    CompletePlayback(current?.End ?? GetVideoPlaybackEnd());
            }
            finally
            {
                _handlingPlaybackBoundary = false;
            }
        }

        private bool AdvanceToNextTimelineClip(EditTimelineModel.Clip? current = null)
        {
            EditTimelineModel.Clip? next = TimelinePlaybackNavigator.Next(_model.Clips, current, _timeline.Value);
            if (next == null) return false;
            Seek(next.Start);
            if (_playing && _pendingVideoPosition == null) _media.Play();
            return true;
        }

        private void CompletePlayback(double end)
        {
            _media.Pause();
            foreach (MediaElement player in _audioPlayers.Values) player.Pause();
            _clock.Stop();
            _playing = false;
            _playbackRate = 1;
            _media.SpeedRatio = 1;
            end = Math.Clamp(end, 0, Math.Max(0, _model.Duration));
            UpdatePlaybackPosition(end);
            SyncPrimaryAudio(end);
            _status.Text = "انتهت المعاينة بسلاسة — اضغط Space لإعادة التشغيل من البداية.";
        }

        private void RippleTrim(bool trimBefore)
        {
            if (_model.SelectedClip is not { } selected)
            {
                _status.Text = "حدد مقطعًا وضع رأس التشغيل داخله أولًا.";
                return;
            }
            if (_lockedTracks.Contains(selected.Track))
            {
                _status.Text = $"المسار {selected.Track} مقفل.";
                return;
            }
            double oldStart = selected.Start;
            if (_model.RippleTrimToPlayhead(_timeline.Value, trimBefore, _linked.IsChecked == true))
            {
                RefreshEditor();
                Seek(trimBefore ? oldStart : Math.Min(_timeline.Value, _model.Duration));
                _status.Text = trimBefore
                    ? "Q: حُذف الجزء قبل رأس التشغيل والتحم التايم لاين."
                    : "W: حُذف الجزء بعد رأس التشغيل والتحم التايم لاين.";
            }
            else _status.Text = "يجب أن يكون رأس التشغيل داخل المقطع المحدد لتنفيذ Ripple Trim.";
        }

        private void AddMarker()
        {
            double at = _timeline.Value;
            double tolerance = 1.0 / 30;
            double? existing = _markers.FirstOrDefault(value => Math.Abs(value - at) <= tolerance);
            if (existing is double value && Math.Abs(value - at) <= tolerance && _markers.Contains(value))
            {
                _markers.Remove(value);
                _status.Text = "تم حذف العلامة عند " + FormatTime(at);
            }
            else
            {
                _markers.Add(at);
                _markers.Sort();
                _status.Text = "أضيفت علامة عند " + FormatTime(at);
            }
            RenderTimeline();
        }

        private void GoToEdit(bool forward)
        {
            const double epsilon = 0.002;
            List<double> points = _model.Clips
                .SelectMany(clip => new[] { clip.Start, clip.End })
                .Concat(_markers)
                .Where(value => value >= 0 && value <= _model.Duration)
                .Distinct()
                .OrderBy(value => value)
                .ToList();
            double? target = forward
                ? points.FirstOrDefault(value => value > _timeline.Value + epsilon)
                : points.LastOrDefault(value => value < _timeline.Value - epsilon);
            if (target is double value && (forward ? value > _timeline.Value + epsilon : value < _timeline.Value - epsilon))
                Seek(value);
            else Seek(forward ? _model.Duration : 0);
        }

        private void PausePlayback()
        {
            _media.Pause();
            foreach (MediaElement player in _audioPlayers.Values) player.Pause();
            _clock.Stop();
            _playing = false;
        }

        private void ShowShortcutGuide()
        {
            var grid = new Grid { Margin = new Thickness(22) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var title = new TextBlock { Text = "اختصارات ForgeFlow Editor", FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 18) };
            Grid.SetColumnSpan(title, 3); grid.Children.Add(title);
            string left = "V   أداة التحديد\nC   قص عند رأس التشغيل\nH   أداة اليد\nS   تشغيل/إيقاف الجذب\nM   إضافة/حذف علامة\nShift+M   العلامة التالية\nQ   حذف ما قبل الرأس مع الإغلاق\nW   حذف ما بعد الرأس مع الإغلاق";
            string right = "Space   تشغيل/إيقاف\nJ / K / L   رجوع / توقف / أمام\n↑ / ↓   نقطة القطع السابقة/التالية\n← / →   إطار واحد\nShift+←/→   ثانية واحدة\nCtrl+K   قص عند رأس التشغيل\nDelete   حذف وإغلاق الفراغ\nCtrl+C / Ctrl+V   نسخ ولصق\nCtrl+Z / Ctrl+Shift+Z   تراجع وإعادة\n+ / −   تكبير التايم لاين\n\nالصورة والنص داخل المعاينة:\nسحب = تحريك • سحب الحافة = عرض/طول\nسحب الزاوية أو الزر الأيمن = تحجيم كامل\nالعجلة = تكبير/تصغير";
            var leftText = new TextBlock { Text = left, FontFamily = new FontFamily("Segoe UI"), FontSize = 14, LineHeight = 27, Foreground = Brushes.Gainsboro };
            var rightText = new TextBlock { Text = right, FontFamily = new FontFamily("Segoe UI"), FontSize = 14, LineHeight = 27, Foreground = Brushes.Gainsboro };
            Grid.SetRow(leftText, 1); grid.Children.Add(leftText);
            Grid.SetRow(rightText, 1); Grid.SetColumn(rightText, 2); grid.Children.Add(rightText);
            Button close = ToolButton("إغلاق", (_, _) => { });
            close.Width = 110; close.Margin = new Thickness(0, 20, 0, 0); close.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetRow(close, 2); Grid.SetColumnSpan(close, 3); grid.Children.Add(close);
            var dialog = new Window
            {
                Title = "اختصارات المحرر", Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Width = 720, Height = 470, ResizeMode = ResizeMode.NoResize,
                Background = PanelDark, Foreground = Brushes.White,
                Content = new Border { Background = Panel, BorderBrush = new SolidColorBrush(Color.FromRgb(65, 65, 72)), BorderThickness = new Thickness(1), Child = grid }
            };
            dialog.Resources[typeof(Button)] = CreateEditorButtonStyle();
            close.Click += (_, _) => dialog.Close();
            dialog.ShowDialog();
        }

        private void ShuttleForward()
        {
            if (!_playing) _playbackRate = 1;
            else _playbackRate = Math.Min(4, _playbackRate * 2);
            _media.SpeedRatio = _playbackRate;
            foreach (MediaElement player in _audioPlayers.Values) player.SpeedRatio = _playbackRate;
            if (!_playing)
            {
                _media.Play(); SyncExtraAudio(_timeline.Value, true); _clock.Start(); _playing = true;
            }
            _status.Text = $"تشغيل للأمام {_playbackRate:0.#}× — K للإيقاف.";
        }

        private void ShuttleBackward()
        {
            PausePlayback();
            Seek(Math.Max(0, _timeline.Value - (_playbackRate >= 2 ? 2 : 1)));
            _playbackRate = Math.Min(4, Math.Max(1, _playbackRate * 2));
            _status.Text = "J: رجوع سريع على التايم لاين — K للإيقاف، L للتشغيل للأمام.";
        }

        private void Split()
        {
            EditTimelineModel.Clip? selected = _model.SelectedClip;
            EditTimelineModel.Clip? cutTarget = selected != null && _timeline.Value > selected.Start && _timeline.Value < selected.End
                ? selected
                : _model.Clips.FirstOrDefault(c => c.Kind == EditTimelineModel.ClipKind.Video && _timeline.Value > c.Start && _timeline.Value < c.End);
            if (cutTarget != null && _lockedTracks.Contains(cutTarget.Track))
            {
                _status.Text = $"المسار {cutTarget.Track} مقفل."; return;
            }
            if (_model.SplitAt(_timeline.Value, _linked.IsChecked == true))
            {
                // Splitting does not change playback time or media. Redrawing only the
                // timeline avoids a redundant MediaElement seek and the visible micro-jump
                // that used to happen on every edit point.
                RenderTimeline();
                ShowProperties();
                UpdateOverlayVisibility();
                _status.Text = "تم تقسيم المقطع بسلاسة عند " + FormatTime(_timeline.Value);
            }
            else _status.Text = "اختر مقطعًا وضع رأس التشغيل داخله قبل التقسيم.";
        }

        private void DeleteSelected()
        {
            if (_model.SelectedClip is not { } selected)
            {
                _status.Text = "حدد مقطعًا ثم اضغط Delete للحذف وإغلاق الفراغ.";
                return;
            }
            if (_lockedTracks.Contains(selected.Track))
            {
                _status.Text = $"المسار {selected.Track} مقفل."; return;
            }
            double joinPosition = selected.Start;
            PausePlayback();
            if (_model.DeleteSelected(_linked.IsChecked == true))
            {
                RefreshEditor();
                Seek(Math.Min(joinPosition, _model.Duration));
                _status.Text = "تم حذف المقطع والتحمت المقاطع التالية — رأس التشغيل عند نقطة الالتحام. Space للمعاينة.";
            }
        }

        private void ImportMediaClicked(object? sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "Media|*.mp4;*.mov;*.mkv;*.avi;*.webm;*.m4v;*.mp3;*.wav;*.m4a;*.aac;*.flac;*.ogg;*.png;*.jpg;*.jpeg;*.webp;*.bmp",
                Title = "استيراد وسائط إلى المشروع",
                Multiselect = true
            };
            if (dialog.ShowDialog(this) != true) return;
            foreach (string file in dialog.FileNames.Where(IsSupportedDropFile)) RegisterProjectMedia(file);
            _status.Text = $"تم استيراد {dialog.FileNames.Length} ملف/ملفات إلى مكتبة المشروع. استخدم زر ＋ لإدراجها عند رأس التشغيل.";
        }

        private void RegisterProjectMedia(string path)
        {
            if (_projectMedia.Add(System.IO.Path.GetFullPath(path))) RefreshMediaBin();
        }

        private void RefreshMediaBin()
        {
            _mediaBin.Children.Clear();
            foreach (string path in _projectMedia.OrderBy(path => string.Equals(path, System.IO.Path.GetFullPath(_sourcePath), StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(System.IO.Path.GetFileName))
            {
                string extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
                string icon = IsVideoExtension(extension) ? "🎬" : IsAudioExtension(extension) ? "♫" : "▣";
                var row = new Grid { Margin = new Thickness(0, 2, 0, 2), Background = new SolidColorBrush(Color.FromRgb(43, 43, 47)) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
                var name = new TextBlock
                {
                    Text = $"{icon}  {System.IO.Path.GetFileName(path)}", ToolTip = path,
                    Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 7, 4, 7)
                };
                row.Children.Add(name);
                Button add = ToolButton("＋", async (_, _) => await AddProjectMediaAtPlayheadAsync(path));
                add.Width = 30; add.MinWidth = 30; add.Height = 28; add.Padding = new Thickness(0); add.Margin = new Thickness(2);
                add.ToolTip = "إدراج عند رأس التشغيل";
                Grid.SetColumn(add, 1); row.Children.Add(add);
                _mediaBin.Children.Add(row);
            }
        }

        private async System.Threading.Tasks.Task AddProjectMediaAtPlayheadAsync(string path)
        {
            string extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
            if (IsVideoExtension(extension)) await AddVideoFileAsync(path, _timeline.Value);
            else if (IsAudioExtension(extension)) await AddAudioFileAsync(path, _timeline.Value);
            else
            {
                EditTimelineModel.Clip clip = _model.AddImage(path, _timeline.Value, Math.Min(5, Math.Max(0.04, _model.Duration - _timeline.Value)));
                RefreshEditor();
                _status.Text = $"أضيفت الصورة إلى {clip.Track}.";
            }
        }

        private async void AddVideo(object? sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog { Filter = "Video|*.mp4;*.mov;*.mkv;*.avi;*.webm;*.m4v", Title = "اختر فيديو لإدراجه على V1/A1" };
            if (dialog.ShowDialog(this) != true) return;
            await AddVideoFileAsync(dialog.FileName, _timeline.Value);
        }

        private async System.Threading.Tasks.Task AddVideoFileAsync(string path, double at)
        {
            try
            {
                string ffprobe = System.IO.Path.Combine(AppContext.BaseDirectory, "Tools", "ffprobe.exe");
                VideoInfo? info = await VideoInfoReader.ReadAsync(ffprobe, path);
                double length = info?.DurationSeconds > 0 ? info.DurationSeconds : 1;
                RegisterProjectMedia(path);
                EditTimelineModel.Clip clip = _model.AddVideo(path, at, length);
                RefreshEditor();
                Seek(clip.Start);
                _status.Text = $"أُدرج {System.IO.Path.GetFileName(path)} على V1 وصوته على A1 عند {FormatTime(clip.Start)}. أُزيحت المقاطع التالية تلقائيًا.";
            }
            catch (Exception ex)
            {
                ShowEditorMessage("تعذر إدراج الفيديو", ex.Message, true);
            }
        }

        private void AddLogo(object? sender, RoutedEventArgs e)
        {
            if (_model.Duration <= 0) return;
            var dialog = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.webp;*.bmp", Title = "اختر شعارًا أو صورة" };
            if (dialog.ShowDialog(this) != true) return;
            RegisterProjectMedia(dialog.FileName);
            EditTimelineModel.Clip clip = _model.AddImage(dialog.FileName, _timeline.Value, Math.Min(5, _model.Duration));
            RefreshEditor(); _status.Text = $"أضيفت الصورة إلى {clip.Track}. اسحبها داخل المعاينة لتحديد مكانها.";
        }

        private void AddText(object? sender, RoutedEventArgs e)
        {
            if (_model.Duration <= 0) return;
            string? value = PromptText();
            if (string.IsNullOrWhiteSpace(value)) return;
            EditTimelineModel.Clip clip = _model.AddText(value, _timeline.Value, Math.Min(5, _model.Duration));
            RefreshEditor(); _status.Text = $"أضيف النص إلى {clip.Track}. يمكن تحرير الخط واللون والحجم من لوحة الخصائص.";
        }

        private void AddLensEffect()
        {
            if (_model.Duration <= 0) return;
            double available = Math.Max(0.04, _model.Duration - _timeline.Value);
            EditTimelineModel.Clip clip = _model.AddLens(_timeline.Value, Math.Min(5, available));
            RefreshEditor();
            _status.Text = $"أضيفت العدسة إلى {clip.Track}. اسحبها وحجّمها بالماوس، واضبط مدتها من طرفي المقطع في التايم لاين.";
        }

        private void AddBlurEffect()
        {
            if (_model.Duration <= 0) return;
            double available = Math.Max(0.04, _model.Duration - _timeline.Value);
            EditTimelineModel.Clip clip = _model.AddBlur(_timeline.Value, Math.Min(5, available));
            RefreshEditor();
            _status.Text = $"أضيف Gaussian Blur إلى {clip.Track}. عجلة الماوس فوق المعاينة تغيّر القوة، وطرفا المقطع يحددان زمنه.";
        }

        private async void AddAudio(object? sender, RoutedEventArgs e)
        {
            if (_model.Duration <= 0) return;
            var dialog = new OpenFileDialog { Filter = "Audio|*.mp3;*.wav;*.m4a;*.aac;*.flac;*.ogg", Title = "اختر ملف صوت أو موسيقى" };
            if (dialog.ShowDialog(this) != true) return;
            RegisterProjectMedia(dialog.FileName);
            await AddAudioFileAsync(dialog.FileName, _timeline.Value);
        }

        private async System.Threading.Tasks.Task AddAudioFileAsync(string path, double at)
        {
            try
            {
                string ffprobe = System.IO.Path.Combine(AppContext.BaseDirectory, "Tools", "ffprobe.exe");
                VideoInfo? info = await VideoInfoReader.ReadAsync(ffprobe, path);
                double length = info?.DurationSeconds > 0 ? info.DurationSeconds : Math.Max(0.04, _model.Duration - at);
                EditTimelineModel.Clip clip = _model.AddAudio(path, at, length);
                RefreshEditor();
                Seek(at);
                SyncExtraAudio(at, false);
                _status.Text = $"أضيف الصوت إلى {clip.Track}. اسحب المقطع لأي مكان، واسحب حوافه لقصه.";
            }
            catch (Exception ex)
            {
                ShowEditorMessage("تعذر إضافة الصوت", ex.Message, true);
            }
        }

        private void EditorDragOver(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                return;
            }
            string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
            e.Effects = files.Any(IsSupportedDropFile) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private async void EditorDrop(object sender, DragEventArgs e)
        {
            if (_model.Duration <= 0 || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            string[] files = ((string[])e.Data.GetData(DataFormats.FileDrop)).Where(IsSupportedDropFile).ToArray();
            if (files.Length == 0) return;
            double at = _timeline.Value;
            if (_tracksCanvas.IsMouseOver && _tracksCanvas.ActualWidth > 0)
            {
                Point point = e.GetPosition(_tracksCanvas);
                at = Math.Clamp(point.X / _tracksCanvas.ActualWidth * _model.Duration, 0, Math.Max(0, _model.Duration - 0.04));
            }
            foreach (string file in files)
            {
                string extension = System.IO.Path.GetExtension(file).ToLowerInvariant();
                RegisterProjectMedia(file);
                if (IsVideoExtension(extension))
                {
                    await AddVideoFileAsync(file, at);
                    if (_model.SelectedClip is { Kind: EditTimelineModel.ClipKind.Video } inserted) at = inserted.End;
                }
                else if (IsAudioExtension(extension)) await AddAudioFileAsync(file, at);
                else
                {
                    EditTimelineModel.Clip clip = _model.AddImage(file, at, Math.Min(5, Math.Max(0.04, _model.Duration - at)));
                    RefreshEditor();
                    _status.Text = $"أضيفت الصورة إلى {clip.Track}. حرّكها داخل المعاينة أو من التايم لاين.";
                }
            }
            Seek(at);
            e.Handled = true;
        }

        private static bool IsSupportedDropFile(string path)
        {
            string extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
            return IsVideoExtension(extension) || IsAudioExtension(extension) || extension is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp";
        }

        private static bool IsVideoExtension(string extension) => extension is ".mp4" or ".mov" or ".mkv" or ".avi" or ".webm" or ".m4v";
        private static bool IsAudioExtension(string extension) => extension is ".mp3" or ".wav" or ".m4a" or ".aac" or ".flac" or ".ogg";

        private async void ExportClicked(object? sender, RoutedEventArgs e)
        {
            if (_model.Duration <= 0) return;
            EditorExportSettings exportSettings = EditorExportSettings.Load(_projectWidth, _projectHeight, _projectFrameRate);
            var settingsWindow = new EditorExportSettingsWindow(this, exportSettings, _model.Duration);
            if (settingsWindow.ShowDialog() != true) return;
            exportSettings = settingsWindow.ResultSettings;
            var save = new SaveFileDialog
            {
                Filter = "MP4 Video|*.mp4", DefaultExt = ".mp4", AddExtension = true,
                FileName = System.IO.Path.GetFileNameWithoutExtension(_sourcePath) + "_EDITED.mp4",
                InitialDirectory = System.IO.Path.GetDirectoryName(_sourcePath)
            };
            if (save.ShowDialog(this) != true) return;
            using var exportCancellation = new System.Threading.CancellationTokenSource();
            var progressLabel = new TextBlock { Text = "تحضير التصدير...", Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 10) };
            var progressTrack = new Grid { Width = 430, Height = 8, Background = new SolidColorBrush(Color.FromRgb(49, 49, 54)), ClipToBounds = true };
            var progressFill = new Border { Width = 0, HorizontalAlignment = HorizontalAlignment.Left, Background = new SolidColorBrush(Color.FromRgb(0, 155, 215)), CornerRadius = new CornerRadius(4) };
            progressTrack.Children.Add(progressFill);
            Button cancelExport = ToolButton("إلغاء التصدير", (_, _) =>
            {
                exportCancellation.Cancel();
                progressLabel.Text = "جارٍ إيقاف FFmpeg بأمان...";
            });
            cancelExport.HorizontalAlignment = HorizontalAlignment.Right;
            cancelExport.Margin = new Thickness(0, 14, 0, 0);
            var progressPanel = new StackPanel { Margin = new Thickness(18) };
            progressPanel.Children.Add(new TextBlock { Text = "تصدير المشروع", FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 5) });
            progressPanel.Children.Add(new TextBlock
            {
                Text = $"{exportSettings.Width}×{exportSettings.Height} • {exportSettings.FrameRate:0.##} FPS • {exportSettings.VideoEncoder} • " +
                       (exportSettings.IsBitrateMode ? $"{exportSettings.VideoBitrateMbps:0.##} Mbps" : $"CQ {exportSettings.Quality}"),
                Foreground = Brushes.Gray,
                FontSize = 11,
                Margin = new Thickness(0, 0, 0, 12)
            });
            progressPanel.Children.Add(progressLabel);
            progressPanel.Children.Add(progressTrack);
            progressPanel.Children.Add(cancelExport);
            var progressWindow = new Window
            {
                Title = "ForgeFlow Export",
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Width = 480,
                Height = 215,
                ResizeMode = ResizeMode.NoResize,
                Background = Panel,
                Foreground = Brushes.White,
                Content = progressPanel,
                ShowInTaskbar = false
            };
            progressWindow.Resources[typeof(Button)] = CreateEditorButtonStyle();
            NativeWindowTheme.Apply(progressWindow);
            progressWindow.Closing += (_, _) => exportCancellation.Cancel();
            try
            {
                IsEnabled = false;
                progressWindow.Show();
                _status.Text = "جارٍ تصدير المشروع عبر FFmpeg...";
                _options.ExportEditOnly = true;
                var progress = new Progress<string>(message =>
                {
                    _status.Text = message;
                    progressLabel.Text = message;
                    int marker = message.IndexOf('%');
                    int colon = message.IndexOf(':');
                    if (marker > colon && colon >= 0 && double.TryParse(message[(colon + 1)..marker].Trim(), out double percent))
                        progressFill.Width = Math.Max(0, progressTrack.ActualWidth * Math.Clamp(percent, 0, 100) / 100.0);
                });
                await EditorExportService.ExportAsync(
                    _model, save.FileName, exportSettings,
                    1, 1, 0, 0,
                    _lensEnabled, _lensX, _lensY, _lensZoom, _lensSize,
                    progress, exportCancellation.Token);
                _status.Text = "اكتمل التصدير: " + save.FileName;
                progressWindow.Close();
                ShowEditorMessage("اكتمل التصدير", "تم تصدير الفيديو بنجاح.\n\n" + save.FileName, false);
            }
            catch (OperationCanceledException)
            {
                _status.Text = "تم إلغاء التصدير وإيقاف عملية FFmpeg.";
            }
            catch (Exception ex)
            {
                _status.Text = "فشل التصدير.";
                ShowEditorMessage("خطأ في التصدير", ex.Message, true);
            }
            finally
            {
                if (progressWindow.IsVisible) progressWindow.Close();
                IsEnabled = true;
                Activate();
            }
        }

        private void HandleKeys(object sender, KeyEventArgs e)
        {
            if (Keyboard.FocusedElement is TextBoxBase) return;
            if (Keyboard.FocusedElement is Slider && e.Key is Key.Left or Key.Right or Key.Home or Key.End or Key.PageUp or Key.PageDown)
                return;
            bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
            bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            if (ctrl && shift && e.Key == Key.Z) { if (_model.Redo()) RefreshEditor(); }
            else if (ctrl && e.Key == Key.Z) { if (_model.Undo()) RefreshEditor(); }
            else if (ctrl && e.Key == Key.Y) { if (_model.Redo()) RefreshEditor(); }
            else if (ctrl && e.Key == Key.C) { if (_model.CopySelected(_linked.IsChecked == true)) _status.Text = _linked.IsChecked == true ? "تم نسخ المقطع مع الصوت المرتبط." : "تم نسخ المقطع المحدد."; }
            else if (ctrl && e.Key == Key.V) { if (_model.PasteAt(_timeline.Value)) { RefreshEditor(); _status.Text = "تم اللصق عند رأس التشغيل."; } }
            else if (ctrl && e.Key == Key.D)
            {
                if (_model.DuplicateSelected(_timeline.Value, _linked.IsChecked == true)) { RefreshEditor(); _status.Text = "تم تكرار المقطع عند رأس التشغيل مع الحفاظ على ارتباط الصوت."; }
            }
            else if (ctrl && e.Key == Key.K) Split();
            else if (e.Key == Key.Space) Toggle();
            else if (!ctrl && e.Key == Key.V) SetTool(EditorTool.Selection);
            else if (!ctrl && e.Key == Key.C) Split();
            else if (!ctrl && e.Key == Key.H) SetTool(EditorTool.Hand);
            else if (!ctrl && e.Key == Key.S)
            {
                _snappingEnabled = !_snappingEnabled;
                if (_snapButton != null) UpdateToggleButton(_snapButton, _snappingEnabled);
                _status.Text = _snappingEnabled ? "الجذب مفعّل (S)." : "الجذب متوقف (S).";
            }
            else if (!ctrl && e.Key == Key.M)
            {
                if (shift)
                {
                    double next = _markers.FirstOrDefault(value => value > _timeline.Value + 0.002);
                    if (next > _timeline.Value + 0.002) Seek(next);
                }
                else AddMarker();
            }
            else if (!ctrl && e.Key == Key.Q) RippleTrim(true);
            else if (!ctrl && e.Key == Key.W) RippleTrim(false);
            else if (!ctrl && e.Key == Key.J) ShuttleBackward();
            else if (!ctrl && e.Key == Key.K) PausePlayback();
            else if (!ctrl && e.Key == Key.L) ShuttleForward();
            else if (e.Key is Key.Delete or Key.Back) DeleteSelected();
            else if (e.Key == Key.Escape) SetTool(EditorTool.Selection);
            else if (e.Key == Key.Home) Seek(0);
            else if (e.Key == Key.End) Seek(_model.Duration);
            else if (e.Key == Key.Up) GoToEdit(false);
            else if (e.Key == Key.Down) GoToEdit(true);
            else if (e.Key is Key.OemPlus or Key.Add) _zoom.Value = Math.Min(_zoom.Maximum, _zoom.Value * 1.22);
            else if (e.Key is Key.OemMinus or Key.Subtract) _zoom.Value = Math.Max(_zoom.Minimum, _zoom.Value / 1.22);
            else if (e.Key == Key.OemBackslash) FitTimeline();
            else if (e.Key == Key.Left) Seek(_timeline.Value - (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 1 : 1.0 / 30));
            else if (e.Key == Key.Right) Seek(_timeline.Value + (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 1 : 1.0 / 30));
            else return;
            e.Handled = true;
        }

        private void TimelineMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
            Point pointer = e.GetPosition(_timelineScroll);
            double oldWidth = Math.Max(1, _tracksCanvas.Width);
            double anchor = (_timelineScroll.HorizontalOffset + pointer.X) / oldWidth;
            _zoom.Value = Math.Clamp(_zoom.Value * (e.Delta > 0 ? 1.18 : 1.0 / 1.18), _zoom.Minimum, _zoom.Maximum);
            UpdateLayout();
            _timelineScroll.ScrollToHorizontalOffset(Math.Max(0, anchor * _tracksCanvas.Width - pointer.X));
            e.Handled = true;
        }

        private void FitTimeline()
        {
            if (_model.Duration <= 0) return;
            double viewport = Math.Max(320, _timelineScroll.ViewportWidth - 16);
            _zoom.Value = Math.Clamp(viewport / _model.Duration, _zoom.Minimum, _zoom.Maximum);
            _timelineScroll.ScrollToHorizontalOffset(0);
            _status.Text = "تمت ملاءمة كامل المشروع داخل التايم لاين.";
        }

        private static double NormalizeProjectFrameRate(double sourceFrameRate)
        {
            if (sourceFrameRate >= 50) return 60;
            if (sourceFrameRate >= 23) return 30;
            return Math.Clamp(Math.Round(sourceFrameRate), 15, 60);
        }

        private void UpdateSequenceBadge()
        {
            _sequenceBadge.Text = $"{_projectWidth}×{_projectHeight}  •  {_projectFrameRate:0.##}p";
        }

        private string? PromptText()
        {
            var input = new TextBox { MinWidth = 390, MinHeight = 90, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12) };
            var ok = new Button { Content = "إضافة", Width = 90, Height = 30, Margin = new Thickness(6), IsDefault = true };
            var cancel = new Button { Content = "إلغاء", Width = 90, Height = 30, Margin = new Thickness(6), IsCancel = true };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(cancel); buttons.Children.Add(ok);
            var panel = new StackPanel(); panel.Children.Add(new TextBlock { Text = "اكتب النص الذي سيظهر على الفيديو:", Margin = new Thickness(12, 12, 12, 0) }); panel.Children.Add(input); panel.Children.Add(buttons);
            var dialog = new Window { Title = "إضافة نص", Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, Background = Panel, Foreground = Brushes.White, Content = panel };
            dialog.Resources[typeof(Button)] = CreateEditorButtonStyle();
            dialog.Resources[typeof(TextBox)] = CreateEditorTextBoxStyle();
            ok.Click += (_, _) => dialog.DialogResult = true;
            return dialog.ShowDialog() == true ? input.Text : null;
        }

        private void ShowEditorMessage(string title, string message, bool isError)
        {
            var ok = ToolButton("حسنًا", (_, _) => { });
            ok.Width = 100; ok.HorizontalAlignment = HorizontalAlignment.Right;
            var panel = new StackPanel { Margin = new Thickness(18) };
            panel.Children.Add(new TextBlock { Text = isError ? "تعذر إكمال العملية" : "تمت العملية بنجاح", Foreground = isError ? Brushes.OrangeRed : Brushes.LightGreen, FontSize = 18, FontWeight = FontWeights.SemiBold });
            panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 560, Margin = new Thickness(0, 12, 0, 18), Foreground = Brushes.White });
            panel.Children.Add(ok);
            var dialog = new Window { Title = title, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, Background = Panel, Foreground = Brushes.White, Content = panel };
            dialog.Resources[typeof(Button)] = CreateEditorButtonStyle();
            ok.Click += (_, _) => dialog.Close();
            dialog.ShowDialog();
        }

        private void ScrollPlayheadIntoView()
        {
            if (_model.Duration <= 0 || _timelineScroll.ViewportWidth <= 0) return;
            double x = _timeline.Value / _model.Duration * _tracksCanvas.Width;
            double left = _timelineScroll.HorizontalOffset;
            double right = left + _timelineScroll.ViewportWidth;
            if (x < left + 30) _timelineScroll.ScrollToHorizontalOffset(Math.Max(0, x - 60));
            else if (x > right - 30) _timelineScroll.ScrollToHorizontalOffset(Math.Max(0, x - _timelineScroll.ViewportWidth + 60));
        }

        private static Button ToolButton(string text, RoutedEventHandler handler)
        {
            var button = new Button { Content = text, Height = 30, MinWidth = 62, Margin = new Thickness(3), Padding = new Thickness(9, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center };
            button.Click += handler; return button;
        }

        private static Style CreateEditorButtonStyle()
        {
            var style = new Style(typeof(Button));
            style.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(54, 54, 59))));
            style.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(77, 77, 84))));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
            style.Setters.Add(new Setter(Control.FontFamilyProperty, new FontFamily("Segoe UI")));
            style.Setters.Add(new Setter(Control.FontSizeProperty, 12.0));
            style.Setters.Add(new Setter(Control.CursorProperty, Cursors.Hand));
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
            border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
            border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
            border.SetBinding(Border.BorderThicknessProperty, new System.Windows.Data.Binding("BorderThickness") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(presenter);
            var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(67, 78, 86))));
            hover.Setters.Add(new Setter(Control.BorderBrushProperty, Accent));
            template.Triggers.Add(hover);
            var pressed = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
            pressed.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0, 105, 150))));
            template.Triggers.Add(pressed);
            var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.42));
            template.Triggers.Add(disabled);
            style.Setters.Add(new Setter(Control.TemplateProperty, template));
            return style;
        }

        private static Style CreateEditorTextBoxStyle()
        {
            var style = new Style(typeof(TextBox));
            style.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(38, 38, 42))));
            style.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(73, 73, 80))));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 5, 8, 5)));
            style.Setters.Add(new Setter(TextBox.CaretBrushProperty, Brushes.White));
            style.Setters.Add(new Setter(TextBox.SelectionBrushProperty, Accent));
            return style;
        }

        private static Style CreateEditorComboBoxStyle()
        {
            const string xaml = """
                <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                 xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                                 TargetType="{x:Type ComboBox}">
                  <Grid>
                    <ToggleButton x:Name="Toggle" Focusable="False" ClickMode="Press"
                                  IsChecked="{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}">
                      <ToggleButton.Template>
                        <ControlTemplate TargetType="{x:Type ToggleButton}">
                          <Border x:Name="Frame" Background="#26262A" BorderBrush="#494950" BorderThickness="1" CornerRadius="5">
                            <Grid>
                              <Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition Width="28"/></Grid.ColumnDefinitions>
                              <ContentPresenter Margin="9,4,4,4" VerticalAlignment="Center" HorizontalAlignment="Left"
                                                Content="{Binding SelectionBoxItem, RelativeSource={RelativeSource AncestorType=ComboBox}}"
                                                ContentTemplate="{Binding SelectionBoxItemTemplate, RelativeSource={RelativeSource AncestorType=ComboBox}}"/>
                              <TextBlock Grid.Column="1" Text="▾" Foreground="#BFC5CA" FontSize="12" HorizontalAlignment="Center" VerticalAlignment="Center"/>
                            </Grid>
                          </Border>
                          <ControlTemplate.Triggers>
                            <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Frame" Property="BorderBrush" Value="#0091D2"/></Trigger>
                            <Trigger Property="IsChecked" Value="True"><Setter TargetName="Frame" Property="Background" Value="#30343A"/><Setter TargetName="Frame" Property="BorderBrush" Value="#F28C28"/></Trigger>
                          </ControlTemplate.Triggers>
                        </ControlTemplate>
                      </ToggleButton.Template>
                    </ToggleButton>
                    <Popup x:Name="PART_Popup" IsOpen="{TemplateBinding IsDropDownOpen}" Placement="Bottom" AllowsTransparency="True" Focusable="False" PopupAnimation="Fade">
                      <Border Margin="0,3,0,0" MinWidth="{TemplateBinding ActualWidth}" MaxHeight="{TemplateBinding MaxDropDownHeight}"
                              Background="#26262A" BorderBrush="#55555C" BorderThickness="1" CornerRadius="5">
                        <ScrollViewer Margin="2" SnapsToDevicePixels="True">
                          <StackPanel IsItemsHost="True" KeyboardNavigation.DirectionalNavigation="Contained"/>
                        </ScrollViewer>
                      </Border>
                    </Popup>
                  </Grid>
                </ControlTemplate>
                """;
            var style = new Style(typeof(ComboBox));
            style.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(38, 38, 42))));
            style.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(73, 73, 80))));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(7, 4, 7, 4)));
            style.Setters.Add(new Setter(ComboBox.MaxDropDownHeightProperty, 260.0));
            style.Setters.Add(new Setter(Control.TemplateProperty, (ControlTemplate)XamlReader.Parse(xaml)));
            return style;
        }

        private static Style CreateEditorComboBoxItemStyle()
        {
            var style = new Style(typeof(ComboBoxItem));
            style.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(38, 38, 42))));
            style.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(9, 6, 9, 6)));
            style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
            var hover = new Trigger { Property = ComboBoxItem.IsHighlightedProperty, Value = true };
            hover.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0, 105, 150))));
            style.Triggers.Add(hover);
            var selected = new Trigger { Property = ComboBoxItem.IsSelectedProperty, Value = true };
            selected.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(176, 83, 18))));
            style.Triggers.Add(selected);
            return style;
        }

        private static ControlTemplate CreateEditorThumbTemplate(double radius)
        {
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius));
            border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background")
            {
                RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent)
            });
            return new ControlTemplate(typeof(Thumb)) { VisualTree = border };
        }

        private static Style CreateEditorSliderStyle()
        {
            const string xaml = """
                <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                 xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                                 TargetType="{x:Type Slider}">
                  <Grid Height="24" Background="Transparent">
                    <Border Height="4" Margin="2,0" VerticalAlignment="Center" Background="#35353A" CornerRadius="2"/>
                    <Track x:Name="PART_Track" Orientation="Horizontal">
                      <Track.DecreaseRepeatButton>
                        <RepeatButton Command="Slider.DecreaseLarge" Background="#008FCE">
                          <RepeatButton.Template>
                            <ControlTemplate TargetType="{x:Type RepeatButton}">
                              <Border Height="4" VerticalAlignment="Center" Background="{TemplateBinding Background}" CornerRadius="2"/>
                            </ControlTemplate>
                          </RepeatButton.Template>
                        </RepeatButton>
                      </Track.DecreaseRepeatButton>
                      <Track.IncreaseRepeatButton>
                        <RepeatButton Command="Slider.IncreaseLarge" Background="Transparent">
                          <RepeatButton.Template>
                            <ControlTemplate TargetType="{x:Type RepeatButton}">
                              <Border Background="Transparent"/>
                            </ControlTemplate>
                          </RepeatButton.Template>
                        </RepeatButton>
                      </Track.IncreaseRepeatButton>
                      <Track.Thumb>
                        <Thumb Width="15" Height="15" Background="#F28C28" Cursor="Hand">
                          <Thumb.Template>
                            <ControlTemplate TargetType="{x:Type Thumb}">
                              <Border Background="{TemplateBinding Background}" BorderBrush="#FFD2A4" BorderThickness="1" CornerRadius="8"/>
                            </ControlTemplate>
                          </Thumb.Template>
                        </Thumb>
                      </Track.Thumb>
                    </Track>
                  </Grid>
                </ControlTemplate>
                """;
            var style = new Style(typeof(Slider));
            style.Setters.Add(new Setter(Control.TemplateProperty, (ControlTemplate)XamlReader.Parse(xaml)));
            style.Setters.Add(new Setter(Control.ForegroundProperty, Accent));
            return style;
        }

        private static TextBlock PanelTitle(string text) => new() { Text = text, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White };
        private static TextBlock PropertyLine(string label, string value) => new() { Text = label + ":  " + value, Margin = new Thickness(0, 8, 0, 0), Foreground = Brushes.LightGray };
        private static Brush ClipBrush(EditTimelineModel.ClipKind kind) => kind switch
        {
            EditTimelineModel.ClipKind.Video => new SolidColorBrush(Color.FromRgb(0, 91, 126)),
            EditTimelineModel.ClipKind.Audio => new SolidColorBrush(Color.FromRgb(22, 88, 103)),
            EditTimelineModel.ClipKind.Image => new SolidColorBrush(Color.FromRgb(91, 55, 127)),
            EditTimelineModel.ClipKind.Lens => new SolidColorBrush(Color.FromRgb(22, 112, 83)),
            EditTimelineModel.ClipKind.Blur => new SolidColorBrush(Color.FromRgb(73, 67, 145)),
            _ => new SolidColorBrush(Color.FromRgb(124, 70, 32))
        };
        private static Brush ParseBrush(string value)
        {
            try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(value)); }
            catch { return Brushes.White; }
        }
        private static string FormatTime(double seconds) => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"hh\:mm\:ss\.ff", System.Globalization.CultureInfo.InvariantCulture);
    }
}
