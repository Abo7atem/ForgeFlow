using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ForgeFlow
{
    public sealed class WelcomeWindow : Window
    {
        public bool OpenUpgrade { get; private set; }

        public WelcomeWindow()
        {
            Title = "Welcome to ForgeFlow";
            NativeWindowTheme.Apply(this);
            Icon = new BitmapImage(new Uri("pack://application:,,,/Assets/ForgeFlow.ico"));
            Width = 560;
            Height = 420;
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
                Width = 132,
                Height = 80,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Left
            });

            root.Children.Add(new TextBlock
            {
                Text = "Welcome to ForgeFlow",
                FontSize = 24,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 16, 0, 0),
                Foreground = Brushes.White
            });

            root.Children.Add(new TextBlock
            {
                Text = "Try ForgeFlow with 3 processing attempts. After that, activation is required to continue and unlock Pro features.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(209, 209, 214)),
                Margin = new Thickness(0, 10, 0, 0)
            });

            root.Children.Add(CreateCard(
                "Trial",
                "3 processing attempts only",
                "After the trial, activate a license to continue."));

            root.Children.Add(CreateCard(
                "Pro",
                "Unlimited processing, NVIDIA/AMD, Quality Boost, and advanced pipeline options",
                "Best for production work and faster delivery."));

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 24, 0, 0)
            };

            var continueTrial = CreateButton("Continue Trial", false);
            continueTrial.Margin = new Thickness(0, 0, 8, 0);
            continueTrial.Click += (_, _) =>
            {
                OpenUpgrade = false;
                DialogResult = true;
                Close();
            };

            var upgrade = CreateButton("Activate Pro", true);
            upgrade.Click += (_, _) =>
            {
                OpenUpgrade = true;
                DialogResult = true;
                Close();
            };

            buttons.Children.Add(continueTrial);
            buttons.Children.Add(upgrade);
            root.Children.Add(buttons);

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

        private static Button CreateButton(string text, bool primary)
        {
            return new Button
            {
                Content = text,
                Width = 148,
                Height = 36,
                Background = primary ? new SolidColorBrush(Color.FromRgb(10, 132, 255)) : new SolidColorBrush(Color.FromRgb(44, 44, 46)),
                Foreground = Brushes.White,
                BorderBrush = primary ? new SolidColorBrush(Color.FromRgb(10, 132, 255)) : new SolidColorBrush(Color.FromRgb(58, 58, 60)),
                BorderThickness = new Thickness(1),
                FontWeight = FontWeights.SemiBold,
                Cursor = System.Windows.Input.Cursors.Hand
            };
        }
    }
}
