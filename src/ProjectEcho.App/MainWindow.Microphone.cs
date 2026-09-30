using System.Windows;
using System.Windows.Controls;
using ProjectEcho.Capture.Windows;

namespace ProjectEcho.App;

public partial class MainWindow
{
    private AudioDevice? PickRecordingMicrophone(IReadOnlyList<AudioDevice> devices)
    {
        var picker = new ComboBox
        {
            ItemsSource = devices, DisplayMemberPath = "Name", SelectedIndex = -1,
            MinWidth = 320, Margin = new Thickness(0, 12, 0, 16)
        };
        System.Windows.Automation.AutomationProperties.SetName(picker, UiText.T("本场麦克风设备"));
        var confirm = new Button { Content = UiText.T("同意并开启所选麦克风"), IsEnabled = false, IsDefault = true };
        var cancel = new Button { Content = UiText.T("取消"), IsCancel = true };
        var buttons = new WrapPanel(); buttons.Children.Add(confirm); buttons.Children.Add(cancel);
        var content = new StackPanel { Margin = new Thickness(22) };
        content.Children.Add(new TextBlock
        {
            Text = devices.Count == 0 ? UiText.T("未发现可用麦克风。连接设备后可再次点击开启。")
                : UiText.T("请选择本场麦克风。开启后与 Zoom/Webex 静音独立，可能录下私下声音、其他人和回声；本场固定使用所选设备。"),
            TextWrapping = TextWrapping.Wrap, MaxWidth = 420
        });
        content.Children.Add(picker); content.Children.Add(buttons);
        var dialog = new Window
        {
            Owner = this, Title = UiText.T("录音中开启麦克风"), Content = content,
            SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (System.Windows.Media.Brush)FindResource("SurfaceBrush"),
            Foreground = (System.Windows.Media.Brush)FindResource("TextBrush")
        };
        picker.SelectionChanged += (_, _) => confirm.IsEnabled = picker.SelectedItem is AudioDevice;
        confirm.Click += (_, _) => dialog.DialogResult = true;
        return dialog.ShowDialog() == true ? picker.SelectedItem as AudioDevice : null;
    }
}
