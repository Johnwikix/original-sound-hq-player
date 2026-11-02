using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using WinUIMusicPlayer.ViewModel.Pages;
namespace WinUIMusicPlayer.View.SubView.Settings;
/// <summary>只绑定分析状态和开关，生命周期由应用拥有。</summary>
public sealed partial class AudioAnalysisSettingsControl : UserControl
{
    public AnalysisSettingsViewModel ViewModel { get; }
    public AudioAnalysisSettingsControl()
    {
        ViewModel = App.Services.GetRequiredService<AnalysisSettingsViewModel>();
        InitializeComponent();
        WinUIMusicPlayer.Services.UiLayoutDiagnostics.Track(this);
    }
}
