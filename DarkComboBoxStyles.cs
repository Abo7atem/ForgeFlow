using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;

namespace ForgeFlow
{
    internal static class DarkComboBoxStyles
    {
        public static Style CreateComboBoxStyle()
        {
            const string xaml = """
                <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                 xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                                 TargetType="{x:Type ComboBox}">
                  <Grid SnapsToDevicePixels="True">
                    <ToggleButton x:Name="Toggle" Focusable="False" ClickMode="Press"
                                  Background="Transparent" BorderThickness="0"
                                  IsChecked="{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}">
                      <ToggleButton.Template>
                        <ControlTemplate TargetType="{x:Type ToggleButton}">
                          <Border x:Name="Frame" Background="#17171A" BorderBrush="#4A4A50" BorderThickness="1" CornerRadius="6">
                            <Grid>
                              <Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition Width="32"/></Grid.ColumnDefinitions>
                              <Border Grid.Column="1" Background="#202024" BorderBrush="#3F3F45" BorderThickness="1,0,0,0" CornerRadius="0,6,6,0"/>
                              <Path Grid.Column="1" Data="M 0 0 L 4 4 L 8 0" Stroke="#F0F0F2" StrokeThickness="1.7"
                                    HorizontalAlignment="Center" VerticalAlignment="Center"/>
                            </Grid>
                          </Border>
                          <ControlTemplate.Triggers>
                            <Trigger Property="IsMouseOver" Value="True">
                              <Setter TargetName="Frame" Property="BorderBrush" Value="#00A7E8"/>
                            </Trigger>
                            <Trigger Property="IsChecked" Value="True">
                              <Setter TargetName="Frame" Property="Background" Value="#1E1E22"/>
                              <Setter TargetName="Frame" Property="BorderBrush" Value="#F28C28"/>
                            </Trigger>
                            <Trigger Property="IsEnabled" Value="False">
                              <Setter TargetName="Frame" Property="Opacity" Value="0.42"/>
                            </Trigger>
                          </ControlTemplate.Triggers>
                        </ControlTemplate>
                      </ToggleButton.Template>
                    </ToggleButton>

                    <ContentPresenter x:Name="ContentSite" IsHitTestVisible="False"
                                      Content="{TemplateBinding SelectionBoxItem}"
                                      ContentTemplate="{TemplateBinding SelectionBoxItemTemplate}"
                                      ContentTemplateSelector="{TemplateBinding ItemTemplateSelector}"
                                      Margin="11,4,38,4" VerticalAlignment="Center"
                                      HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}">
                      <ContentPresenter.Resources>
                        <Style TargetType="{x:Type TextBlock}">
                          <Setter Property="Foreground" Value="#F7F7F8"/>
                        </Style>
                      </ContentPresenter.Resources>
                    </ContentPresenter>

                    <Popup x:Name="PART_Popup" IsOpen="{TemplateBinding IsDropDownOpen}" Placement="Bottom"
                           AllowsTransparency="True" Focusable="False" PopupAnimation="Fade">
                      <Border Margin="0,4,0,0" MinWidth="{TemplateBinding ActualWidth}"
                              MaxHeight="{TemplateBinding MaxDropDownHeight}"
                              Background="#111114" BorderBrush="#515159" BorderThickness="1" CornerRadius="7">
                        <Border.Effect>
                          <DropShadowEffect Color="#000000" BlurRadius="15" ShadowDepth="4" Opacity="0.75"/>
                        </Border.Effect>
                        <ScrollViewer Margin="3" Background="#111114" SnapsToDevicePixels="True">
                          <ScrollViewer.Resources>
                            <Style TargetType="{x:Type ScrollBar}">
                              <Setter Property="Background" Value="#111114"/>
                              <Setter Property="Foreground" Value="#F28C28"/>
                            </Style>
                          </ScrollViewer.Resources>
                          <StackPanel IsItemsHost="True" Background="#111114"
                                      KeyboardNavigation.DirectionalNavigation="Contained"/>
                        </ScrollViewer>
                      </Border>
                    </Popup>
                  </Grid>
                </ControlTemplate>
                """;

            var style = new Style(typeof(ComboBox));
            style.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(23, 23, 26))));
            style.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(Color.FromRgb(247, 247, 248))));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(74, 74, 80))));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(9, 5, 9, 5)));
            style.Setters.Add(new Setter(Control.FontFamilyProperty, new FontFamily("Segoe UI")));
            style.Setters.Add(new Setter(Control.FontSizeProperty, 13.0));
            style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
            style.Setters.Add(new Setter(ComboBox.MaxDropDownHeightProperty, 300.0));
            style.Setters.Add(new Setter(Control.TemplateProperty, (ControlTemplate)XamlReader.Parse(xaml)));
            return style;
        }

        public static Style CreateComboBoxItemStyle()
        {
            const string xaml = """
                <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                 xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                                 TargetType="{x:Type ComboBoxItem}">
                  <Border x:Name="ItemBorder" Background="{TemplateBinding Background}"
                          BorderBrush="Transparent" BorderThickness="1"
                          CornerRadius="5" Margin="2,1" Padding="{TemplateBinding Padding}">
                    <ContentPresenter VerticalAlignment="Center" HorizontalAlignment="Stretch">
                      <ContentPresenter.Resources>
                        <Style TargetType="{x:Type TextBlock}">
                          <Setter Property="Foreground" Value="#F7F7F8"/>
                        </Style>
                      </ContentPresenter.Resources>
                    </ContentPresenter>
                  </Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsHighlighted" Value="True">
                      <Setter TargetName="ItemBorder" Property="Background" Value="#063F5A"/>
                      <Setter TargetName="ItemBorder" Property="BorderBrush" Value="#00A7E8"/>
                    </Trigger>
                    <Trigger Property="IsSelected" Value="True">
                      <Setter TargetName="ItemBorder" Property="Background" Value="#8E400D"/>
                      <Setter TargetName="ItemBorder" Property="BorderBrush" Value="#F28C28"/>
                    </Trigger>
                    <Trigger Property="IsEnabled" Value="False">
                      <Setter TargetName="ItemBorder" Property="Opacity" Value="0.38"/>
                    </Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
                """;

            var style = new Style(typeof(ComboBoxItem));
            style.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(17, 17, 20))));
            style.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(Color.FromRgb(247, 247, 248))));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(11, 8, 11, 8)));
            style.Setters.Add(new Setter(Control.FontFamilyProperty, new FontFamily("Segoe UI")));
            style.Setters.Add(new Setter(Control.FontSizeProperty, 13.0));
            style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
            style.Setters.Add(new Setter(Control.TemplateProperty, (ControlTemplate)XamlReader.Parse(xaml)));
            return style;
        }
    }
}
