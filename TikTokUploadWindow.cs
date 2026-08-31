using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ForgeFlow
{
    internal sealed class TikTokUploadWindow : Window
    {
        private readonly TextBox _videoPath = CreateTextBox();
        private readonly TextBox _caption = CreateTextBox();
        private readonly TextBlock _status = new();
        private readonly ProgressBar _progress = new();
        private readonly Button _uploadButton;
        private readonly Button _openChromeButton;
        private readonly Button _cancelButton;
        private CancellationTokenSource? _cts;

        public TikTokUploadWindow(string? suggestedVideo)
        {
            Title = "ForgeFlow • TikTok Chrome Upload";
            NativeWindowTheme.Apply(this);
            Icon = new BitmapImage(new Uri("pack://application:,,,/Assets/ForgeFlow.ico"));
            Width = 780;
            Height = 540;
            MinWidth = 680;
            MinHeight = 500;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = Brush("#0A0A0A");
            Foreground = Brushes.White;
            FontFamily = new FontFamily("Segoe UI");

            _videoPath.Text = File.Exists(suggestedVideo) ? suggestedVideo! : "";
            _caption.AcceptsReturn = true;
            _caption.TextWrapping = TextWrapping.Wrap;
            _caption.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            _caption.Height = 100;
            _caption.VerticalContentAlignment = VerticalAlignment.Top;
            _caption.ToolTip = "الوصف الذي سيُكتب في TikTok Studio. يمكنك مراجعته داخل Chrome قبل النشر.";

            _status.Text = "جاهز. سيستخدم ForgeFlow ملف Chrome مستقلًا ويحفظ تسجيل دخول TikTok محليًا.";
            _status.Foreground = Brush("#A1A1AA");
            _status.TextWrapping = TextWrapping.Wrap;
            _status.FlowDirection = FlowDirection.RightToLeft;
            _progress.Height = 5;
            _progress.IsIndeterminate = false;
            _progress.Background = Brush("#27272A");
            _progress.Foreground = Brush("#25F4EE");

            _uploadButton = CreateButton("رفع عبر Chrome", "#FE2C55", "#FE2C55");
            _uploadButton.Click += UploadButton_Click;
            _openChromeButton = CreateButton("تسجيل الدخول", "#0F766E", "#14B8A6");
            _openChromeButton.Click += OpenChromeButton_Click;
            _cancelButton = CreateButton("إلغاء", "#27272A", "#3F3F46");
            _cancelButton.IsEnabled = false;
            _cancelButton.Click += (_, _) => _cts?.Cancel();
            Button browse = CreateButton("اختيار فيديو", "#27272A", "#3F3F46");
            browse.Click += Browse_Click;
            Button close = CreateButton("إغلاق", "#27272A", "#3F3F46");
            close.Click += (_, _) => Close();

            var root = new Grid { Margin = new Thickness(24) };
            for (int i = 0; i < 8; i++)
                root.RowDefinitions.Add(new RowDefinition { Height = i == 5 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });

            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.Children.Add(new Border
            {
                Width = 54,
                Height = 54,
                CornerRadius = new CornerRadius(14),
                Background = Brush("#18181B"),
                Child = new Image
                {
                    Source = new BitmapImage(new Uri("pack://application:,,,/Assets/ForgeFlowMark.png")),
                    Stretch = Stretch.Uniform,
                    Margin = new Thickness(5)
                }
            });
            var title = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            title.Children.Add(new TextBlock { Text = "TikTok Chrome Upload", FontSize = 23, FontWeight = FontWeights.SemiBold });
            title.Children.Add(new TextBlock { Text = "رفع حقيقي داخل TikTok Studio • بدون كلمات مرور أو أدوات خارجية", Foreground = Brush("#A1A1AA"), Margin = new Thickness(0, 4, 0, 0) });
            Grid.SetColumn(title, 1);
            header.Children.Add(title);
            root.Children.Add(header);

            var videoLabel = Label("الفيديو", "اختر TikTok 60 Ultra للحصول على أفضل نتيجة");
            Grid.SetRow(videoLabel, 1);
            videoLabel.Margin = new Thickness(0, 22, 0, 7);
            root.Children.Add(videoLabel);

            var pathRow = new Grid();
            pathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            pathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            pathRow.Children.Add(_videoPath);
            Grid.SetColumn(browse, 1);
            browse.Margin = new Thickness(10, 0, 0, 0);
            pathRow.Children.Add(browse);
            Grid.SetRow(pathRow, 2);
            root.Children.Add(pathRow);

            var captionLabel = Label("الوصف", "اختياري — سيُكتب تلقائيًا ويمكن تعديله قبل النشر");
            Grid.SetRow(captionLabel, 3);
            captionLabel.Margin = new Thickness(0, 18, 0, 7);
            root.Children.Add(captionLabel);
            Grid.SetRow(_caption, 4);
            root.Children.Add(_caption);

            var info = new Border
            {
                Background = Brush("#121216"),
                BorderBrush = Brush("#2C2C32"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(15),
                Margin = new Thickness(0, 18, 0, 14),
                Child = new TextBlock
                {
                    Text = "أول مرة: اضغط «تسجيل الدخول»، سجّل دخول TikTok ثم أغلق Chrome بالكامل. ارفع ملف TikTok60Ultra وانشره Private أولًا حتى تنتهي المعالجة، ثم حوّله إلى عام. لا تضف صوتًا من مكتبة TikTok لأن ذلك يعيد ترميز الفيديو؛ استخدم صوت المقطع الأصلي. قد تبدأ المعاينة 720p ثم ترتفع بعد اكتمال المعالجة.",
                    Foreground = Brush("#D4D4D8"),
                    TextWrapping = TextWrapping.Wrap,
                    FlowDirection = FlowDirection.RightToLeft,
                    LineHeight = 23
                }
            };
            Grid.SetRow(info, 5);
            root.Children.Add(info);

            var state = new StackPanel();
            state.Children.Add(_status);
            _progress.Margin = new Thickness(0, 9, 0, 0);
            state.Children.Add(_progress);
            Grid.SetRow(state, 6);
            root.Children.Add(state);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
            actions.Children.Add(close);
            _cancelButton.Margin = new Thickness(8, 0, 0, 0);
            actions.Children.Add(_cancelButton);
            _openChromeButton.Margin = new Thickness(8, 0, 0, 0);
            actions.Children.Add(_openChromeButton);
            _uploadButton.Margin = new Thickness(8, 0, 0, 0);
            actions.Children.Add(_uploadButton);
            Grid.SetRow(actions, 7);
            root.Children.Add(actions);

            Content = root;
            Closed += (_, _) => _cts?.Cancel();
        }

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "TikTok video|*.mp4;*.webm|Video files|*.mp4;*.mov;*.mkv;*.webm|All files|*.*",
                CheckFileExists = true
            };
            if (dialog.ShowDialog(this) == true)
                _videoPath.Text = dialog.FileName;
        }

        private async void UploadButton_Click(object sender, RoutedEventArgs e)
        {
            string path = _videoPath.Text.Trim();
            if (!File.Exists(path))
            {
                SetStatus("اختر ملف فيديو موجودًا أولًا.", error: true);
                return;
            }

            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            SetBusy(true);
            var uploader = new TikTokChromeUploader();
            uploader.StatusChanged += message => Dispatcher.Invoke(() => SetStatus(message));
            try
            {
                TikTokUploadResult result = await uploader.UploadAsync(path, _caption.Text.Trim(), _cts.Token);
                SetStatus(result.Message, error: !result.Success, success: result.Success);
            }
            catch (OperationCanceledException)
            {
                SetStatus("تم إلغاء انتظار الرفع. ستبقى نافذة Chrome مفتوحة.");
            }
            catch (Exception ex)
            {
                SetStatus("تعذر بدء الرفع: " + ex.Message, error: true);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async void OpenChromeButton_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            SetBusy(true);
            var uploader = new TikTokChromeUploader();
            uploader.StatusChanged += message => Dispatcher.Invoke(() => SetStatus(message));
            try
            {
                TikTokUploadResult result = await uploader.OpenStudioAsync(_cts.Token);
                SetStatus(result.Message, error: !result.Success, success: result.Success);
            }
            catch (OperationCanceledException)
            {
                SetStatus("تم إلغاء فتح Chrome.");
            }
            catch (Exception ex)
            {
                SetStatus("تعذر فتح Chrome: " + ex.Message, error: true);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void SetBusy(bool busy)
        {
            _uploadButton.IsEnabled = !busy;
            _openChromeButton.IsEnabled = !busy;
            _cancelButton.IsEnabled = busy;
            _progress.IsIndeterminate = busy;
            _videoPath.IsEnabled = !busy;
        }

        private void SetStatus(string message, bool error = false, bool success = false)
        {
            _status.Text = message;
            _status.Foreground = Brush(error ? "#FF6B6B" : success ? "#30D158" : "#A1A1AA");
        }

        private static StackPanel Label(string title, string hint)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(new TextBlock { Text = title, Foreground = Brushes.White, FontWeight = FontWeights.SemiBold });
            panel.Children.Add(new TextBlock { Text = "  •  " + hint, Foreground = Brush("#71717A"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
            return panel;
        }

        private static TextBox CreateTextBox() => new()
        {
            Height = 38,
            Background = Brush("#18181B"),
            Foreground = Brush("#FAFAFA"),
            CaretBrush = Brushes.White,
            SelectionBrush = Brush("#FE2C55"),
            BorderBrush = Brush("#3F3F46"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(11, 7, 11, 7),
            FontSize = 13,
            VerticalContentAlignment = VerticalAlignment.Center
        };

        private static Button CreateButton(string text, string background, string border)
        {
            var button = new Button
            {
                Content = text,
                Height = 38,
                MinWidth = 112,
                Padding = new Thickness(16, 0, 16, 0),
                Background = Brush(background),
                BorderBrush = Brush(border),
                BorderThickness = new Thickness(1),
                Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold,
                Cursor = Cursors.Hand
            };

            var borderFactory = new FrameworkElementFactory(typeof(Border));
            borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
            borderFactory.SetBinding(Border.BackgroundProperty, new Binding("Background") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
            borderFactory.SetBinding(Border.BorderBrushProperty, new Binding("BorderBrush") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
            borderFactory.SetBinding(Border.BorderThicknessProperty, new Binding("BorderThickness") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            borderFactory.AppendChild(presenter);
            button.Template = new ControlTemplate(typeof(Button)) { VisualTree = borderFactory };
            return button;
        }

        private static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
    }
}
