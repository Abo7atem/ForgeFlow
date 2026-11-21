using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace ForgeFlow
{
    internal sealed class EditorExportSettingsWindow : Window
    {
        private static readonly Brush Panel = new SolidColorBrush(Color.FromRgb(31, 31, 35));
        private static readonly Brush PanelAlt = new SolidColorBrush(Color.FromRgb(40, 40, 45));
        private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(225, 105, 0));
        private readonly double _duration;
        private readonly TextBox _width = Field();
        private readonly TextBox _height = Field();
        private readonly ComboBox _fps = Combo();
        private readonly ComboBox _encoder = Combo();
        private readonly ComboBox _rateControl = Combo();
        private readonly ComboBox _preset = Combo();
        private readonly ComboBox _pixelFormat = Combo();
        private readonly TextBox _targetBitrate = Field();
        private readonly TextBox _maxBitrate = Field();
        private readonly TextBox _bufferSize = Field();
        private readonly Slider _quality = new() { Minimum = 0, Maximum = 51, TickFrequency = 1, IsSnapToTickEnabled = true, IsMoveToPointEnabled = true, Width = 260 };
        private readonly TextBlock _qualityValue = new() { Foreground = Brushes.White, Width = 38, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        private readonly ComboBox _gop = Combo();
        private readonly ComboBox _audio = Combo();
        private readonly ComboBox _audioBitrate = Combo();
        private readonly ComboBox _fastStart = Combo();
        private readonly TextBlock _estimate = new() { Foreground = Brushes.LightSkyBlue, FontSize = 13, FontWeight = FontWeights.SemiBold };
        private readonly TextBlock _error = new() { Foreground = Brushes.OrangeRed, TextWrapping = TextWrapping.Wrap, MinHeight = 20 };

        public EditorExportSettings ResultSettings { get; private set; }

        public EditorExportSettingsWindow(Window owner, EditorExportSettings settings, double duration)
        {
            Owner = owner;
            ResultSettings = settings.Clone();
            _duration = duration;
            Title = "ForgeFlow — إعدادات التصدير";
            Width = 760;
            Height = 780;
            MinWidth = 680;
            MinHeight = 620;
            MaxHeight = SystemParameters.WorkArea.Height - 40;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(Color.FromRgb(20, 20, 23));
            Foreground = Brushes.White;
            FlowDirection = FlowDirection.RightToLeft;
            ShowInTaskbar = false;
            Resources[typeof(Button)] = ButtonStyle();
            Resources[typeof(TextBox)] = TextBoxStyle();
            Resources[typeof(ComboBox)] = DarkComboBoxStyles.CreateComboBoxStyle();
            Resources[typeof(ComboBoxItem)] = DarkComboBoxStyles.CreateComboBoxItemStyle();

            PopulateChoices();
            LoadValues(settings);
            Content = BuildContent();
            HookLiveUpdates();
            UpdateControlState();
            UpdateEstimate();
            NativeWindowTheme.Apply(this);
        }

        private UIElement BuildContent()
        {
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition());
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var header = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(13, 13, 15)),
                BorderBrush = Accent,
                BorderThickness = new Thickness(0, 0, 0, 2),
                Padding = new Thickness(22, 16, 22, 16)
            };
            var headerText = new StackPanel();
            headerText.Children.Add(new TextBlock { Text = "تصدير الفيديو", FontSize = 22, FontWeight = FontWeights.SemiBold });
            headerText.Children.Add(new TextBlock { Text = "تحكم كامل بالدقة والإطارات والترميز والبت ريت والصوت", Foreground = Brushes.Gray, Margin = new Thickness(0, 4, 0, 0) });
            header.Child = headerText;
            Grid.SetRow(header, 0);
            root.Children.Add(header);

            var content = new StackPanel { Margin = new Thickness(20, 14, 20, 14) };
            var quick = new Border { Background = PanelAlt, CornerRadius = new CornerRadius(8), Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 10) };
            var quickPanel = new StackPanel();
            quickPanel.Children.Add(new TextBlock { Text = "قوالب احترافية سريعة", Foreground = Brushes.LightGray, Margin = new Thickness(0, 0, 0, 7) });
            var quickButtons = new StackPanel { Orientation = Orientation.Horizontal };
            var fourK = new Button { Content = "4K Ultra • 150 Mbps", Height = 34, Margin = new Thickness(0, 0, 7, 0), ToolTip = "Master عالي الجودة بدقة 4K وبت ريت مرتفع." };
            fourK.Click += (_, _) => Apply4KPreset();
            var lossless = new Button { Content = "Lossless Master", Height = 34, ToolTip = "ترميز H.264 CRF 0؛ حجم الملف سيكون كبيرًا جدًا." };
            lossless.Click += (_, _) => ApplyLosslessPreset();
            quickButtons.Children.Add(fourK); quickButtons.Children.Add(lossless);
            quickPanel.Children.Add(quickButtons); quick.Child = quickPanel; content.Children.Add(quick);
            content.Children.Add(Section("إعدادات الصورة", new (string, Control)[]
            {
                ("العرض", _width), ("الارتفاع", _height), ("معدل الإطارات", _fps)
            }));
            content.Children.Add(Section("الترميز والجودة", new (string, Control)[]
            {
                ("مرمّز الفيديو", _encoder), ("التحكم بالمعدل", _rateControl), ("سرعة الترميز", _preset), ("عمق اللون", _pixelFormat)
            }));
            content.Children.Add(Section("البت ريت", new (string, Control)[]
            {
                ("Target Mbps", _targetBitrate), ("Maximum Mbps", _maxBitrate), ("Buffer Mbps", _bufferSize)
            }));

            var qualityRow = new Grid { Margin = new Thickness(0, 7, 0, 7) };
            qualityRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
            qualityRow.ColumnDefinitions.Add(new ColumnDefinition());
            qualityRow.Children.Add(Label("الجودة CRF / CQ"));
            var qualityPanel = new StackPanel { Orientation = Orientation.Horizontal, FlowDirection = FlowDirection.LeftToRight };
            qualityPanel.Children.Add(_quality);
            qualityPanel.Children.Add(_qualityValue);
            Grid.SetColumn(qualityPanel, 1);
            qualityRow.Children.Add(qualityPanel);
            content.Children.Add(qualityRow);

            content.Children.Add(Section("إعدادات متقدمة", new (string, Control)[]
            {
                ("Keyframe / GOP", _gop), ("الصوت", _audio), ("بت ريت الصوت", _audioBitrate), ("Web Fast Start", _fastStart)
            }));

            var estimateCard = new Border { Background = PanelAlt, CornerRadius = new CornerRadius(8), Padding = new Thickness(14), Margin = new Thickness(0, 10, 0, 0) };
            var estimatePanel = new StackPanel();
            estimatePanel.Children.Add(new TextBlock { Text = "الملف المتوقع", Foreground = Brushes.Gray, Margin = new Thickness(0, 0, 0, 5) });
            estimatePanel.Children.Add(_estimate);
            estimatePanel.Children.Add(new TextBlock { Text = "الحجم تقديري وقد يختلف حسب محتوى الفيديو، خاصة في وضع الجودة الثابتة.", Foreground = Brushes.Gray, FontSize = 11, Margin = new Thickness(0, 4, 0, 0) });
            estimateCard.Child = estimatePanel;
            content.Children.Add(estimateCard);
            content.Children.Add(_error);

            var scroll = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Grid.SetRow(scroll, 1);
            root.Children.Add(scroll);

            var footer = new Border { Background = new SolidColorBrush(Color.FromRgb(15, 15, 17)), Padding = new Thickness(20, 12, 20, 12) };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
            var export = new Button { Content = "بدء التصدير", MinWidth = 150, Height = 38, Background = Accent, BorderBrush = Brushes.Orange, FontWeight = FontWeights.SemiBold };
            export.Click += Confirm;
            var cancel = new Button { Content = "إلغاء", MinWidth = 100, Height = 38, Margin = new Thickness(8, 0, 0, 0) };
            cancel.Click += (_, _) => DialogResult = false;
            buttons.Children.Add(export);
            buttons.Children.Add(cancel);
            footer.Child = buttons;
            Grid.SetRow(footer, 2);
            root.Children.Add(footer);
            return root;
        }

        private static Border Section(string title, (string Label, Control Control)[] fields)
        {
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 7) });
            foreach ((string label, Control control) in fields)
            {
                var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.Children.Add(Label(label));
                control.HorizontalAlignment = HorizontalAlignment.Stretch;
                control.MinHeight = 32;
                Grid.SetColumn(control, 1);
                row.Children.Add(control);
                panel.Children.Add(row);
            }
            return new Border { Background = Panel, CornerRadius = new CornerRadius(8), Padding = new Thickness(14), Margin = new Thickness(0, 0, 0, 10), Child = panel };
        }

        private static TextBlock Label(string text) => new() { Text = text, Foreground = Brushes.LightGray, VerticalAlignment = VerticalAlignment.Center };

        private void PopulateChoices()
        {
            Add(_fps, "24 FPS", "24"); Add(_fps, "25 FPS", "25"); Add(_fps, "30 FPS", "30"); Add(_fps, "50 FPS", "50"); Add(_fps, "60 FPS", "60"); Add(_fps, "120 FPS", "120");
            Add(_encoder, "H.264 — توافق ممتاز (CPU)", "libx264");
            Add(_encoder, "H.265 / HEVC — حجم أصغر (CPU)", "libx265");
            Add(_encoder, "NVIDIA H.264 — سريع", "h264_nvenc");
            Add(_encoder, "NVIDIA HEVC — سريع وحجم أصغر", "hevc_nvenc");
            Add(_encoder, "AMD H.264 — AMF", "h264_amf");
            Add(_encoder, "AMD HEVC — AMF وحجم أصغر", "hevc_amf");
            Add(_rateControl, "بت ريت محدد VBR", "bitrate"); Add(_rateControl, "جودة ثابتة CRF / CQ", "quality");
            Add(_preset, "سريع", "fast"); Add(_preset, "متوازن", "medium"); Add(_preset, "بطيء — جودة أعلى", "slow"); Add(_preset, "أفضل جودة", "quality");
            Add(_pixelFormat, "8-bit — yuv420p (الأكثر توافقًا)", "yuv420p"); Add(_pixelFormat, "10-bit — yuv420p10le (HEVC)", "yuv420p10le");
            Add(_gop, "كل ثانية", "1"); Add(_gop, "كل ثانيتين — موصى به", "2"); Add(_gop, "كل 4 ثوانٍ", "4");
            Add(_audio, "AAC — تضمين الصوت", "on"); Add(_audio, "بدون صوت", "off");
            Add(_audioBitrate, "128 kbps", "128"); Add(_audioBitrate, "192 kbps", "192"); Add(_audioBitrate, "256 kbps", "256"); Add(_audioBitrate, "320 kbps", "320");
            Add(_fastStart, "مفعّل — تشغيل أسرع على الويب", "on"); Add(_fastStart, "متوقف", "off");
        }

        private void LoadValues(EditorExportSettings settings)
        {
            _width.Text = settings.Width.ToString(CultureInfo.InvariantCulture);
            _height.Text = settings.Height.ToString(CultureInfo.InvariantCulture);
            Select(_fps, settings.FrameRate.ToString("0.###", CultureInfo.InvariantCulture));
            Select(_encoder, settings.VideoEncoder);
            Select(_rateControl, settings.RateControl);
            Select(_preset, settings.Preset);
            Select(_pixelFormat, settings.PixelFormat);
            _targetBitrate.Text = settings.VideoBitrateMbps.ToString("0.##", CultureInfo.InvariantCulture);
            _maxBitrate.Text = settings.MaxBitrateMbps.ToString("0.##", CultureInfo.InvariantCulture);
            _bufferSize.Text = settings.BufferSizeMbps.ToString("0.##", CultureInfo.InvariantCulture);
            _quality.Value = settings.Quality;
            Select(_gop, settings.KeyframeIntervalSeconds.ToString(CultureInfo.InvariantCulture));
            Select(_audio, settings.IncludeAudio ? "on" : "off");
            Select(_audioBitrate, settings.AudioBitrateKbps.ToString(CultureInfo.InvariantCulture));
            Select(_fastStart, settings.FastStart ? "on" : "off");
        }

        private void HookLiveUpdates()
        {
            foreach (TextBox box in new[] { _width, _height, _targetBitrate, _maxBitrate, _bufferSize }) box.TextChanged += (_, _) => UpdateEstimate();
            foreach (ComboBox combo in new[] { _fps, _encoder, _rateControl, _preset, _pixelFormat, _gop, _audio, _audioBitrate, _fastStart }) combo.SelectionChanged += (_, _) => { UpdateControlState(); UpdateEstimate(); };
            _quality.ValueChanged += (_, _) => { _qualityValue.Text = ((int)_quality.Value).ToString(CultureInfo.InvariantCulture); UpdateEstimate(); };
        }

        private void UpdateControlState()
        {
            bool bitrate = SelectedValue(_rateControl) == "bitrate";
            _targetBitrate.IsEnabled = bitrate;
            _maxBitrate.IsEnabled = bitrate;
            _bufferSize.IsEnabled = bitrate;
            _quality.IsEnabled = !bitrate;
            _qualityValue.Foreground = !bitrate ? Brushes.White : Brushes.Gray;
            _audioBitrate.IsEnabled = SelectedValue(_audio) == "on";
            if (SelectedValue(_encoder) is "libx264" or "h264_nvenc" or "h264_amf" && SelectedValue(_pixelFormat) == "yuv420p10le") Select(_pixelFormat, "yuv420p");
        }

        private void Apply4KPreset()
        {
            bool portrait = _height.Text.Trim().Length > 0 &&
                            int.TryParse(_height.Text, out int h) && int.TryParse(_width.Text, out int w) && h > w;
            _width.Text = (portrait ? 2160 : 3840).ToString(CultureInfo.InvariantCulture);
            _height.Text = (portrait ? 3840 : 2160).ToString(CultureInfo.InvariantCulture);
            Select(_fps, "60"); Select(_encoder, "hevc_nvenc"); Select(_rateControl, "bitrate");
            Select(_preset, "quality"); Select(_pixelFormat, "yuv420p");
            _targetBitrate.Text = "150"; _maxBitrate.Text = "200"; _bufferSize.Text = "400";
            _quality.Value = 12; Select(_gop, "1"); Select(_audioBitrate, "320"); Select(_fastStart, "on");
            UpdateControlState(); UpdateEstimate();
        }

        private void ApplyLosslessPreset()
        {
            Select(_encoder, "libx264"); Select(_rateControl, "quality"); Select(_preset, "quality");
            Select(_pixelFormat, "yuv420p"); _quality.Value = 0; Select(_fastStart, "on");
            UpdateControlState(); UpdateEstimate();
        }

        private void UpdateEstimate()
        {
            _qualityValue.Text = ((int)_quality.Value).ToString(CultureInfo.InvariantCulture);
            if (!TryRead(out EditorExportSettings? settings, showError: false) || settings == null)
            {
                _estimate.Text = "أدخل أرقامًا صحيحة لحساب الحجم.";
                return;
            }
            double mb = settings.EstimateSizeMegabytes(_duration);
            string size = mb >= 1024 ? $"{mb / 1024:0.00} GB" : $"{mb:0} MB";
            _estimate.Text = $"{settings.Width}×{settings.Height} • {settings.FrameRate:0.##} FPS • {size} • مدة {TimeSpan.FromSeconds(_duration):hh\\:mm\\:ss}";
        }

        private void Confirm(object sender, RoutedEventArgs e)
        {
            _error.Text = "";
            if (!TryRead(out EditorExportSettings? settings, showError: true) || settings == null) return;
            settings.Normalize();
            settings.Save();
            ResultSettings = settings;
            DialogResult = true;
        }

        private bool TryRead(out EditorExportSettings? settings, bool showError)
        {
            settings = null;
            if (!TryInt(_width.Text, out int width) || width is < 64 or > 7680 ||
                !TryInt(_height.Text, out int height) || height is < 64 or > 4320)
                return Fail("الدقة يجب أن تكون بين 64×64 و7680×4320.", showError);
            if (!TryDouble(_targetBitrate.Text, out double target) || target is < 0.5 or > 300 ||
                !TryDouble(_maxBitrate.Text, out double max) || max < target || max > 400 ||
                !TryDouble(_bufferSize.Text, out double buffer) || buffer < max || buffer > 800)
                return Fail("تحقق من البت ريت: Target من 0.5 إلى 300، وMaximum لا يقل عنه، وBuffer لا يقل عن Maximum.", showError);

            settings = new EditorExportSettings
            {
                Width = width, Height = height,
                FrameRate = ParseDouble(SelectedValue(_fps), 30),
                VideoEncoder = SelectedValue(_encoder), RateControl = SelectedValue(_rateControl), Preset = SelectedValue(_preset), PixelFormat = SelectedValue(_pixelFormat),
                VideoBitrateMbps = target, MaxBitrateMbps = max, BufferSizeMbps = buffer, Quality = (int)_quality.Value,
                KeyframeIntervalSeconds = (int)ParseDouble(SelectedValue(_gop), 2), IncludeAudio = SelectedValue(_audio) == "on",
                AudioBitrateKbps = (int)ParseDouble(SelectedValue(_audioBitrate), 192), FastStart = SelectedValue(_fastStart) == "on"
            };
            return true;
        }

        private bool Fail(string message, bool show)
        {
            if (show) _error.Text = message;
            return false;
        }

        private static bool TryInt(string value, out int result) => int.TryParse(value, NumberStyles.Integer, CultureInfo.CurrentCulture, out result) || int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
        private static bool TryDouble(string value, out double result) => double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out result) || double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
        private static double ParseDouble(string value, double fallback) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double result) ? result : fallback;

        private static void Add(ComboBox combo, string label, string value) => combo.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        private static string SelectedValue(ComboBox combo) => (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
        private static void Select(ComboBox combo, string value)
        {
            foreach (ComboBoxItem item in combo.Items)
                if (string.Equals(item.Tag?.ToString(), value, StringComparison.OrdinalIgnoreCase)) { combo.SelectedItem = item; return; }
            if (combo.Items.Count > 0) combo.SelectedIndex = 0;
        }

        private static TextBox Field() => new() { MinWidth = 260, Padding = new Thickness(9, 5, 9, 5), FlowDirection = FlowDirection.LeftToRight, TextAlignment = TextAlignment.Left };
        private static ComboBox Combo() => new() { MinWidth = 260, Padding = new Thickness(7, 4, 7, 4) };

        private static Style ButtonStyle()
        {
            var style = new Style(typeof(Button));
            style.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
            style.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(54, 54, 60))));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(82, 82, 90))));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(16, 7, 16, 7)));
            style.Setters.Add(new Setter(Control.CursorProperty, System.Windows.Input.Cursors.Hand));
            var template = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(presenter);
            template.VisualTree = border;
            style.Setters.Add(new Setter(Control.TemplateProperty, template));
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Control.OpacityProperty, 0.86));
            style.Triggers.Add(hover);
            return style;
        }

        private static Style TextBoxStyle()
        {
            var style = new Style(typeof(TextBox));
            style.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
            style.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(22, 22, 25))));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(76, 76, 84))));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
            style.Setters.Add(new Setter(TextBoxBase.CaretBrushProperty, Brushes.White));
            return style;
        }

    }
}
