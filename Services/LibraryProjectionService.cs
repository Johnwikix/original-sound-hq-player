using CommunityToolkit.WinUI;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Data;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Threading;
using System.Linq;
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
    public event Action? RefreshRequested;
    public void RequestRefresh() => RefreshRequested?.Invoke();
    private List<Music> SongsSource => state.Library.Songs;
    private string SearchText => state.Browse.SearchText;
    private SortOption SelectedSortOption => state.Browse.SelectedSortOption;
    private int CurrentPlayListId => state.Browse.CurrentPlayListId;
    // 仅缓存固定页面投影的键，结果仍由绑定集合持有，不额外复制全库或保留第二份结果。
    private readonly Dictionary<object, QueryKey> _published = new();
    private readonly record struct QueryKey(long Version, string Search, string Sort, string? Group, string ArtistSymbols, int Source);
    private QueryKey Key(string? group = null) => new(state.Library.Version, SearchText,
        SelectedSortOption?.Tag?.ToString() ?? "DefaultOrder", group, AppSettings.ArtistSplitSymbols, state.Browse.SourceFilterId);
    private QueryKey SongKey(SongViewType kind) => Key(kind switch
    {
        SongViewType.Album => state.Browse.CurrentAlbumObj?.Album,
        SongViewType.Artist => state.Browse.CurrentArtistObj?.Author,
        SongViewType.Folder => state.Browse.CurrentFolderObj?.LastLevelFolderPath,
        _ => null
    });
    private readonly Dictionary<BulkObservableCollection<Music>, (SongViewType Kind, Func<Music, bool>? Filter)> _requested = new();
    private readonly Dictionary<BulkObservableCollection<Music>, Task> _inflight = new();
    private readonly SemaphoreSlim _computeGate = new(1, 1);
    private bool _stopped;

    /// <summary>同一列表最多一个在途计算；新请求替换待执行参数，不为每次输入复制音乐库。</summary>
    public Task UpdateSongCollectionsAsync(BulkObservableCollection<Music> target, SongViewType kind, Func<Music, bool>? filterPredicate = null)
    {
        if (_stopped || state.Lifecycle.Phase == AppPhase.Stopping) return Task.CompletedTask;
        var dispatcher = App.MainWindow.DispatcherQueue;
        if (!dispatcher.HasThreadAccess)
            return dispatcher.EnqueueAsync(() => UpdateSongCollectionsAsync(target, kind, filterPredicate));
        _requested[target] = (kind, filterPredicate);
        if (_inflight.TryGetValue(target, out var pending)) return pending;
        if (_published.TryGetValue(target, out var published) && published == SongKey(kind)) return Task.CompletedTask;
        return _inflight[target] = RefreshSongsAsync(target);
    }

    private async Task RefreshSongsAsync(BulkObservableCollection<Music> target)
    {
        await Task.Yield(); // 登记任务后才执行，避免同步完成留下已完成任务条目。
        try
        {
            while (!_stopped && state.Lifecycle.Phase != AppPhase.Stopping)
            {
                await _computeGate.WaitAsync();
                try
                {
                    if (_stopped || state.Lifecycle.Phase == AppPhase.Stopping) return;
                    var request = _requested[target];
                    var key = SongKey(request.Kind);
                    if (_published.TryGetValue(target, out var published) && published == key) return;
                    var rows = ArrayPool<SongRow>.Shared.Rent(Math.Max(1, SongsSource.Count));
                    var result = ArrayPool<Music>.Shared.Rent(Math.Max(1, SongsSource.Count));
                    int count = 0;
                    try
                    {
                        // 在 UI 线程复制字段；后台比较器绝不读取可变 Music 属性或 UI 集合。
                        foreach (var music in SongsSource)
                        {
                            if (!LibraryQueries.MatchesSource(music, key.Source) || (request.Filter is not null && !request.Filter(music))) continue;
                            rows[count++] = new SongRow(music);
                        }
                        int written = await Task.Run(() =>
                        {
                            int matches = 0;
                            for (int i = 0; i < count; i++)
                                if (rows[i].Matches(key.Search, request.Kind == SongViewType.Folder)) rows[matches++] = rows[i];
                            Array.Sort(rows, 0, matches, SongRow.Comparer(key.Sort, request.Kind));
                            for (int i = 0; i < matches; i++) result[i] = rows[i].Music;
                            return matches;
                        });
                        if (_stopped || state.Lifecycle.Phase == AppPhase.Stopping) return;
                        if (SongKey(request.Kind) == key && _requested[target].Kind == request.Kind)
                        {
                            target.FillFrom(result.AsSpan(0, written));
                            _published[target] = key;
                            return;
                        }
                    }
                    finally
                    {
                        ArrayPool<SongRow>.Shared.Return(rows, clearArray: true);
                        ArrayPool<Music>.Shared.Return(result, clearArray: true);
                    }
                }
                finally { _computeGate.Release(); }
            }
        }
        catch (Exception ex) { logger.LogError(ex, "刷新音乐列表失败"); }
        finally { _inflight.Remove(target); }
    }

    public async Task StopAsync()
    {
        _stopped = true;
        await Task.WhenAll(_inflight.Values.ToArray());
        _requested.Clear();
        _published.Clear();
    }

    private readonly record struct SongRow(Music Music, string? Title, string? Author, string? Album, string? Folder,
        int Disk, int Track, int Order, DateTime Created, DateTime Updated)
    {
        public SongRow(Music music) : this(music, music.Title, music.Author, music.Album, music.LastLevelFolderPath,
            music.DiskNumber, music.TrackNumber, music.Order, music.CreateTime, music.UpdateTime)
        { }
        public bool Matches(string search, bool folder) => string.IsNullOrWhiteSpace(search) ||
            (Title?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
            (Author?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
            (Album?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
            (folder && (Folder?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false));
        public static IComparer<SongRow> Comparer(string sort, SongViewType kind) => System.Collections.Generic.Comparer<SongRow>.Create((a, b) =>
        {
            int c;
            switch (sort)
            {
                case "Artist": return CjkStringComparer.Compare(a.Author, b.Author);
                case "Album":
                    c = CjkStringComparer.Compare(a.Album, b.Album);
                    return c != 0 ? c : a.Track.CompareTo(b.Track);
                case "CreateTimeASC": return a.Created.CompareTo(b.Created);
                case "CreateTimeDESC": return b.Created.CompareTo(a.Created);
                case "UpdateTimeASC": return a.Updated.CompareTo(b.Updated);
                case "UpdateTimeDESC": return b.Updated.CompareTo(a.Updated);
                case "DefaultOrder" when kind == SongViewType.Favorite: return b.Order.CompareTo(a.Order);
                case "DefaultOrder" when kind is SongViewType.Album or SongViewType.Artist or SongViewType.Folder:
                    c = kind == SongViewType.Album ? 0 : CjkStringComparer.Compare(a.Album, b.Album);
                    if (c != 0) return c;
                    c = a.Disk.CompareTo(b.Disk);
                    return c != 0 ? c : a.Track.CompareTo(b.Track);
                default: return CjkStringComparer.Compare(a.Title, b.Title);
            }
        });
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
                if (!LibraryQueries.MatchesSource(m, state.Browse.SourceFilterId)) continue;
                if (hasSearch && !MatchesSearchShape(m, search))
                    continue;

                var key = distinctSelector(m);
                if (string.IsNullOrWhiteSpace(key)) continue;
                if (ReferenceEquals(source, state.LibraryViews.AlbumPageSource))
                {
                    // 专辑卡片按专辑名展示，不把来源作为卡片身份；详情歌曲按歌曲记录展示，
                    // 因而同一专辑在本地和 WebDAV 的不同来源曲目都会保留。
                    if (!distinctMap.TryGetValue(key, out var existingMusic) || (existingMusic.IsRemote && !m.IsRemote))
                        distinctMap[key] = m;
                }
                else
                {
                    distinctMap.TryAdd(key, m);
                }
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
                if (!LibraryQueries.MatchesSource(m, state.Browse.SourceFilterId) || string.IsNullOrWhiteSpace(m.Author)) continue;
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
                if (_stopped || state.Lifecycle.Phase == AppPhase.Stopping || Key() != key) return;
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
                if (_stopped || state.Lifecycle.Phase == AppPhase.Stopping || CurrentPlayListId != curListId || SearchText != search) return;
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
    private static readonly IComparer<Music> _songByTitle =
        Comparer<Music>.Create((a, b) => CjkStringComparer.Compare(a.Title, b.Title));
    private static readonly IComparer<Music> _songByAuthor =
        Comparer<Music>.Create((a, b) => CjkStringComparer.Compare(a.Author, b.Author));
    private static readonly IComparer<Music> _songByAlbum =
        Comparer<Music>.Create((a, b) => CjkStringComparer.Compare(a.Album, b.Album));
    private static readonly IComparer<Music> _songByCreateTimeAsc =
        Comparer<Music>.Create((a, b) => a.CreateTime.CompareTo(b.CreateTime));
    private static readonly IComparer<Music> _songByCreateTimeDesc =
        Comparer<Music>.Create((a, b) => b.CreateTime.CompareTo(a.CreateTime));
    private static readonly IComparer<Music> _songByUpdateTimeAsc =
        Comparer<Music>.Create((a, b) => a.UpdateTime.CompareTo(b.UpdateTime));
    private static readonly IComparer<Music> _songByUpdateTimeDesc =
        Comparer<Music>.Create((a, b) => b.UpdateTime.CompareTo(a.UpdateTime));

}
