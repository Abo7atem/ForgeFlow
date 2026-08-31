using Microsoft.Win32;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ForgeFlow
{
    internal sealed class VideoEditWindow : Window
    {
        private readonly VideoEditOptions _options;
        private readonly TextBox _trimStart;
        private readonly TextBox _trimEnd;
        private readonly TextBox _deleteStart;
        private readonly TextBox _deleteEnd;
        private readonly TextBox _logo;
        private readonly TextBox _text;
        private readonly CheckBox _exportOnly;

        public VideoEditWindow(VideoEditOptions options)
        {
            _options = options;
            Title = "ForgeFlow — Edit Before Enhance";
            NativeWindowTheme.Apply(this);
            Icon = new BitmapImage(new Uri("pack://application:,,,/Assets/ForgeFlow.ico"));
            Width = 600; Height = 520; ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(Color.FromRgb(10, 10, 10)); Foreground = Brushes.White;

            var root = new Grid { Margin = new Thickness(22) };
            for (int i = 0; i < 8; i++) root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Content = root;
            root.Children.Add(new TextBlock { Text = "تحرير قبل التحسين", FontSize = 22, FontWeight = FontWeights.SemiBold });
            var hint = new TextBlock { Text = "أدخل الوقت بالثواني. اترك الحقل فارغًا لتعطيل ذلك الخيار.", Foreground = Brushes.Gray, Margin = new Thickness(0, 5, 0, 14) };
            Grid.SetRow(hint, 1); root.Children.Add(hint);

            _trimStart = AddPair(root, 2, "قص من البداية", options.TrimStartSeconds);
            _trimEnd = AddPair(root, 3, "الانتهاء عند", options.TrimEndSeconds);
            _deleteStart = AddPair(root, 4, "حذف مقطع: من", options.DeleteStartSeconds);
            _deleteEnd = AddPair(root, 5, "حذف مقطع: إلى", options.DeleteEndSeconds);

            var logoRow = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            logoRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            logoRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            logoRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
            logoRow.Children.Add(new TextBlock { Text = "شعار PNG", VerticalAlignment = VerticalAlignment.Center });
            _logo = new TextBox { Text = options.LogoPath, Margin = new Thickness(8, 0, 8, 0) }; Grid.SetColumn(_logo, 1); logoRow.Children.Add(_logo);
            var browse = new Button { Content = "اختيار" }; browse.Click += (_, _) => { var d = new OpenFileDialog { Filter = "PNG Image|*.png" }; if (d.ShowDialog() == true) _logo.Text = d.FileName; }; Grid.SetColumn(browse, 2); logoRow.Children.Add(browse);
            Grid.SetRow(logoRow, 6); root.Children.Add(logoRow);

            var textRow = new Grid { Margin = new Thickness(0, 8, 0, 0) }; textRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) }); textRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            textRow.Children.Add(new TextBlock { Text = "نص على الفيديو", VerticalAlignment = VerticalAlignment.Center });
            _text = new TextBox { Text = options.OverlayText, Margin = new Thickness(8, 0, 0, 0) }; Grid.SetColumn(_text, 1); textRow.Children.Add(_text);
            Grid.SetRow(textRow, 7); root.Children.Add(textRow);

            _exportOnly = new CheckBox { Content = "Export Edit Only — حفظ التعديل فقط بدون تحسين", IsChecked = options.ExportEditOnly, Margin = new Thickness(0, 16, 0, 0), Foreground = Brushes.White };
            Grid.SetRow(_exportOnly, 8); root.Children.Add(_exportOnly);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var cancel = new Button { Content = "إلغاء", Width = 100, Height = 34, Margin = new Thickness(8, 0, 0, 0) }; cancel.Click += (_, _) => Close();
            var save = new Button { Content = "تطبيق", Width = 100, Height = 34, Margin = new Thickness(8, 0, 0, 0), Background = new SolidColorBrush(Color.FromRgb(10,132,255)) }; save.Click += Save;
            buttons.Children.Add(cancel); buttons.Children.Add(save); Grid.SetRow(buttons, 9); root.Children.Add(buttons);
        }

        private static TextBox AddPair(Grid root, int row, string label, double? value)
        {
            var panel = new Grid { Margin = new Thickness(0, 4, 0, 0) }; panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) }); panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            panel.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
            var box = new TextBox { Text = value?.ToString(CultureInfo.InvariantCulture) ?? "", Margin = new Thickness(8, 0, 0, 0) }; Grid.SetColumn(box, 1); panel.Children.Add(box); Grid.SetRow(panel, row); root.Children.Add(panel); return box;
        }

        private void Save(object sender, RoutedEventArgs e)
        {
            if (!TrySeconds(_trimStart, out var trimStart) || !TrySeconds(_trimEnd, out var trimEnd) || !TrySeconds(_deleteStart, out var deleteStart) || !TrySeconds(_deleteEnd, out var deleteEnd) ||
                (trimStart.HasValue && trimEnd.HasValue && trimEnd <= trimStart) || (deleteStart.HasValue && deleteEnd.HasValue && deleteEnd <= deleteStart))
            { MessageBox.Show(this, "تحقق من أوقات القص والحذف.", "Edit", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            _options.TrimStartSeconds = trimStart; _options.TrimEndSeconds = trimEnd; _options.DeleteStartSeconds = deleteStart; _options.DeleteEndSeconds = deleteEnd;
            _options.LogoPath = _logo.Text.Trim(); _options.OverlayText = _text.Text.Trim(); _options.ExportEditOnly = _exportOnly.IsChecked == true;
            DialogResult = true;
        }

        private static bool TrySeconds(TextBox box, out double? value)
        { value = null; if (string.IsNullOrWhiteSpace(box.Text)) return true; if (!double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) || seconds < 0) return false; value = seconds; return true; }
    }
}
