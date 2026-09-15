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

    private async void EditCurve_Click(object sender, RoutedEventArgs args)
    {
        var dialog = new ConvolutionCurveDialog(ViewModel) { XamlRoot = XamlRoot, RequestedTheme = ActualTheme };
        try
        {
            if (await dialog.ShowAsync() == ContentDialogResult.Primary) await ViewModel.ApplyCurveAsync(dialog.Draft);
        }
        finally { ViewModel.EndCorrectionEditing(); App.Services.GetRequiredService<IpcService>().UpdateDsp(); }
    }

    private async void ManageDeviceBindings_Click(object sender, RoutedEventArgs args)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, RequestedTheme = ActualTheme,
            Title = Utils.ToolUtils.GetString("DspDeviceTarget"),
            CloseButtonText = Utils.ToolUtils.GetString("CloseButton"),
            Content = new DspDeviceBindingsControl(ViewModel)
        };
        await dialog.ShowAsync();
    }

    private async void ImportImpulse_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(XamlRoot.ContentIslandEnvironment.AppWindowId);
            picker.FileTypeFilter.Add(".wav");
            var result = await picker.PickSingleFileAsync();
            if (result != null) await ViewModel.ImportImpulseAsync(result.Path);
        }
        catch (Exception) { ViewModel.ShowImportError(); }
    }

    private async void ClearImpulse_Click(object sender, RoutedEventArgs args) => await ViewModel.ClearImpulseAsync();

    private async void Reset_Click(object sender, RoutedEventArgs args) => await ViewModel.ResetAsync();

    private async void OpenEqualizer_Click(object sender, RoutedEventArgs args)
    {
        var dialog = new EqualizerDialog { XamlRoot = XamlRoot, RequestedTheme = ActualTheme };
        dialog.EqualizerCommitted += static (_, _) => App.Services.GetRequiredService<IpcService>().UpdateEq();
        await dialog.ShowAsync();
    }
}
