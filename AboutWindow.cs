using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ForgeFlow
{
    public sealed class AboutWindow : Window
    {
        public AboutWindow(string licenseStatus)
        {
            Title = "About ForgeFlow";
            Icon = new BitmapImage(new Uri("pack://application:,,,/Assets/ForgeFlow.ico"));
            Width = 480;
            Height = 360;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(Color.FromRgb(10, 10, 10));
            Foreground = Brushes.White;
            FontFamily = new FontFamily("Segoe UI");

            string version = Assembly.GetExecutingAssembly().GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "1.0.0";
            var root = new StackPanel { Margin = new Thickness(24) };
            root.Children.Add(new Image
            {
                Source = new BitmapImage(new Uri("pack://application:,,,/Assets/ForgeFlowMark.png")),
                Width = 128,
                Height = 78,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Left
            });
            root.Children.Add(new TextBlock { Text = "ForgeFlow Lite", FontSize = 24, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 0) });
            root.Children.Add(CreateLine("Version", version));
            root.Children.Add(CreateLine("License", licenseStatus));
            root.Children.Add(CreateLine("Outputs", "Video 60FPS + TikTok30FPS"));
            root.Children.Add(CreateLine("Website", "4tiko.net"));
            root.Children.Add(new TextBlock
            {
                Text = "جميع الحقوق محفوظة A.Hatem T-Zone SYR",
                Foreground = new SolidColorBrush(Color.FromRgb(160, 160, 166)),
                Margin = new Thickness(0, 18, 0, 0)
            });

            var close = new Button
            {
                Content = "Close",
                Width = 110,
                Height = 34,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 24, 0, 0),
                Background = new SolidColorBrush(Color.FromRgb(44, 44, 46)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(58, 58, 60)),
                BorderThickness = new Thickness(1)
            };
            close.Click += (_, _) => Close();
            root.Children.Add(close);
            Content = root;
        }

        private static TextBlock CreateLine(string label, string value) => new()
        {
            Text = $"{label}: {value}",
            Foreground = new SolidColorBrush(Color.FromRgb(209, 209, 214)),
            Margin = new Thickness(0, 8, 0, 0)
        };
    }
}
