using System.Windows;
using System.Windows.Controls;

namespace ForgeFlow.Security
{
    public sealed class LicenseWindow : Window
    {
        private readonly LicenseManager _licenseManager;
        private readonly TextBox _keyBox;
        private readonly TextBlock _statusText;

        public LicenseWindow(LicenseManager licenseManager)
        {
            _licenseManager = licenseManager;
            Title = "ForgeFlow Activation";
            Width = 560;
            Height = 300;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;

            var root = new Grid { Margin = new Thickness(18) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var title = new TextBlock
            {
                Text = "Activate ForgeFlow Lite",
                FontSize = 20,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 14)
            };
            Grid.SetRow(title, 0);
            root.Children.Add(title);

            string token = HardwareIdHelper.GetLicenseHardwareToken();
            var machineRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
            machineRow.Children.Add(new TextBlock { Text = "Machine ID: ", VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold });
            machineRow.Children.Add(new TextBox { Text = token, IsReadOnly = true, Width = 210, Margin = new Thickness(4, 0, 8, 0) });
            var copyButton = new Button { Content = "Copy", Width = 80 };
            copyButton.Click += (_, _) => Clipboard.SetText(token);
            machineRow.Children.Add(copyButton);
            Grid.SetRow(machineRow, 1);
            root.Children.Add(machineRow);

            _keyBox = new TextBox
            {
                MinHeight = 32,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12),
                ToolTip = "Paste signed license key here"
            };
            Grid.SetRow(_keyBox, 2);
            root.Children.Add(_keyBox);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var activate = new Button { Content = "Activate", Width = 110, Height = 32, Margin = new Thickness(0, 0, 8, 0) };
            var close = new Button { Content = "Close", Width = 90, Height = 32 };
            activate.Click += Activate_Click;
            close.Click += (_, _) => Close();
            buttons.Children.Add(activate);
            buttons.Children.Add(close);
            Grid.SetRow(buttons, 3);
            root.Children.Add(buttons);

            _statusText = new TextBlock
            {
                Text = _licenseManager.IsLicenseValid() ? "License is active." : "Trial mode: 5 videos before activation.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 16, 0, 0)
            };
            Grid.SetRow(_statusText, 4);
            root.Children.Add(_statusText);

            Content = root;
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
    }
}
