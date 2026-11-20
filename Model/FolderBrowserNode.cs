using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.Generic;
using System.Windows.Input;
using WinUIMusicPlayer.Services;

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
    public int SongCount { get; private set => SetProperty(ref field, value); }
    public int DirectSongCount { get; private set => SetProperty(ref field, value); }
    internal int PendingSongCount { get; set; }
    internal int PendingDirectSongCount { get; set; }

    internal void PublishCounts()
    {
        SongCount = PendingSongCount;
        DirectSongCount = PendingDirectSongCount;
        PendingSongCount = 0;
        PendingDirectSongCount = 0;
    }

    internal void NotifyChildrenChanged(bool hadChildren)
    {
        if (HasChildren != hadChildren) OnPropertyChanged(nameof(HasChildren));
    }
    public ICommand ToggleFolderCommand => FolderCommands.ToggleFolderCommand;
    public bool IsExpanded { get; set => SetProperty(ref field, value); }
}
