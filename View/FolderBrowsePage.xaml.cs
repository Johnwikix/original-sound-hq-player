using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System.Collections.Generic;
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
        if (!_ownsActivation || ViewModel.SelectedNode is not { } selected)
        {
            StopSelectionRestore();
            return;
        }
        if (FolderTree.ContainerFromItem(selected) is TreeViewItem { IsSelected: true })
        {
            StopSelectionRestore();
            return;
        }
        for (var parent = selected.Parent; parent is not null; parent = parent.Parent)
            if (!parent.IsExpanded)
            {
                StopSelectionRestore();
                return;
            }
        // 根节点重建后容器由框架同步为折叠，且模型的展开值在容器生成前已写入、不会再触发绑定推送；
        // 因此从根到选中节点逐层核对容器，模型已展开而容器未展开时推一层，子级容器下一轮布局才就绪。
        List<FolderBrowserNode> ancestors = [];
        for (var parent = selected.Parent; parent is not null; parent = parent.Parent) ancestors.Add(parent);
        for (var depth = ancestors.Count - 1; depth >= 0; depth--)
        {
            if (FolderTree.ContainerFromItem(ancestors[depth]) is not TreeViewItem container) return;
            if (!container.IsExpanded)
            {
                container.IsExpanded = true;
                return;
            }
        }
        // 增删过渡会推迟容器实现；保持监听直到成功、节点收起或离页，不能只等首轮布局。
        if (FolderTree.ContainerFromItem(selected) is not TreeViewItem item) return;
        item.IsSelected = true;
        if (item.IsSelected) StopSelectionRestore();
    }

    private void TreeItem_Loaded(object sender, RoutedEventArgs args)
    {
        // 行就绪即注册展开回调（重排记录用）；淡入窗口内的行在首帧渲染前置 0 起步。
        if (sender is TreeViewItem row) _treeMotion.OnRowLoaded(row);
        RequestSelectionRestore();
        // Loaded 时容器已经可用，不必再等下一轮可能不发生的布局通知。
        if (_selectionRestorePending) RestoreSelectionAfterLayout(sender, args);
    }

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
        if (args.Item is FolderBrowserNode node) ViewModel.ExpandFolder(node);
        RequestSelectionRestore();
    }
    private void Tree_Collapsed(TreeView sender, TreeViewCollapsedEventArgs args)
    {
        if (!_ownsActivation) return;
        // 收起是用户意图，经 VM 记录；容器复位（重建、复用）不触发此事件，不会误写。
        if (args.Item is FolderBrowserNode node) ViewModel.CollapseFolder(node);
    }
}
