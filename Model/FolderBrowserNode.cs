using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.Generic;

namespace WinUIMusicPlayer.Model;

/// <summary>库目录节点；层级和展开状态不依赖任何 UI 容器。</summary>
public sealed class FolderBrowserNode(FolderLocation location, FolderBrowserNode? parent) : ObservableObject
{
    public FolderLocation Location { get; } = location;
    public FolderBrowserNode? Parent { get; } = parent;
    public int Depth { get; } = parent is null ? 0 : parent.Depth + 1;
    public string Name => Location.Name;
    public string Path => Location.Path;
    public List<FolderBrowserNode> Children { get; } = [];
    public bool HasChildren => Children.Count > 0;
    public int SongCount { get; internal set; }
    public int DirectSongCount { get; internal set; }
    public bool IsExpanded { get; set => SetProperty(ref field, value); }
}
