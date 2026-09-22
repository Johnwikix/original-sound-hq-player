using CommunityToolkit.Mvvm.ComponentModel;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.State;

/// <summary>保留各页已有的共享搜索、排序与详情导航语义，控件选择和滚动仍归页面。</summary>
public sealed class BrowseState : ObservableObject
{
    public int SourceFilterId { get; set => SetProperty(ref field, value); } = -1;
    public string SearchText { get; set => SetProperty(ref field, value); } = "";
    public SortOption SelectedSortOption { get; set => SetProperty(ref field, value); }
    public Music? CurrentArtistObj { get; set => SetProperty(ref field, value); }
    public Music? CurrentAlbumObj { get; set => SetProperty(ref field, value); }
    public Music? CurrentFolderObj { get; set => SetProperty(ref field, value); }
    public PlayList CurrentPlayList { get; set => SetProperty(ref field, value); }
    public int CurrentPlayListId { get; set; }
}
