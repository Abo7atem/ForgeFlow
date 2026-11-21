using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ForgeFlow
{
    public sealed class WelcomeWindow : Window
    {
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
                Text = "ForgeFlow is free and open source. Use every feature without an account, activation, or usage limit.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(209, 209, 214)),
                Margin = new Thickness(0, 10, 0, 0)
            });

            root.Children.Add(CreateCard(
                "Free and open source",
                "Unlimited video processing",
                "No account or activation required. The MIT license lets everyone use, modify, and share the project."));

            root.Children.Add(CreateCard(
                "Help the project grow",
                "Use it, improve it, and share it",
                "Keep the copyright and license notice with copies of the project."));

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 24, 0, 0)
            };

            var continueButton = CreateButton("Continue", true);
            continueButton.Click += (_, _) =>
            {
                DialogResult = true;
                Close();
            };
            buttons.Children.Add(continueButton);
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
