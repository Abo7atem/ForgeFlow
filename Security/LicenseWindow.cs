using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ForgeFlow.Security
{
    public sealed class LicenseWindow : Window
    {
        private readonly LicenseManager _licenseManager;
        private readonly TextBox _keyBox;
        private readonly TextBlock _statusText;
        private const string StoreUrl = "https://4tiko.net";

        public LicenseWindow(LicenseManager licenseManager)
        {
            _licenseManager = licenseManager;
            Title = "ForgeFlow Activation";
            ForgeFlow.NativeWindowTheme.Apply(this);
            Icon = new BitmapImage(new Uri("pack://application:,,,/Assets/ForgeFlow.ico"));
            Width = 680;
            Height = 500;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            Background = new SolidColorBrush(Color.FromRgb(10, 10, 10));
            Foreground = new SolidColorBrush(Color.FromRgb(245, 245, 247));
            FontFamily = new FontFamily("Segoe UI");

            var root = new Grid
            {
                Margin = new Thickness(22),
                Background = new SolidColorBrush(Color.FromRgb(10, 10, 10))
            };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var header = new StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(new Image
            {
                Source = new BitmapImage(new Uri("pack://application:,,,/Assets/ForgeFlowMark.png")),
                Width = 88,
                Height = 54,
                Stretch = Stretch.Uniform,
                Margin = new Thickness(0, 0, 14, 0)
            });
            var headerText = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            headerText.Children.Add(new TextBlock
            {
                Text = "Activate or Buy ForgeFlow",
                FontSize = 22,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White
            });
            headerText.Children.Add(new TextBlock
            {
                Text = "Trial includes 3 full-feature processing attempts. Paste a license key or open the store to continue after trial.",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(160, 160, 166)),
                Margin = new Thickness(1, 4, 0, 0)
            });
            header.Children.Add(headerText);
            Grid.SetRow(header, 0);
            root.Children.Add(header);

            var infoBox = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(28, 28, 30)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(58, 58, 60)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12)
            };

            string token = HardwareIdHelper.GetLicenseHardwareToken();
            var machineRow = new Grid();
            machineRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            machineRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            machineRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            machineRow.Children.Add(new TextBlock
            {
                Text = "Machine ID",
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(209, 209, 214)),
                Margin = new Thickness(0, 0, 10, 0)
            });
            var machineBox = CreateDarkTextBox(token, true);
            machineBox.Height = 34;
            Grid.SetColumn(machineBox, 1);
            machineRow.Children.Add(machineBox);
            var copyButton = CreateDarkButton("Copy", 84);
            copyButton.Margin = new Thickness(10, 0, 0, 0);
            copyButton.Click += (_, _) => Clipboard.SetText(token);
            Grid.SetColumn(copyButton, 2);
            machineRow.Children.Add(copyButton);
            infoBox.Child = machineRow;
            Grid.SetRow(infoBox, 2);
            root.Children.Add(infoBox);

            _keyBox = CreateDarkTextBox("", false);
            _keyBox.Height = 190;
            _keyBox.MinHeight = 190;
            _keyBox.MaxHeight = 190;
            _keyBox.AcceptsReturn = true;
            _keyBox.AcceptsTab = false;
            _keyBox.TextWrapping = TextWrapping.Wrap;
            _keyBox.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            _keyBox.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            _keyBox.ToolTip = "Paste signed license key here";
            Grid.SetRow(_keyBox, 4);
            root.Children.Add(_keyBox);

            _statusText = new TextBlock
            {
                Text = _licenseManager.IsLicenseValid() ? "License is active." : $"Trial: {TrialManager.MaxTrialVideos} full-feature processing attempts. Activate a license to continue after trial.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(160, 160, 166))
            };
            Grid.SetRow(_statusText, 6);
            root.Children.Add(_statusText);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var activate = CreateDarkButton("Activate", 120);
            activate.Background = new SolidColorBrush(Color.FromRgb(10, 132, 255));
            activate.BorderBrush = new SolidColorBrush(Color.FromRgb(10, 132, 255));
            activate.Margin = new Thickness(0, 0, 8, 0);
            var buy = CreateDarkButton("Buy License", 120);
            buy.Margin = new Thickness(0, 0, 8, 0);
            var close = CreateDarkButton("Close", 96);
            activate.Click += Activate_Click;
            buy.Click += (_, _) => OpenStore();
            close.Click += (_, _) => Close();
            buttons.Children.Add(activate);
            buttons.Children.Add(buy);
            buttons.Children.Add(close);
            Grid.SetRow(buttons, 8);
            root.Children.Add(buttons);

            Content = root;
        }

        private static TextBox CreateDarkTextBox(string text, bool readOnly)
        {
            return new TextBox
            {
                Text = text,
                IsReadOnly = readOnly,
                Background = new SolidColorBrush(Color.FromRgb(28, 28, 30)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(58, 58, 60)),
                BorderThickness = new Thickness(1),
                CaretBrush = Brushes.White,
                SelectionBrush = new SolidColorBrush(Color.FromRgb(10, 132, 255)),
                Padding = new Thickness(8, 5, 8, 5),
                FontSize = 13
            };
        }

        private static Button CreateDarkButton(string text, double width)
        {
            return new Button
            {
                Content = text,
                Width = width,
                Height = 34,
                Background = new SolidColorBrush(Color.FromRgb(44, 44, 46)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(58, 58, 60)),
                BorderThickness = new Thickness(1),
                FontWeight = FontWeights.SemiBold,
                Cursor = System.Windows.Input.Cursors.Hand
            };
        }

        private void Activate_Click(object sender, RoutedEventArgs e)
        {
            var result = _licenseManager.ActivateLicense(_keyBox.Text);
            _statusText.Text = result.Message;
            MessageBox.Show(this, result.Message, result.Success ? "Activated" : "Activation failed",
                MessageBoxButton.OK, result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
            if (result.Success)
            {
                DialogResult = true;
                Close();
            }
        }

        private static void OpenStore()
        {
            Process.Start(new ProcessStartInfo(StoreUrl) { UseShellExecute = true });
        }
    }
}
