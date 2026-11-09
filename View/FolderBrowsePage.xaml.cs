using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.View;

/// <summary>目录浏览视图；页面管理激活生命周期，目录状态与交互命令由会话 ViewModel 所有。</summary>
public sealed partial class FolderBrowsePage : Page
{
    private bool _ownsActivation;
    public FolderViewModel ViewModel { get; }
    public FolderBrowsePage()
    {
        ViewModel = App.Services.GetRequiredService<FolderViewModel>();
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Disabled;
        Loaded += Page_Loaded;
        Unloaded += Page_Unloaded;
    }
    public bool HasText(string text) => !string.IsNullOrEmpty(text);
    private async void Page_Loaded(object sender, RoutedEventArgs args)
    {
        if (_ownsActivation) return;
        Bindings.Initialize();
        Bindings.Update();
        _ownsActivation = true;
        await ViewModel.ActivateAsync();
    }
    private void Page_Unloaded(object sender, RoutedEventArgs args)
    {
        DeactivateView();
        Bindings.StopTracking();
    }
    private void DeactivateView()
    {
        if (!_ownsActivation) return;
        _ownsActivation = false;
        ViewModel.Deactivate();
    }
    protected override void OnNavigatedFrom(NavigationEventArgs args)
    {
        DeactivateView();
        base.OnNavigatedFrom(args);
    }
}
