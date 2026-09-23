using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace WinUIMusicPlayer.ViewModel;

/// <summary>WebDAV 对话框共享的懒加载树节点；不持有 WinUI 控件。</summary>
public sealed class WebDavTreeItem(string href, string name, bool isDirectory) : ObservableObject
{
    public string Href { get; } = href;
    public string Name { get; } = name;
    public string IconGlyph => IsDirectory ? "\uE8B7" : "\uE8A5";
    public bool IsDirectory { get; } = isDirectory;
    public ObservableCollection<WebDavTreeItem> Children { get; } = [];
    public Task? LoadTask { get; set; }
    public bool IsLoaded { get; set; }
    public bool IsLoading { get; set; }
    public bool IsExpanded { get; set; }
    public bool IsSelected { get; set => SetProperty(ref field, value); }
}
