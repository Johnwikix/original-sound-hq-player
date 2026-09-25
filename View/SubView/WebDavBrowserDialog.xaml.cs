using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using System.Collections.Generic;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Services.WebDav;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.View.SubView;

public sealed partial class WebDavBrowserDialog : ContentDialog
{
    public WebDavBrowserViewModel ViewModel { get; }
    private readonly TreeViewNode _rootNode;
    private readonly Dictionary<TreeViewNode, WebDavTreeItem> _itemsByNode = new();
    private readonly HashSet<TreeViewNode> _selectedNodes = [];

    public WebDavBrowserDialog(WebDavSource source)
    {
        ViewModel = new(source, App.Services.GetRequiredService<WebDavLibraryService>(), App.Services.GetRequiredService<WebDavTransport>(),
            App.Services.GetRequiredService<MusicDatabaseService>(), App.Services.GetRequiredService<PlaybackCoordinator>(),
            App.Services.GetRequiredService<AppViewModel>());
        InitializeComponent();
        _rootNode = CreateNode(ViewModel.Root);
        BrowserTree.RootNodes.Add(_rootNode);
        Opened += async (_, _) =>
        {
            await LoadNodeAsync(_rootNode);
            _rootNode.IsExpanded = true;
        };
        Closed += (_, _) => ViewModel.Dispose();
    }

    private TreeViewNode CreateNode(WebDavTreeItem item)
    {
        // Content 只持有数据；模板为每个容器创建视图，折叠再展开时不会重复挂载同一控件。
        var node = new TreeViewNode
        {
            Content = item,
            HasUnrealizedChildren = item.IsDirectory && !item.IsLoaded,
            IsExpanded = item.IsExpanded
        };
        _itemsByNode.Add(node, item);
        return node;
    }

    private async Task LoadNodeAsync(TreeViewNode node)
    {
        if (!_itemsByNode.TryGetValue(node, out var item) || !item.IsDirectory || !node.HasUnrealizedChildren) return;
        await ViewModel.LoadChildrenAsync(item);
        if (!item.IsLoaded || !node.HasUnrealizedChildren) return;
        foreach (var child in item.Children)
        {
            var childNode = CreateNode(child);
            node.Children.Add(childNode);
        }
        node.HasUnrealizedChildren = false;
        SyncSelection();
    }

    private async void Tree_Expanding(TreeView sender, TreeViewExpandingEventArgs args)
        => await LoadNodeAsync(args.Node);

    private async void Tree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is not TreeViewNode node || !_itemsByNode.TryGetValue(node, out var item)) return;
        if (item.IsDirectory) node.IsExpanded = !node.IsExpanded;
        else await ViewModel.PlayItemAsync(item);
    }

    private void Tree_SelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args)
        => SyncSelection();

    private void SyncSelection()
    {
        var selectedNow = new HashSet<TreeViewNode>(BrowserTree.SelectedNodes);
        var removedItems = new List<WebDavTreeItem>();
        var addedItems = new List<WebDavTreeItem>();
        foreach (var node in _selectedNodes)
            if (!selectedNow.Contains(node) && _itemsByNode.TryGetValue(node, out var item)) removedItems.Add(item);
        // 按控件提供的选中节点顺序记录本次新增曲目。
        foreach (var node in BrowserTree.SelectedNodes)
            if (!_selectedNodes.Contains(node) && _itemsByNode.TryGetValue(node, out var item)) addedItems.Add(item);
        _selectedNodes.Clear();
        _selectedNodes.UnionWith(selectedNow);
        if (removedItems.Count != 0 || addedItems.Count != 0)
            ViewModel.UpdateSelection(removedItems, addedItems);
    }
}
