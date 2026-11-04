using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace WinUIMusicPlayer.Model;

/// <summary>库目录节点；树只在展开时发布下一层节点，不包含控件。</summary>
public sealed class FolderBrowserNode(FolderLocation location, FolderBrowserNode? parent) : ObservableObject
{
    public FolderLocation Location { get; } = location;
    public FolderBrowserNode? Parent { get; } = parent;
    public string Name => Location.Name;
    public string Path => Location.Path;
    public List<FolderBrowserNode> Children { get; } = [];
    public ObservableCollection<FolderBrowserNode> VisibleChildren { get; } = [];
    public int SongCount { get; internal set; }
    public int DirectSongCount { get; internal set; }
    public bool IsExpanded { get; set => SetProperty(ref field, value); }
    public bool HasUnrealizedChildren { get; private set => SetProperty(ref field, value); }
    public void InitializeChildren() => HasUnrealizedChildren = Children.Count > 0;
    public void RealizeChildren()
    {
        if (!HasUnrealizedChildren) return;
        HasUnrealizedChildren = false;
        foreach (var child in Children) VisibleChildren.Add(child);
    }
}
