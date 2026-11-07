using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System.Collections.Specialized;
using System.ComponentModel;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.View.Controls;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.View;

/// <summary>目录浏览视图；交互转发给会话 ViewModel，控件不拥有目录状态。</summary>
public sealed partial class FolderBrowsePage : Page
{
    private bool _ownsActivation;
    private bool _selectionRestorePending;
    private readonly TreeViewExpansionMotion _treeMotion;
    public FolderViewModel ViewModel { get; }
    public FolderBrowsePage()
    {
        ViewModel = App.Services.GetRequiredService<FolderViewModel>();
        InitializeComponent();
        _treeMotion = new(FolderTree);
        NavigationCacheMode = NavigationCacheMode.Disabled;
        Loaded += Page_Loaded;
        Unloaded += Page_Unloaded;
    }
    public bool HasText(string text) => !string.IsNullOrEmpty(text);
    private async void Page_Loaded(object sender, RoutedEventArgs args)
    {
        // Loaded 可能在没有 Unloaded 的情况下重入（重挂到另一个宿主），已激活时不重复订阅。
        if (_ownsActivation) return;
        Bindings.Initialize();
        Bindings.Update();
        _ownsActivation = true;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        ViewModel.Roots.CollectionChanged += Roots_CollectionChanged;
        await ViewModel.ActivateAsync();
        RequestSelectionRestore();
    }
    private void Page_Unloaded(object sender, RoutedEventArgs args)
    {
        DeactivateView();
        Bindings.StopTracking();
        _treeMotion.Cancel();
    }
    private void DeactivateView()
    {
        // OnNavigatedFrom 已交接给新页面后，旧页面迟到的 Unloaded 不能停止新会话。
        if (!_ownsActivation) return;
        _ownsActivation = false;
        // 离页即取消过渡：不依赖宿主是否触发 Unloaded（Cancel 幂等）。
        _treeMotion.Cancel();
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        ViewModel.Roots.CollectionChanged -= Roots_CollectionChanged;
        StopSelectionRestore();
        ViewModel.Deactivate();
    }
    protected override void OnNavigatedFrom(NavigationEventArgs args)
    {
        DeactivateView();
        base.OnNavigatedFrom(args);
    }
    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(FolderViewModel.SelectedNode)) RequestSelectionRestore();
    }

    private void Roots_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        // 根节点整体重建会连带恢复展开状态：只跳过重建那一轮布局，避免逐行补播盖住刷新。
        if (args.Action == NotifyCollectionChangedAction.Reset) _treeMotion.SuppressRestorePass();
    }

    private void RequestSelectionRestore()
    {
        if (!_ownsActivation) return;
        if (ViewModel.SelectedNode is null || FolderTree.ContainerFromItem(ViewModel.SelectedNode) is TreeViewItem { IsSelected: true })
        {
            StopSelectionRestore();
            return;
        }
        if (_selectionRestorePending) return;
        _selectionRestorePending = true;
        FolderTree.LayoutUpdated += RestoreSelectionAfterLayout;
    }

    private void RestoreSelectionAfterLayout(object? sender, object args)
    {
        // 每次只等一轮布局；屏外节点由容器 Loaded 再触发，避免持续跟踪布局。
        StopSelectionRestore();
        if (!_ownsActivation || ViewModel.SelectedNode is not { } selected)
        {
            return;
        }
        for (var parent = selected.Parent; parent is not null; parent = parent.Parent)
            if (!parent.IsExpanded)
            {
                return;
            }
        if (FolderTree.ContainerFromItem(selected) is not TreeViewItem item) return;
        // 重建目录树时 SelectedItem 可能早于子节点容器生效；布局完成后恢复真正的高亮。
        // 状态始终取自 VM，只临时跟踪布局，成功或离页后立即解绑。
        item.IsSelected = true;
    }

    private void TreeItem_Loaded(object sender, RoutedEventArgs args) => RequestSelectionRestore();

    private void StopSelectionRestore()
    {
        if (!_selectionRestorePending) return;
        _selectionRestorePending = false;
        FolderTree.LayoutUpdated -= RestoreSelectionAfterLayout;
    }

    private void Tree_SelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args)
    {
        if (!_ownsActivation) return;
        // WinUI 单选事件先发出，SelectedItem 随后更新；必须使用本次事件的数据。
        if (args.AddedItems.Count > 0 && args.AddedItems[0] is FolderBrowserNode node)
            ViewModel.SelectFolderCommand.Execute(node);
    }
    private void Tree_Expanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        if (!_ownsActivation) return;
        if (args.Item is FolderBrowserNode node)
        {
            ViewModel.ExpandFolder(node);
            _treeMotion.PrepareExpand();
        }
        RequestSelectionRestore();
    }
    private void Tree_Collapsed(TreeView sender, TreeViewCollapsedEventArgs args)
    {
        if (!_ownsActivation) return;
        _treeMotion.PrepareCollapse();
    }
}
