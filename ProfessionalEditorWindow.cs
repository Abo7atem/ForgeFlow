using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ForgeFlow
{
    internal sealed class ProfessionalEditorWindow : Window
    {
        public ProfessionalEditorWindow(string videoPath, VideoEditOptions options)
        {
            Title = "ForgeFlow Editor"; Width = 1440; Height = 900; MinWidth = 1100; MinHeight = 700;
            WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = new SolidColorBrush(Color.FromRgb(10, 10, 10)); Foreground = Brushes.White;
            NativeWindowTheme.Apply(this); Icon = new BitmapImage(new Uri("pack://application:,,,/Assets/ForgeFlow.ico"));
            var root = new Grid { Margin = new Thickness(16) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(180) }); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var header = new DockPanel(); header.Children.Add(new TextBlock { Text = "ForgeFlow  •  Edit Workspace", FontSize = 22, FontWeight = FontWeights.SemiBold }); var export = new Button { Content = "Export Edit Only", Width = 150, Height = 34, Background = Brushes.DodgerBlue, Foreground = Brushes.White }; DockPanel.SetDock(export, Dock.Right); header.Children.Add(export); Grid.SetRow(header, 0); root.Children.Add(header);
            var workspace = new Grid { Margin = new Thickness(0, 12, 0, 12) }; workspace.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) }); workspace.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); workspace.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) }); Grid.SetRow(workspace, 1); root.Children.Add(workspace);
            workspace.Children.Add(Panel("Assets", "Logo PNG\nText Layer\nAdd media"));
            var canvas = new Grid { Background = Brushes.Black, Margin = new Thickness(12, 0, 12, 0) }; Grid.SetColumn(canvas, 1); canvas.Children.Add(new MediaElement { Source = File.Exists(videoPath) ? new Uri(videoPath) : null, LoadedBehavior = MediaState.Manual, Stretch = Stretch.Uniform }); canvas.Children.Add(new Border { Width = 180, Height = 60, BorderBrush = Brushes.DodgerBlue, BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Child = new TextBlock { Text = "Text / Logo layer", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } }); workspace.Children.Add(canvas);
            var properties = Panel("Properties", "Position  X / Y\nScale  100%\nText color\nFont size\nOpacity"); Grid.SetColumn(properties, 2); workspace.Children.Add(properties);
            var timeline = new Grid { Background = new SolidColorBrush(Color.FromRgb(28, 28, 30)), Margin = new Thickness(0, 0, 0, 10) }; timeline.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); timeline.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); timeline.Children.Add(new TextBlock { Text = "Timeline  •  I: In   O: Out   Delete: Remove selection   Space: Play/Pause", Margin = new Thickness(12, 8, 12, 4), Foreground = Brushes.LightGray }); var track = new Slider { Minimum = 0, Maximum = 100, Value = 0, Margin = new Thickness(24, 30, 24, 0), VerticalAlignment = VerticalAlignment.Center }; Grid.SetRow(track, 1); timeline.Children.Add(track); Grid.SetRow(timeline, 2); root.Children.Add(timeline);
            var controls = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center }; foreach (string label in new[] { "|◀", "◀", "▶", "▶|", "Split", "Delete", "Undo" }) controls.Children.Add(new Button { Content = label, MinWidth = 65, Height = 32, Margin = new Thickness(4, 0, 4, 0) }); Grid.SetRow(controls, 3); root.Children.Add(controls); Content = root;
        }
        private static Border Panel(string title, string text) => new() { Background = new SolidColorBrush(Color.FromRgb(28, 28, 30)), Padding = new Thickness(14), Child = new StackPanel { Children = { new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 15 }, new TextBlock { Text = text, Margin = new Thickness(0, 16, 0, 0), Foreground = Brushes.LightGray, LineHeight = 24 } } } };
    }
}
