using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ForgeFlow
{
    public sealed class SupportWindow : Window
    {
        public SupportWindow(string planStatus)
        {
            Title = "Support ForgeFlow";
            NativeWindowTheme.Apply(this);
            Icon = new BitmapImage(new Uri("pack://application:,,,/Assets/ForgeFlow.ico"));
            Width = 560;
            Height = 430;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(Color.FromRgb(10, 10, 10));
            Foreground = Brushes.White;
            FontFamily = new FontFamily("Segoe UI");

            var root = new StackPanel
            {
                Margin = new Thickness(24)
            };

            root.Children.Add(new Image
            {
                Source = new BitmapImage(new Uri("pack://application:,,,/Assets/ForgeFlowMark.png")),
                Width = 128,
                Height = 78,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Left
            });

            root.Children.Add(new TextBlock
            {
                Text = "Support the Project",
                FontSize = 24,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 14, 0, 0),
                Foreground = Brushes.White
            });

            root.Children.Add(new TextBlock
            {
                Text = "ForgeFlow includes 3 full-feature trial attempts. Activate a license for unlimited processing after trial.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(209, 209, 214)),
                Margin = new Thickness(0, 10, 0, 0)
            });

            root.Children.Add(CreateCard(
                "Current plan",
                planStatus,
                "Trial includes 3 full-feature processing attempts. License activation removes the attempt limit."));

            root.Children.Add(CreateCard(
                "Why support",
                "Your support funds updates, bundled tools, model maintenance, and faster feature delivery.",
                "This page can later host a light sponsor message or partner placement without interrupting processing."));

            var buttons = new WrapPanel
            {
                Margin = new Thickness(0, 18, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                Orientation = Orientation.Horizontal
            };

            buttons.Children.Add(CreateActionButton("Buy License", "https://4tiko.net"));
            buttons.Children.Add(CreateActionButton("Support Project", "https://4tiko.net"));
            buttons.Children.Add(CreateActionButton("YouTube", "https://youtube.com/@bo_7atemm"));
            buttons.Children.Add(CreateActionButton("Discord", "https://discord.gg/jF5uNEyg8r"));

            root.Children.Add(buttons);

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

        private static Border CreateCard(string title, string lineOne, string lineTwo)
        {
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White
            });
            stack.Children.Add(new TextBlock
            {
                Text = lineOne,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(209, 209, 214)),
                Margin = new Thickness(0, 8, 0, 0)
            });
            stack.Children.Add(new TextBlock
            {
                Text = lineTwo,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(160, 160, 166)),
                Margin = new Thickness(0, 8, 0, 0)
            });

            return new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(28, 28, 30)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(58, 58, 60)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14),
                Margin = new Thickness(0, 16, 0, 0),
                Child = stack
            };
        }

        private static Button CreateActionButton(string text, string url)
        {
            var button = new Button
            {
                Content = text,
                MinWidth = 118,
                Height = 34,
                Margin = new Thickness(0, 0, 8, 8),
                Padding = new Thickness(12, 0, 12, 0),
                Background = new SolidColorBrush(Color.FromRgb(44, 44, 46)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(58, 58, 60)),
                BorderThickness = new Thickness(1),
                FontWeight = FontWeights.SemiBold,
                Cursor = System.Windows.Input.Cursors.Hand
            };

            button.Click += (_, _) => OpenUrl(url);
            return button;
        }

        private static void OpenUrl(string url)
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
    }
}
