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
    private readonly Dictionary<string, Music> _firstFolderIndex = new(capacity: 4096, StringComparer.Ordinal);
    private readonly Dictionary<string, int> _albumSongCounts = new(capacity: 4096, StringComparer.Ordinal);
    private long _indexedVersion = -1;
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

    public Music? FindFirstByFolder(string? folder)
    {
        if (string.IsNullOrEmpty(folder)) return null;
        if (_indexedVersion != library.Version) RebuildIdIndex();
        return _firstFolderIndex.TryGetValue(folder, out var m) ? m : null;
    }

    public void Invalidate() => library.NotifyChanged();

    private void RebuildIdIndex()
    {
        _idIndex.Clear();
        _firstAlbumIndex.Clear();
        _firstArtistIndex.Clear();
        _firstFolderIndex.Clear();
        _albumSongCounts.Clear();
        var src = library.Songs;
        for (int i = 0; i < src.Count; i++)
        {
            var m = src[i];
            if (m is null) continue;
            _idIndex[m.Id] = m;
            if (!string.IsNullOrEmpty(m.Album))
            {
                if (!_firstAlbumIndex.ContainsKey(m.Album))
                    _firstAlbumIndex[m.Album] = m;
                _albumSongCounts[m.Album] = _albumSongCounts.GetValueOrDefault(m.Album) + 1;
            }
            if (!string.IsNullOrEmpty(m.Author))
                AddArtistIndexEntry(m);
            if (!string.IsNullOrEmpty(m.LastLevelFolderPath) && !_firstFolderIndex.ContainsKey(m.LastLevelFolderPath))
                _firstFolderIndex[m.LastLevelFolderPath] = m;
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
