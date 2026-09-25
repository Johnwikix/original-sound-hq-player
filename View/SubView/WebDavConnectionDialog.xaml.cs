using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using System.Collections.Specialized;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.View.SubView;

public sealed partial class WebDavConnectionDialog : ContentDialog
{
    public WebDavConnectionViewModel ViewModel { get; }
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
        FolderTree.RootNodes.Clear();
        foreach (var folder in ViewModel.Folders) FolderTree.RootNodes.Add(CreateNode(folder));
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
        foreach (var child in item.Children) node.Children.Add(CreateNode(child));
        return node;
    }
    private async void Tree_Expanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        if (args.Node.Content is not WebDavTreeItem item || !args.Node.HasUnrealizedChildren) return;
        await ViewModel.LoadChildrenAsync(item);
        if (!item.IsLoaded || !args.Node.HasUnrealizedChildren) return;
        foreach (var child in item.Children)
            args.Node.Children.Add(CreateNode(child));
        args.Node.HasUnrealizedChildren = false;
    }
    private async void Save_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try { args.Cancel = !await ViewModel.SaveAsync(); }
        finally { deferral.Complete(); }
    }
    private void ScanRoot_Click(object sender, RoutedEventArgs args)
    {
        if (sender is CheckBox { Tag: WebDavTreeItem folder })
            ViewModel.SetSelected(folder, folder.SelectionState != true);
    }
}
