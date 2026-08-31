using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ForgeFlow
{
    /// <summary>Plays the selected source alongside a finished ForgeFlow export.</summary>
    public sealed class ComparePreviewWindow : Window
    {
        private readonly MediaElement _sourcePlayer;
        private readonly MediaElement _outputPlayer;
        private readonly Slider _timeline;
        private readonly TextBlock _timeText;
        private readonly TextBlock _outputStatus;
        private readonly Button _playButton;
        private readonly Button _muteButton;
        private readonly DispatcherTimer _timer;
        private bool _isPlaying;
        private bool _isSeeking;
        private bool _isMuted;

        public ComparePreviewWindow(string sourcePath, string? outputPath)
        {
            Title = "ForgeFlow Preview — Before / After";
            NativeWindowTheme.Apply(this);
            Icon = new BitmapImage(new Uri("pack://application:,,,/Assets/ForgeFlow.ico"));
            Width = 1120;
            Height = 760;
            MinWidth = 850;
            MinHeight = 560;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(Color.FromRgb(10, 10, 10));
            Foreground = Brushes.White;
            FontFamily = new FontFamily("Segoe UI");

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _timer.Tick += (_, _) => RefreshTimeline();

            var root = new Grid { Margin = new Thickness(18) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Content = root;

            var title = new StackPanel { Orientation = Orientation.Horizontal };
            title.Children.Add(new Image
            {
                Source = new BitmapImage(new Uri("pack://application:,,,/Assets/ForgeFlowMark.png")),
                Width = 52,
                Height = 34,
                Stretch = Stretch.Uniform,
                Margin = new Thickness(0, 0, 10, 0)
            });
            var titleCopy = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            titleCopy.Children.Add(new TextBlock { Text = "معاينة قبل / بعد", FontSize = 20, FontWeight = FontWeights.SemiBold });
            titleCopy.Children.Add(new TextBlock
            {
                Text = "الأصلي والنتيجة يعملان بالتزامن للمقارنة الدقيقة.",
                Foreground = new SolidColorBrush(Color.FromRgb(160, 160, 166)),
                FontSize = 12
            });
            title.Children.Add(titleCopy);
            root.Children.Add(title);

            var previewBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(5, 5, 5)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(58, 58, 60)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10)
            };
            Grid.SetRow(previewBorder, 2);
            root.Children.Add(previewBorder);

            var previewGrid = new Grid();
            previewGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            previewGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
            previewGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            previewBorder.Child = previewGrid;

            _sourcePlayer = CreatePlayer();
            _outputPlayer = CreatePlayer();
            _sourcePlayer.Source = new Uri(sourcePath);
            _sourcePlayer.MediaOpened += (_, _) => RefreshTimeline();
            _sourcePlayer.MediaEnded += (_, _) => StopPlayback();
            _outputPlayer.MediaOpened += (_, _) => SyncOutputToSource();

            AddVideoPanel(previewGrid, 0, "قبل — الأصلي", Path.GetFileName(sourcePath), _sourcePlayer, "سيظهر الفيديو الأصلي هنا");
            var divider = new Border { Background = new SolidColorBrush(Color.FromRgb(58, 58, 60)), Margin = new Thickness(6, 0, 6, 0) };
            Grid.SetColumn(divider, 1);
            previewGrid.Children.Add(divider);
            _outputStatus = AddVideoPanel(previewGrid, 2, "بعد — المحسّن", outputPath == null ? "بانتظار النتيجة" : Path.GetFileName(outputPath), _outputPlayer, "ستظهر النتيجة بعد اكتمال المعالجة");

            if (outputPath != null && File.Exists(outputPath))
            {
                _outputPlayer.Source = new Uri(outputPath);
            }
            else
            {
                _outputStatus.Foreground = new SolidColorBrush(Color.FromRgb(255, 159, 10));
            }

            _timeline = new Slider
            {
                Minimum = 0,
                Maximum = 100,
                IsMoveToPointEnabled = true,
                Margin = new Thickness(8, 12, 8, 0),
                IsEnabled = false
            };
            _timeline.ValueChanged += Timeline_ValueChanged;
            _timeline.AddHandler(System.Windows.Controls.Primitives.Thumb.DragStartedEvent, new System.Windows.Controls.Primitives.DragStartedEventHandler((_, _) => _isSeeking = true));
            _timeline.AddHandler(System.Windows.Controls.Primitives.Thumb.DragCompletedEvent, new System.Windows.Controls.Primitives.DragCompletedEventHandler((_, _) => { _isSeeking = false; SeekToTimeline(); }));
            Grid.SetRow(_timeline, 3);
            root.Children.Add(_timeline);

            var controls = new Grid { Margin = new Thickness(8, 8, 8, 0) };
            controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetRow(controls, 4);
            root.Children.Add(controls);

            _playButton = CreateButton("▶ تشغيل", (_, _) => TogglePlayback(), true);
            controls.Children.Add(_playButton);
            var stop = CreateButton("■ إيقاف", (_, _) => StopPlayback());
            Grid.SetColumn(stop, 1);
            controls.Children.Add(stop);
            _muteButton = CreateButton("🔊 الصوت", (_, _) => ToggleMute());
            Grid.SetColumn(_muteButton, 2);
            controls.Children.Add(_muteButton);
            _timeText = new TextBlock
            {
                Text = "0:00 / 0:00",
                Foreground = new SolidColorBrush(Color.FromRgb(160, 160, 166)),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                FontFamily = new FontFamily("Consolas")
            };
            Grid.SetColumn(_timeText, 3);
            controls.Children.Add(_timeText);
            var close = CreateButton("إغلاق", (_, _) => Close());
            Grid.SetColumn(close, 4);
            controls.Children.Add(close);

            Closed += (_, _) =>
            {
                _timer.Stop();
                _sourcePlayer.Stop();
                _outputPlayer.Stop();
            };
        }

        private static MediaElement CreatePlayer() => new()
        {
            LoadedBehavior = MediaState.Manual,
            UnloadedBehavior = MediaState.Stop,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsMuted = true,
            SnapsToDevicePixels = true
        };

        private TextBlock AddVideoPanel(Grid root, int column, string heading, string status, MediaElement player, string placeholder)
        {
            var panel = new Grid();
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(panel, column);
            root.Children.Add(panel);

            var header = new Grid { Margin = new Thickness(4, 0, 4, 8) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.Children.Add(new TextBlock { Text = heading, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(209, 209, 214)) });
            var statusText = new TextBlock { Text = status, FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(142, 142, 147)), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(statusText, 1);
            header.Children.Add(statusText);
            panel.Children.Add(header);

            var content = new Grid { Background = Brushes.Black };
            Grid.SetRow(content, 1);
            panel.Children.Add(content);
            content.Children.Add(new TextBlock
            {
                Text = placeholder,
                Foreground = new SolidColorBrush(Color.FromRgb(100, 100, 105)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });
            content.Children.Add(player);
            return statusText;
        }

        private static Button CreateButton(string text, RoutedEventHandler click, bool primary = false)
        {
            var button = new Button
            {
                Content = text,
                Height = 32,
                MinWidth = 88,
                Margin = new Thickness(0, 0, 7, 0),
                Padding = new Thickness(10, 0, 10, 0),
                Background = new SolidColorBrush(primary ? Color.FromRgb(10, 132, 255) : Color.FromRgb(44, 44, 46)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(primary ? Color.FromRgb(10, 132, 255) : Color.FromRgb(58, 58, 60)),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            button.Click += click;
            return button;
        }

        private void TogglePlayback()
        {
            if (_isPlaying)
            {
                _sourcePlayer.Pause();
                _outputPlayer.Pause();
                _timer.Stop();
                _isPlaying = false;
                _playButton.Content = "▶ تشغيل";
                return;
            }

            _sourcePlayer.Play();
            _outputPlayer.Play();
            _timer.Start();
            _isPlaying = true;
            _playButton.Content = "❚❚ إيقاف مؤقت";
        }

        private void StopPlayback()
        {
            _sourcePlayer.Stop();
            _outputPlayer.Stop();
            _timer.Stop();
            _timeline.Value = 0;
            _timeText.Text = "0:00 / 0:00";
            _isPlaying = false;
            _playButton.Content = "▶ تشغيل";
        }

        private void ToggleMute()
        {
            _isMuted = !_isMuted;
            _sourcePlayer.IsMuted = true;
            _outputPlayer.IsMuted = _isMuted;
            _muteButton.Content = _isMuted ? "🔇 مكتوم" : "🔊 الصوت";
        }

        private void RefreshTimeline()
        {
            if (!_sourcePlayer.NaturalDuration.HasTimeSpan || _sourcePlayer.NaturalDuration.TimeSpan.TotalSeconds <= 0 || _isSeeking)
                return;
            TimeSpan duration = _sourcePlayer.NaturalDuration.TimeSpan;
            _timeline.IsEnabled = true;
            _timeline.Value = Math.Clamp(_sourcePlayer.Position.TotalSeconds / duration.TotalSeconds * 100, 0, 100);
            _timeText.Text = $"{FormatTime(_sourcePlayer.Position)} / {FormatTime(duration)}";
            SyncOutputToSource();
        }

        private void Timeline_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isSeeking)
                return;
        }

        private void SeekToTimeline()
        {
            if (!_sourcePlayer.NaturalDuration.HasTimeSpan)
                return;
            TimeSpan position = TimeSpan.FromSeconds(_sourcePlayer.NaturalDuration.TimeSpan.TotalSeconds * _timeline.Value / 100);
            _sourcePlayer.Position = position;
            if (_outputPlayer.NaturalDuration.HasTimeSpan)
                _outputPlayer.Position = TimeSpan.FromSeconds(Math.Min(position.TotalSeconds, _outputPlayer.NaturalDuration.TimeSpan.TotalSeconds));
        }

        private void SyncOutputToSource()
        {
            if (_isSeeking || !_sourcePlayer.NaturalDuration.HasTimeSpan || !_outputPlayer.NaturalDuration.HasTimeSpan)
                return;
            double drift = Math.Abs((_outputPlayer.Position - _sourcePlayer.Position).TotalMilliseconds);
            if (drift > 350)
                _outputPlayer.Position = TimeSpan.FromSeconds(Math.Min(_sourcePlayer.Position.TotalSeconds, _outputPlayer.NaturalDuration.TimeSpan.TotalSeconds));
        }

        private static string FormatTime(TimeSpan value) => value.TotalHours >= 1
            ? value.ToString(@"h\:mm\:ss", System.Globalization.CultureInfo.InvariantCulture)
            : value.ToString(@"m\:ss", System.Globalization.CultureInfo.InvariantCulture);
    }
}
