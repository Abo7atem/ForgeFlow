using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ForgeFlow
{
    public sealed class SuccessWindow : Window
    {
        public bool OpenFolderRequested { get; private set; }

        public SuccessWindow(string video60Path, string tikTok30Path)
        {
            Title = "ForgeFlow Complete";
            NativeWindowTheme.Apply(this);
            Icon = new BitmapImage(new Uri("pack://application:,,,/Assets/ForgeFlow.ico"));
            Width = 560;
            Height = 310;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(Color.FromRgb(10, 10, 10));
            Foreground = Brushes.White;
            FontFamily = new FontFamily("Segoe UI");

            var root = new Grid { Margin = new Thickness(22) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(16) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(16) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var header = new StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(new Image
            {
                Source = new BitmapImage(new Uri("pack://application:,,,/Assets/ForgeFlowMark.png")),
                Width = 76,
                Height = 48,
                Stretch = Stretch.Uniform,
                Margin = new Thickness(0, 0, 14, 0)
            });
            var headerText = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            headerText.Children.Add(new TextBlock { Text = "تم إنشاء الملفات بنجاح", FontSize = 21, FontWeight = FontWeights.SemiBold });
            headerText.Children.Add(new TextBlock
            {
                Text = "Video 60FPS و TikTok30FPS جاهزين.",
                Foreground = new SolidColorBrush(Color.FromRgb(160, 160, 166)),
                Margin = new Thickness(0, 4, 0, 0)
            });
            header.Children.Add(headerText);
            root.Children.Add(header);

            var outputBox = new TextBox
            {
                Text = $"Video 60FPS: {video60Path}{Environment.NewLine}TikTok30FPS: {tikTok30Path}",
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = new SolidColorBrush(Color.FromRgb(28, 28, 30)),
                Foreground = new SolidColorBrush(Color.FromRgb(209, 209, 214)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(58, 58, 60)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10),
                FontFamily = new FontFamily("Consolas")
            };
            Grid.SetRow(outputBox, 2);
            root.Children.Add(outputBox);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(CreateButton("Copy Paths", (_, _) => Clipboard.SetText(outputBox.Text)));
            buttons.Children.Add(CreateButton("Open Folder", (_, _) =>
            {
                OpenFolderRequested = true;
                DialogResult = true;
                Close();
            }, true));
            buttons.Children.Add(CreateButton("Close", (_, _) => Close()));
            Grid.SetRow(buttons, 4);
            root.Children.Add(buttons);

            Content = root;
        }

        private static Button CreateButton(string text, RoutedEventHandler handler, bool primary = false)
        {
            var button = new Button
            {
                Content = text,
                MinWidth = 104,
                Height = 34,
                Margin = new Thickness(8, 0, 0, 0),
                Background = new SolidColorBrush(primary ? Color.FromRgb(10, 132, 255) : Color.FromRgb(44, 44, 46)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(primary ? Color.FromRgb(10, 132, 255) : Color.FromRgb(58, 58, 60)),
                BorderThickness = new Thickness(1),
                FontWeight = FontWeights.SemiBold,
                Cursor = System.Windows.Input.Cursors.Hand
            };
            button.Click += handler;
            return button;
        }
    }
}
