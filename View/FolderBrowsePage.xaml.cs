using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using System.Threading.Tasks;
using System.Windows.Input;
using Windows.System;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Behaviors;
using WinUIMusicPlayer.View.Controls;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.View;

/// <summary>目录浏览视图；页面管理激活生命周期，目录状态与交互命令由会话 ViewModel 所有。</summary>
public sealed partial class FolderBrowsePage : Page
{
    private bool _isCurrent;
    private bool _ownsActivation;
    private bool _resetScrollOnEntry;
    private FolderTreeMotion? _treeMotion;
    public FolderViewModel ViewModel { get; }
    public FolderBrowsePage()
    {
        ViewModel = App.Services.GetRequiredService<FolderViewModel>();
        _treeMotion = new FolderTreeMotion(ViewModel.VisibleNodes);
        InitializeComponent();
        _treeMotion.Attach(FolderTree, FolderMotionOverlay);
        NavigationCacheMode = NavigationCacheMode.Enabled;
        Loaded += Page_Loaded;
        Unloaded += Page_Unloaded;
    }
    public bool HasText(string text) => !string.IsNullOrEmpty(text);
    protected override async void OnNavigatedTo(NavigationEventArgs args)
    {
        base.OnNavigatedTo(args);
        _isCurrent = true;
        if (IsLoaded) await ActivateViewAsync();
    }

    private async void Page_Loaded(object sender, RoutedEventArgs args) => await ActivateViewAsync();

    private async Task ActivateViewAsync()
    {
        if (!_isCurrent || _ownsActivation) return;
        if (_treeMotion is null)
        {
            _treeMotion = new FolderTreeMotion(ViewModel.VisibleNodes);
            _treeMotion.Attach(FolderTree, FolderMotionOverlay);
        }
        Bindings.Initialize();
        Bindings.Update();
        if (_resetScrollOnEntry)
        {
            ListViewCentering.ResetScrollPosition(FolderTree);
            DetailView.ResetScrollPosition();
            _resetScrollOnEntry = false;
        }
        _ownsActivation = true;
        await ViewModel.ActivateAsync();
    }
    // These events are wired once with the tree. A cached visual can receive a late Unloaded
    // without another Loaded, which can leave an attached behavior disconnected.
    private void FolderTree_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_ownsActivation || FolderTree.SelectedItem is not FolderBrowserNode node) return;
        if (ViewModel.SelectFolderCommand.CanExecute(node)) ViewModel.SelectFolderCommand.Execute(node);
        FolderTree.ScrollIntoView(node);
    }

    private void FolderTree_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (!args.Handled) args.Handled = TryNavigateFolderKey(args.Key);
    }

    internal bool TryNavigateFolderKey(VirtualKey key)
    {
        if (!_ownsActivation) return false;
        ICommand? command = key switch
        {
            VirtualKey.Right => ViewModel.NavigateIntoFolderCommand,
            VirtualKey.Left => ViewModel.NavigateOutOfFolderCommand,
            _ => null
        };
        var node = ViewModel.SelectedNode;
        if (command?.CanExecute(node) != true) return false;
        command.Execute(node);
        return true;
    }

    private void Page_Unloaded(object sender, RoutedEventArgs args)
    {
        if (_isCurrent && IsLoaded)
        {
            // A delayed Unloaded from the previous visit may arrive after cached reentry.
            DispatcherQueue.TryEnqueue(CheckUnloaded);
            return;
        }
        ReleaseView();
    }

    private void CheckUnloaded()
    {
        if (!IsLoaded || !_isCurrent) ReleaseView();
    }

    private void ReleaseView()
    {
        DeactivateView();
        _treeMotion?.Dispose();
        _treeMotion = null;
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
        _isCurrent = false;
        _resetScrollOnEntry = true;
        DeactivateView();
        base.OnNavigatedFrom(args);
    }
}
