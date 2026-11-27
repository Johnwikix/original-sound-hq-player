using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using System;
using System.Collections.Specialized;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.View.SubView;

public sealed partial class WebDavConnectionDialog : ContentDialog
{
    public WebDavConnectionViewModel ViewModel { get; }
    private readonly Func<WebDavSource, string?, bool> _submitSave;
    private bool _accepted, _closed;
    internal WebDavConnectionDialog(WebDavConnectionViewModel viewModel, Func<WebDavSource, string?, bool> submitSave)
    {
        ViewModel = viewModel;
        _submitSave = submitSave;
        InitializeComponent();
        ViewModel.Folders.CollectionChanged += Folders_CollectionChanged;
        Closed += (_, _) =>
        {
            _closed = true;
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
        if (_closed || !item.IsLoaded || !args.Node.HasUnrealizedChildren) return;
        foreach (var child in item.Children)
            args.Node.Children.Add(CreateNode(child));
        args.Node.HasUnrealizedChildren = false;
    }
    private void Save_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (_accepted) return;
        args.Cancel = true;
        if (!ViewModel.TryPrepareSave(out var source, out var password)) return;
        if (!_submitSave(source, password)) { ViewModel.ShowSaveUnavailable(source.Name); return; }
        _accepted = true;
        args.Cancel = false;
    }
    private void ScanRoot_Click(object sender, RoutedEventArgs args)
    {
        if (sender is CheckBox { Tag: WebDavTreeItem folder })
            ViewModel.SetSelected(folder, folder.SelectionState != true);
    }
}
