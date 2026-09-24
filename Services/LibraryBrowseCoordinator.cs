using System;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.State;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.View;
using WinUIMusicPlayer.ViewModel;
using static WinUIMusicPlayer.Utils.ToolUtils;
namespace WinUIMusicPlayer.Services;

/// <summary>活跃库页面的查询调度；页面通过同一 LibraryViewState 消费结果。</summary>
public sealed class LibraryBrowseCoordinator(AppState state, LibraryQueries queries, LibraryProjectionService projections) : IDisposable
{
    private DispatcherQueueTimer? _searchDebounceTimer;
    private bool _started;
    public void Start()
    {
        if (_started) return;
        _started = true;
        state.Browse.PropertyChanged += OnBrowseStateChanged;
        projections.RefreshRequested += RefreshAllViews;
        state.LibraryViews.FavoriteSongs.CollectionChanged += FavoritesChanged;
    }
    private void FavoritesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        => state.LibraryViews.FavoriteEmptyVisibility = state.LibraryViews.FavoriteSongs.Count == 0 && string.IsNullOrWhiteSpace(state.Browse.SearchText)
            ? Visibility.Visible : Visibility.Collapsed;
    private void OnBrowseStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(state.Browse.SearchText):
                if (_searchDebounceTimer is null)
                {
                    _searchDebounceTimer = App.MainWindow.DispatcherQueue.CreateTimer();
                    _searchDebounceTimer.Interval = TimeSpan.FromMilliseconds(300);
                    _searchDebounceTimer.IsRepeating = false;
                    _searchDebounceTimer.Tick += OnSearchDebounceElapsed;
                }
                _searchDebounceTimer.Stop();
                _searchDebounceTimer.Start();
                break;
            case nameof(state.Browse.SelectedSortOption):
                OnSelectSortChanged();
                break;
            case nameof(state.Browse.CurrentArtistObj):
                _ = projections.UpdateSongCollectionsAsync(state.LibraryViews.ArtistSongs, SongViewType.Artist, m => ArtistHelper.IsMusicByArtist(m, state.Browse.CurrentArtistObj?.Author ?? ""));
                break;
            case nameof(state.Browse.CurrentAlbumObj):
                _ = projections.UpdateSongCollectionsAsync(state.LibraryViews.AlbumSongs, SongViewType.Album, m => m.Album == state.Browse.CurrentAlbumObj?.Album);
                break;
            case nameof(state.Browse.CurrentFolderObj):
                _ = projections.UpdateSongCollectionsAsync(state.LibraryViews.FolderSongs, SongViewType.Folder, m => m.LastLevelFolderPath == state.Browse.CurrentFolderObj?.LastLevelFolderPath);
                break;
        }
    }

    private void OnSearchDebounceElapsed(DispatcherQueueTimer sender, object args)
    {
        RefreshDataSource();
    }

    public void RefreshDataSource()
    {
        if (!_started || state.Lifecycle.Phase == AppPhase.Stopping) return;
        RefreshDataForPageType(AppData.CurrentPage);
    }

    public void RefreshAllViews()
    {
        if (!_started || state.Lifecycle.Phase == AppPhase.Stopping) return;
        queries.Invalidate();
        // 收藏/歌单映射用于跨页操作；其他展示投影在导航到对应页时按版本重建。
        _ = projections.UpdateSongCollectionsAsync(state.LibraryViews.FavoriteSongs, SongViewType.Favorite, m => m.IsFavorite == true);
        _ = RefreshPlayListSongMapping();
        RefreshDataSource();
    }

    private void RefreshDataForPageType(Type pageType)
    {
        if (pageType == typeof(SongListPage))
        {
            _ = projections.UpdateSongCollectionsAsync(state.LibraryViews.ListSongs, SongViewType.All);
        }
        else if (pageType == typeof(AlbumPage))
        {
            _ = projections.UpdateSongCollectionsAsync(state.LibraryViews.AlbumSongs, SongViewType.Album, m => m.Album == state.Browse.CurrentAlbumObj?.Album);
            projections.UpdateGroupedByFirstLetter(m => m.Album, m => GetFirstLetterAdvanced(m.Album), state.LibraryViews.AlbumPageSource);
        }
        else if (pageType == typeof(ArtistPage))
        {
            _ = projections.UpdateSongCollectionsAsync(state.LibraryViews.ArtistSongs, SongViewType.Artist, m => ArtistHelper.IsMusicByArtist(m, state.Browse.CurrentArtistObj?.Author ?? ""));
            projections.UpdateArtistGroupedByFirstLetter(state.LibraryViews.ArtistPageSource);
        }
        else if (pageType == typeof(FolderBrowsePage))
        {
            _ = projections.UpdateSongCollectionsAsync(state.LibraryViews.FolderSongs, SongViewType.Folder, m => m.LastLevelFolderPath == state.Browse.CurrentFolderObj?.LastLevelFolderPath);
            projections.UpdateGroupedByFirstLetter(m => m.LastLevelFolderPath, m => GetFirstLetterAdvanced(m.LastLevelFolderPath), state.LibraryViews.FolderPageSource);
        }
        else if (pageType == typeof(FavouritePlayListPage))
        {
            _ = projections.UpdateSongCollectionsAsync(state.LibraryViews.FavoriteSongs, SongViewType.Favorite, m => m.IsFavorite == true);
        }
        else if (pageType == typeof(PlayListPage))
        {
            _ = RefreshPlayListSongMapping();
        }
        else
        {
            return;
        }
        App.Services.GetRequiredService<MusicBrowseViewModel>()?.UpdateViewList();
    }

    public Task RefreshPlayListSongMapping() => projections.RefreshPlayListSongMapping(state.LibraryViews.PlayListSongs);

    public void OnSelectSortChanged()
    {
        RefreshDataSource();
    }


    public void Dispose()
    {
        _started = false;
        state.Browse.PropertyChanged -= OnBrowseStateChanged;
        projections.RefreshRequested -= RefreshAllViews;
        state.LibraryViews.FavoriteSongs.CollectionChanged -= FavoritesChanged;
        if (_searchDebounceTimer is not null)
        {
            _searchDebounceTimer.Stop();
            _searchDebounceTimer.Tick -= OnSearchDebounceElapsed;
        }
    }
}
