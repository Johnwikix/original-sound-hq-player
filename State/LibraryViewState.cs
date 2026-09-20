using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using WinUIMusicPlayer.Model;
namespace WinUIMusicPlayer.State;

/// <summary>库页面共用的绑定集合。查询与导航调度不属于此状态对象。</summary>
public sealed class LibraryViewState : ObservableObject
{
    public BulkObservableCollection<Music> FavoriteSongs { get; set => SetProperty(ref field, value); } = [];
    public BulkObservableCollection<PlayListMusicItem> PlayListSongs { get; set => SetProperty(ref field, value); } = [];
    public BulkObservableCollection<PlayList> AllPlayList { get; set => SetProperty(ref field, value); } = [];
    public CollectionViewSource AlbumPageSource { get; set => SetProperty(ref field, value); } = new CollectionViewSource() { IsSourceGrouped = true };
    public CollectionViewSource ArtistPageSource { get; set => SetProperty(ref field, value); } = new CollectionViewSource() { IsSourceGrouped = true };
    public CollectionViewSource FolderPageSource { get; set => SetProperty(ref field, value); } = new CollectionViewSource() { IsSourceGrouped = true };
    public BulkObservableCollection<Music> ListSongs { get; set => SetProperty(ref field, value); } = [];
    public BulkObservableCollection<Music> AlbumSongs { get; set => SetProperty(ref field, value); } = [];
    public BulkObservableCollection<Music> ArtistSongs { get; set => SetProperty(ref field, value); } = [];
    public BulkObservableCollection<Music> FolderSongs { get; set => SetProperty(ref field, value); } = [];
    public Visibility LibraryEmptyVisibility { get; set => SetProperty(ref field, value); } = Visibility.Collapsed;
    public Visibility FavoriteEmptyVisibility { get; set => SetProperty(ref field, value); } = Visibility.Collapsed;
}
