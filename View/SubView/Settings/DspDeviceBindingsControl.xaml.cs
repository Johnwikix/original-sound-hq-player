using Microsoft.UI.Xaml.Controls;
using WinUIMusicPlayer.ViewModel.Controls;

namespace WinUIMusicPlayer.View.SubView.Settings;

/// <summary>设备绑定布局；曲线弹窗与 WAV 绑定弹窗共用 ViewModel 命令。</summary>
public sealed partial class DspDeviceBindingsControl : UserControl
{
    public DspSettingsViewModel ViewModel { get; }

    public DspDeviceBindingsControl(DspSettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        SizeChanged += (_, _) => Actions.Orientation = ActualWidth < 550 ? Orientation.Vertical : Orientation.Horizontal;
    }
}
