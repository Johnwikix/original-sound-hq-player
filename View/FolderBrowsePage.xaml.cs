using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.View;

/// <summary>目录浏览视图；交互转发给会话 ViewModel，控件不拥有目录状态。</summary>
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
        // OnNavigatedFrom 已交接给新页面后，旧页面迟到的 Unloaded 不能停止新会话。
        if (!_ownsActivation) return;
        _ownsActivation = false;
        ViewModel.Deactivate();
    }
    protected override void OnNavigatedFrom(NavigationEventArgs args)
    {
        DeactivateView();
        base.OnNavigatedFrom(args);
    }
    private void Tree_SelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem is FolderBrowserNode node) ViewModel.SelectFolderCommand.Execute(node);
    }
    private void Tree_Expanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        if (args.Item is FolderBrowserNode node) ViewModel.ExpandFolder(node);
    }
}
