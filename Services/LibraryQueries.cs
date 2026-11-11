using System;
using System.Collections.Generic;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.State;

namespace WinUIMusicPlayer.Services;

/// <summary>按库版本惰性重建索引；UI 线程使用，同一版本的查询不再扫描音乐库。</summary>
public sealed class LibraryQueries(LibraryState library)
{
    private readonly Dictionary<int, Music> _idIndex = new(capacity: 16384);
    private readonly Dictionary<string, Music> _firstAlbumIndex = new(capacity: 4096, StringComparer.Ordinal);
    private readonly Dictionary<string, Music> _firstArtistIndex = new(capacity: 4096, StringComparer.Ordinal);
    private readonly Dictionary<string, int> _albumSongCounts = new(capacity: 4096, StringComparer.Ordinal);
    private readonly Dictionary<int, List<Music>> _songVersions = [];
    private readonly Dictionary<int, Music> _firstVersionBySong = [];
    private readonly Dictionary<(int Song, int Source), Music> _displayVersions = [];
    private long _indexedVersion = -1;
    private int _sourceFilter = -1;
    public void SetSourceFilter(int sourceId) { _sourceFilter = sourceId; _indexedVersion = -1; }
    public static bool MatchesSource(Music music, int sourceId) => sourceId == -1 || (sourceId == -2 ? music.IsRemote : music.SourceId == sourceId);
    internal IReadOnlyDictionary<int, Music> GetIdIndex()
    {
        if (_indexedVersion != library.Version) RebuildIdIndex();
        return _idIndex;
    }

    public Music? FindById(int id)
    {
        if (_indexedVersion != library.Version) RebuildIdIndex();
        return _idIndex.TryGetValue(id, out var m) ? m : null;
    }

    public bool TryFindById(int id, out Music? music)
    {
        if (_indexedVersion != library.Version) RebuildIdIndex();
        return _idIndex.TryGetValue(id, out music);
    }

    public Music? FindFirstByAlbum(string? album)
    {
        if (string.IsNullOrEmpty(album)) return null;
        if (_indexedVersion != library.Version) RebuildIdIndex();
        return _firstAlbumIndex.TryGetValue(album, out var m) ? m : null;
    }

    public Music? FindFirstByArtist(string? artist)
    {
        if (string.IsNullOrEmpty(artist)) return null;
        if (_indexedVersion != library.Version) RebuildIdIndex();
        if (_firstArtistIndex.TryGetValue(artist, out var m)) return m;
        var names = ArtistHelper.GetArtistNames(artist);
        for (int i = 0; i < names.Length; i++)
        {
            if (_firstArtistIndex.TryGetValue(names[i], out m)) return m;
        }
        return null;
    }

    public void Invalidate() => library.NotifyChanged();

    /// <summary>只有多版本歌曲返回成员列表；遍历整库时无需为单文件歌曲分配数组。</summary>
    public IReadOnlyList<Music>? GetMultipleVersions(int songId)
    {
        if (_indexedVersion != library.Version) RebuildIdIndex();
        return _songVersions.GetValueOrDefault(songId);
    }

    /// <summary>获取同组版本索引；仅在库版本变化时重建。</summary>
    public IReadOnlyList<Music> GetVersions(int songId)
    {
        if (_indexedVersion != library.Version) RebuildIdIndex();
        return _songVersions.TryGetValue(songId, out var versions) ? versions
            : _firstVersionBySong.TryGetValue(songId, out var single) ? new[] { single } : Array.Empty<Music>();
    }

    /// <summary>获取范围内稳定代表版本，不探网、不跟随一次播放回退。</summary>
    public Music? FindDisplayVersion(int songId, int sourceId = -1)
    {
        if (_indexedVersion != library.Version) RebuildIdIndex();
        if (_displayVersions.TryGetValue((songId, sourceId), out var cached)) return cached;
        if (!_songVersions.TryGetValue(songId, out var versions))
            return _firstVersionBySong.TryGetValue(songId, out var only) && MatchesSource(only, sourceId) ? only : null;
        List<Music> available = [];
        foreach (var version in versions) if (MatchesSource(version, sourceId)) available.Add(version);
        if (available.Count == 0) return null;
        var selected = SongVersionPolicy.Order(available, available[0].SongPreferredMusicId)[0];
        // 全组缺失时缓存可能是唯一可播副本。下一次查询应观察最新缓存状态。
        if (!selected.IsMissing || selected.SongPreferredMusicId == selected.Id)
            _displayVersions[(songId, sourceId)] = selected;
        return selected;
    }

    private void RebuildIdIndex()
    {
        _idIndex.Clear();
        _firstAlbumIndex.Clear();
        _firstArtistIndex.Clear();
        _albumSongCounts.Clear();
        _songVersions.Clear();
        _firstVersionBySong.Clear();
        _displayVersions.Clear();
        var src = library.Songs;
        for (int i = 0; i < src.Count; i++)
        {
            var m = src[i];
            if (m is null) continue;
            _idIndex[m.Id] = m;
            if (m.SongId > 0)
            {
                if (!_firstVersionBySong.TryAdd(m.SongId, m))
                {
                    if (!_songVersions.TryGetValue(m.SongId, out var versions))
                        _songVersions.Add(m.SongId, versions = [_firstVersionBySong[m.SongId]]);
                    versions.Add(m);
                }
            }
        }
        // 先完成版本索引，再按代表版本建立专辑/艺术家与计数，避免重复音频被计为多首。
        _indexedVersion = library.Version;
        var included = new HashSet<int>();
        for (int i = 0; i < src.Count; i++)
        {
            var original = src[i];
            if (original is null || !MatchesSource(original, _sourceFilter)) continue;
            if (original.SongId > 0 && !included.Add(original.SongId)) continue;
            var m = original.SongId > 0 ? FindDisplayVersion(original.SongId, _sourceFilter) ?? original : original;
            if (!string.IsNullOrEmpty(m.Album))
            {
                if (!_firstAlbumIndex.TryGetValue(m.Album, out var firstAlbum) || (firstAlbum.IsRemote && !m.IsRemote))
                    _firstAlbumIndex[m.Album] = m;
                _albumSongCounts[m.Album] = _albumSongCounts.GetValueOrDefault(m.Album) + 1;
            }
            if (!string.IsNullOrEmpty(m.Author))
                AddArtistIndexEntry(m);
        }
        _indexedVersion = library.Version;
    }

    private void AddArtistIndexEntry(Music m)
    {
        var names = ArtistHelper.GetArtistNames(m.Author);
        for (int i = 0; i < names.Length; i++)
        {
            if (!_firstArtistIndex.ContainsKey(names[i]))
                _firstArtistIndex[names[i]] = ArtistHelper.CreateArtistTile(m, names[i]);
        }
    }

    public int GetAlbumSongCount(string? album)
    {
        if (string.IsNullOrEmpty(album)) return 0;
        if (_indexedVersion != library.Version) RebuildIdIndex();
        return _albumSongCounts.TryGetValue(album, out var c) ? c : 0;
    }

}
