using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Threading.Tasks;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.ViewModel.Controls;

namespace WinUIMusicPlayer.View.SubView.Settings;

/// <summary>音效设置宿主：控件状态全部由 DspSettingsViewModel 绑定驱动，这里只处理视图生命周期和均衡器弹窗。</summary>
public sealed partial class DspSettingsControl : UserControl
{
    public DspSettingsViewModel ViewModel { get; }

    public DspSettingsControl()
    {
        InitializeComponent();
        ViewModel = App.Services.GetRequiredService<DspSettingsViewModel>();
        DataContext = this;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs args) => await ViewModel.OnViewLoadedAsync();

    private async void OnUnloaded(object sender, RoutedEventArgs args) => await ViewModel.OnViewUnloadedAsync();

    private async void Reset_Click(object sender, RoutedEventArgs args) => await ViewModel.ResetAsync();

    private async void OpenEqualizer_Click(object sender, RoutedEventArgs args)
    {
        var dialog = new EqualizerDialog { XamlRoot = XamlRoot, RequestedTheme = ActualTheme };
        dialog.EqualizerCommitted += static (_, _) => App.Services.GetRequiredService<IpcService>().UpdateEq();
        await dialog.ShowAsync();
    }
}
