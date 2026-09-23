using Microsoft.UI.Xaml.Controls;
using System.Collections.Generic;
using System.Collections.Specialized;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.View.SubView;

public sealed partial class WebDavConnectionDialog : ContentDialog
{
    public WebDavConnectionViewModel ViewModel { get; }
    private bool _updatingTree;
    private readonly Dictionary<TreeViewNode, WebDavTreeItem> _itemsByNode = new();
    private readonly HashSet<TreeViewNode> _selectedNodes = [];
    public WebDavConnectionDialog(WebDavConnectionViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        ViewModel.Folders.CollectionChanged += Folders_CollectionChanged;
        Closed += (_, _) =>
        {
            ViewModel.Folders.CollectionChanged -= Folders_CollectionChanged;
            ViewModel.Dispose();
        };
    }
    private void Folders_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        _updatingTree = true;
        try
        {
            FolderTree.SelectedNodes.Clear();
            FolderTree.RootNodes.Clear();
            _itemsByNode.Clear();
            _selectedNodes.Clear();
            foreach (var folder in ViewModel.Folders) FolderTree.RootNodes.Add(CreateNode(folder));
            foreach (var node in FolderTree.RootNodes) SelectSavedNodes(node);
            // TreeView 的父节点选中会级联已加载的子节点；同步这些可见选择，供部分取消时保存剩余目录。
            foreach (var node in FolderTree.SelectedNodes)
            {
                _selectedNodes.Add(node);
                if (_itemsByNode.TryGetValue(node, out var item)) item.IsSelected = true;
            }
        }
        finally { _updatingTree = false; }
    }
    private TreeViewNode CreateNode(WebDavTreeItem item)
    {
        // Content 只持有数据；模板为每个容器创建视图，折叠再展开时不会重复挂载同一控件。
        var node = new TreeViewNode
        {
            Content = item,
            HasUnrealizedChildren = !item.IsLoaded,
            IsExpanded = item.IsExpanded
        };
        _itemsByNode.Add(node, item);
        foreach (var child in item.Children) node.Children.Add(CreateNode(child));
        return node;
    }
    private void SelectSavedNodes(TreeViewNode node)
    {
        if (_itemsByNode.TryGetValue(node, out var item) && item.IsSelected && !FolderTree.SelectedNodes.Contains(node))
            FolderTree.SelectedNodes.Add(node);
        foreach (var child in node.Children) SelectSavedNodes(child);
    }
    private async void Tree_Expanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        if (!_itemsByNode.TryGetValue(args.Node, out var item) || !args.Node.HasUnrealizedChildren) return;
        await ViewModel.LoadChildrenAsync(item);
        if (!item.IsLoaded || !args.Node.HasUnrealizedChildren) return;
        foreach (var child in item.Children)
        {
            var childNode = CreateNode(child);
            args.Node.Children.Add(childNode);
            if (child.IsSelected && !FolderTree.SelectedNodes.Contains(childNode))
                FolderTree.SelectedNodes.Add(childNode);
        }
        args.Node.HasUnrealizedChildren = false;
        SyncSelection();
    }
    private void Tree_SelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args)
    {
        if (_updatingTree) return;
        SyncSelection();
    }
    private void SyncSelection()
    {
        var selectedNow = new HashSet<TreeViewNode>(FolderTree.SelectedNodes);
        foreach (var node in _selectedNodes)
            if (!selectedNow.Contains(node) && _itemsByNode.TryGetValue(node, out var item)) ViewModel.SetSelected(item, false);
        foreach (var node in selectedNow)
            if (!_selectedNodes.Contains(node) && _itemsByNode.TryGetValue(node, out var item)) ViewModel.SetSelected(item, true);
        _selectedNodes.Clear();
        _selectedNodes.UnionWith(selectedNow);
    }
    private async void Save_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try { args.Cancel = !await ViewModel.SaveAsync(); }
        finally { deferral.Complete(); }
    }
}
