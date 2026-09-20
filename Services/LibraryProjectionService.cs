using CommunityToolkit.WinUI;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Data;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using WinUIMusicPlayer.Extensions;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.State;
using WinUIMusicPlayer.Utils;
using static WinUIMusicPlayer.Utils.ToolUtils;

namespace WinUIMusicPlayer.Services;

public enum SongViewType
{
    All,
    Album,
    Artist,
    Folder,
    Favorite
}

/// <summary>音乐库展示查询的所有者；UI 线程读取稳定库版本，只发布当前查询结果。</summary>
public sealed class LibraryProjectionService(AppState state, LibraryQueries queries, ILogger<LibraryProjectionService> logger)
{
    private List<Music> SongsSource => state.Library.Songs;
    private string SearchText => state.Browse.SearchText;
    private SortOption SelectedSortOption => state.Browse.SelectedSortOption;
    private int CurrentPlayListId => state.Browse.CurrentPlayListId;
    // 仅缓存固定页面投影的键，结果仍由绑定集合持有，不额外复制全库或保留第二份结果。
    private readonly Dictionary<object, QueryKey> _published = new();
    private readonly record struct QueryKey(long Version, string Search, string Sort, string? Group, string ArtistSymbols);
    private QueryKey Key(string? group = null) => new(state.Library.Version, SearchText,
        SelectedSortOption?.Tag?.ToString() ?? "DefaultOrder", group, AppSettings.ArtistSplitSymbols);
    private QueryKey SongKey(SongViewType kind) => Key(kind switch
    {
        SongViewType.Album => state.Browse.CurrentAlbumObj?.Album,
        SongViewType.Artist => state.Browse.CurrentArtistObj?.Author,
        SongViewType.Folder => state.Browse.CurrentFolderObj?.LastLevelFolderPath,
        _ => null
    });
    public async Task UpdateSongCollectionsAsync(
    BulkObservableCollection<Music> targetCollection,
    SongViewType viewType,
    Func<Music, bool>? filterPredicate = null)
    {
        var key = SongKey(viewType);
        if (_published.TryGetValue(targetCollection, out var existing) && existing == key) return;
        Func<Music, bool>? searchPredicate = null;
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            searchPredicate = viewType switch
            {
                SongViewType.Album => m =>
                    (m.Title?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (m.Author?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (m.Album?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false),
                SongViewType.Artist => m =>
                    (m.Title?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (m.Album?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (m.Author?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false),
                SongViewType.Folder => m =>
                    (m.Title?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (m.Album?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (m.Author?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (m.LastLevelFolderPath?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false),
                _ => m =>
                    (m.Title?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (m.Album?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (m.Author?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false)
            };
        }

        Func<Music, bool>? combinedPredicate = filterPredicate != null && searchPredicate != null
            ? m => filterPredicate(m) && searchPredicate(m)
            : filterPredicate ?? searchPredicate;

        var srcSpan = CollectionsMarshal.AsSpan(SongsSource);
        var pool = ArrayPool<Music>.Shared;
        var buf = pool.Rent(Math.Max(srcSpan.Length, 1));
        int count = 0;
        try
        {
            if (combinedPredicate != null)
            {
                for (int i = 0; i < srcSpan.Length; i++)
                {
                    if (combinedPredicate(srcSpan[i]))
                        buf[count++] = srcSpan[i];
                }
            }
            else
            {
                srcSpan.CopyTo(buf);
                count = srcSpan.Length;
            }

            var slice = buf.AsSpan(0, count);
            var tag = SelectedSortOption?.Tag?.ToString() ?? "DefaultOrder";
            IComparer<Music> comparer;
            if (tag == "DefaultOrder")
            {
                comparer = viewType switch
                {
                    SongViewType.Album => _songByDiskTrack,
                    SongViewType.Artist or SongViewType.Folder => _songByAlbumDiskTrack,
                    SongViewType.Favorite => _songByOrderDesc,
                    _ => _songByTitle
                };
            }
            else
            {
                comparer = tag switch
                {
                    "A-Z" => _songByTitle,
                    "Artist" => _songByAuthor,
                    "Album" => _songByAlbumTrack,
                    "CreateTimeASC" => _songByCreateTimeAsc,
                    "CreateTimeDESC" => _songByCreateTimeDesc,
                    "UpdateTimeASC" => _songByUpdateTimeAsc,
                    "UpdateTimeDESC" => _songByUpdateTimeDesc,
                    _ => _songByTitle
                };
            }
            slice.Sort(comparer);

            await App.MainWindow.DispatcherQueue.EnqueueAsync(() =>
            {
                if (state.Lifecycle.Phase == AppPhase.Stopping || SongKey(viewType) != key) return;
                targetCollection.FillFrom(buf.AsSpan(0, count));
                _published[targetCollection] = key;
            });
        }
        finally
        {
            pool.Return(buf, clearArray: true);
        }
    }

    public void UpdateGroupedByFirstLetter(Func<Music, string> distinctSelector, Func<Music, string> groupSelector, CollectionViewSource source)
    {
        if (_published.TryGetValue(source, out var existing) && existing == Key()) return;
        try
        {
            var srcSpan = CollectionsMarshal.AsSpan(SongsSource);
            bool hasSearch = !string.IsNullOrWhiteSpace(SearchText);
            var search = SearchText;

            var distinctMap = new Dictionary<string, Music>(srcSpan.Length / 4 + 1);
            for (int i = 0; i < srcSpan.Length; i++)
            {
                ref readonly var m = ref srcSpan[i];
                if (hasSearch && !MatchesSearchShape(m, search))
                    continue;

                var key = distinctSelector(m);
                distinctMap.TryAdd(key, m);
            }

            PublishGroupedSource(distinctMap, distinctSelector, groupSelector, source);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, ex.Message);
        }
    }

    public void UpdateArtistGroupedByFirstLetter(CollectionViewSource source)
    {
        if (_published.TryGetValue(source, out var existing) && existing == Key()) return;
        try
        {
            var srcSpan = CollectionsMarshal.AsSpan(SongsSource);
            bool hasSearch = !string.IsNullOrWhiteSpace(SearchText);
            var search = SearchText;

            var distinctMap = new Dictionary<string, Music>(srcSpan.Length / 4 + 1);
            for (int i = 0; i < srcSpan.Length; i++)
            {
                ref readonly var m = ref srcSpan[i];
                if (hasSearch && !MatchesSearchShape(m, search))
                    continue;

                var names = ArtistHelper.GetArtistNames(m.Author);
                for (int n = 0; n < names.Length; n++)
                {
                    if (!distinctMap.ContainsKey(names[n]))
                        distinctMap[names[n]] = ArtistHelper.CreateArtistTile(m, names[n]);
                }
            }

            PublishGroupedSource(distinctMap, m => m.Author, m => GetFirstLetterAdvanced(m.Author), source);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, ex.Message);
        }
    }

    private static bool MatchesSearchShape(Music m, string search)
    {
        return (m.Title?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
               (m.Album?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
               (m.Author?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
               (m.LastLevelFolderPath?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private void PublishGroupedSource(Dictionary<string, Music> distinctMap, Func<Music, string> distinctSelector, Func<Music, string> groupSelector, CollectionViewSource source)
    {
        var key = Key();
        var pool = ArrayPool<Music>.Shared;
        var buf = pool.Rent(Math.Max(distinctMap.Count, 1));
        int count = 0;
        try
        {
            foreach (var kvp in distinctMap)
                buf[count++] = kvp.Value;

            var slice = buf.AsSpan(0, count);
            IComparer<Music> comparer = (SelectedSortOption.Tag as string) switch
            {
                "A-Z" => _songByTitle,
                "Artist" => _songByAuthor,
                "Album" => _songByAlbum,
                "CreateTimeASC" => _songByCreateTimeAsc,
                "CreateTimeDESC" => _songByCreateTimeDesc,
                "UpdateTimeASC" => _songByUpdateTimeAsc,
                "UpdateTimeDESC" => _songByUpdateTimeDesc,
                _ => Comparer<Music>.Create((a, b) => CjkStringComparer.Compare(distinctSelector(a), distinctSelector(b))),
            };
            slice.Sort(comparer);

            var groupDict = new Dictionary<string, List<Music>>(count / 4 + 1);
            for (int i = 0; i < count; i++)
            {
                var gKey = groupSelector(buf[i]);
                if (!groupDict.TryGetValue(gKey, out var list))
                {
                    list = new List<Music>();
                    groupDict[gKey] = list;
                }
                list.Add(buf[i]);
            }

            var groups = new List<MusicGroup>(groupDict.Count);
            foreach (var kvp in groupDict)
                groups.Add(new MusicGroup(kvp.Key, kvp.Value));
            groups.Sort((a, b) => string.Compare(
                a.Key == "ZZZ" ? "#" : a.Key,
                b.Key == "ZZZ" ? "#" : b.Key,
                StringComparison.Ordinal));

            App.MainWindow.DispatcherQueue.TryEnqueue(() =>
            {
                if (state.Lifecycle.Phase == AppPhase.Stopping || Key() != key) return;
                source.Source = groups;
                _published[source] = key;
            });
        }
        finally
        {
            pool.Return(buf, clearArray: true);
        }
    }

    public async Task RefreshPlayListSongMapping(BulkObservableCollection<PlayListMusicItem> PlayListSongs)
    {
        var plmSpan = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(AppData.AllPlayListMusics);
        int plmCount = plmSpan.Length;
        int curListId = CurrentPlayListId;
        var search = SearchText;
        bool hasSearch = !string.IsNullOrWhiteSpace(search);

        int upper = plmCount;
        var pool = System.Buffers.ArrayPool<PlayListMusicItem>.Shared;
        var buf = pool.Rent(upper);
        int written = 0;
        try
        {
            for (int i = 0; i < plmCount; i++)
            {
                ref readonly var plm = ref plmSpan[i];
                if (plm.PlayListId != curListId) continue;
                if (!queries.TryFindById(plm.MusicId, out var m) || m is null) continue;
                if (hasSearch && !MusicMatchesSearch(m, search)) continue;
                buf[written++] = new PlayListMusicItem { Music = m, PlayListOrder = plm.Order };
            }

            var slice = buf.AsSpan(0, written);
            slice.Sort(_byPlayListOrderDesc);

            await App.MainWindow.DispatcherQueue.EnqueueAsync(() =>
            {
                PlayListSongs.FillFrom(buf.AsSpan(0, written));
            });
        }
        finally
        {
            pool.Return(buf, clearArray: true);
        }
    }


    internal static bool MusicMatchesSearch(Music m, string search)
    {
        return (m.Title is not null && m.Title.Contains(search, StringComparison.OrdinalIgnoreCase)) ||
               (m.Album is not null && m.Album.Contains(search, StringComparison.OrdinalIgnoreCase)) ||
               (m.Author is not null && m.Author.Contains(search, StringComparison.OrdinalIgnoreCase));
    }

    private static readonly System.Collections.Generic.IComparer<PlayListMusicItem> _byPlayListOrderDesc =
        Comparer<PlayListMusicItem>.Create((a, b) => b.PlayListOrder.CompareTo(a.PlayListOrder));
    private static readonly System.Collections.Generic.IComparer<PlayListMusicItem> _byMusicTitleAsc =
        Comparer<PlayListMusicItem>.Create((a, b) => CjkStringComparer.Compare(a.Music?.Title, b.Music?.Title));
    private static readonly System.Collections.Generic.IComparer<PlayListMusicItem> _byMusicAuthorAsc =
        Comparer<PlayListMusicItem>.Create((a, b) => CjkStringComparer.Compare(a.Music?.Author, b.Music?.Author));
    private static readonly System.Collections.Generic.IComparer<PlayListMusicItem> _byMusicAlbumAsc =
        Comparer<PlayListMusicItem>.Create((a, b) => CjkStringComparer.Compare(a.Music?.Album, b.Music?.Album));
    private static readonly System.Collections.Generic.IComparer<PlayListMusicItem> _byMusicCreateTimeAsc =
        Comparer<PlayListMusicItem>.Create((a, b) => a.Music?.CreateTime.CompareTo(b.Music?.CreateTime) ?? 0);
    private static readonly System.Collections.Generic.IComparer<PlayListMusicItem> _byMusicCreateTimeDesc =
        Comparer<PlayListMusicItem>.Create((a, b) => b.Music?.CreateTime.CompareTo(a.Music?.CreateTime) ?? 0);
    private static readonly System.Collections.Generic.IComparer<PlayListMusicItem> _byMusicUpdateTimeDesc =
        Comparer<PlayListMusicItem>.Create((a, b) => b.Music?.UpdateTime.CompareTo(a.Music?.UpdateTime) ?? 0);
    private static readonly System.Collections.Generic.IComparer<PlayListMusicItem> _byMusicUpdateTimeAsc =
        Comparer<PlayListMusicItem>.Create((a, b) => a.Music?.UpdateTime.CompareTo(b.Music?.UpdateTime) ?? 0);

    private static readonly IComparer<Music> _songByTitle =
        Comparer<Music>.Create((a, b) => CjkStringComparer.Compare(a.Title, b.Title));
    private static readonly IComparer<Music> _songByAuthor =
        Comparer<Music>.Create((a, b) => CjkStringComparer.Compare(a.Author, b.Author));
    private static readonly IComparer<Music> _songByAlbum =
        Comparer<Music>.Create((a, b) => CjkStringComparer.Compare(a.Album, b.Album));
    private static readonly IComparer<Music> _songByAlbumTrack =
        Comparer<Music>.Create((a, b) =>
        {
            int c = CjkStringComparer.Compare(a.Album, b.Album);
            return c != 0 ? c : a.TrackNumber.CompareTo(b.TrackNumber);
        });
    private static readonly IComparer<Music> _songByDiskTrack =
        Comparer<Music>.Create((a, b) =>
        {
            int c = a.DiskNumber.CompareTo(b.DiskNumber);
            return c != 0 ? c : a.TrackNumber.CompareTo(b.TrackNumber);
        });
    private static readonly IComparer<Music> _songByAlbumDiskTrack =
        Comparer<Music>.Create((a, b) =>
        {
            int c = CjkStringComparer.Compare(a.Album, b.Album);
            if (c != 0) return c;
            c = a.DiskNumber.CompareTo(b.DiskNumber);
            return c != 0 ? c : a.TrackNumber.CompareTo(b.TrackNumber);
        });
    private static readonly IComparer<Music> _songByOrderDesc =
        Comparer<Music>.Create((a, b) => b.Order.CompareTo(a.Order));
    private static readonly IComparer<Music> _songByCreateTimeAsc =
        Comparer<Music>.Create((a, b) => a.CreateTime.CompareTo(b.CreateTime));
    private static readonly IComparer<Music> _songByCreateTimeDesc =
        Comparer<Music>.Create((a, b) => b.CreateTime.CompareTo(a.CreateTime));
    private static readonly IComparer<Music> _songByUpdateTimeAsc =
        Comparer<Music>.Create((a, b) => a.UpdateTime.CompareTo(b.UpdateTime));
    private static readonly IComparer<Music> _songByUpdateTimeDesc =
        Comparer<Music>.Create((a, b) => b.UpdateTime.CompareTo(a.UpdateTime));

}
