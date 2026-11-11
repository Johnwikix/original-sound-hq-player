using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Extensions;
using static WinUIMusicPlayer.Utils.ToolUtils;

namespace WinUIMusicPlayer.State;

/// <summary>规范队列与当前遍历顺序的唯一所有者。修改只通过队列操作，在 UI 线程发布。</summary>
public sealed class PlaybackQueueState : ObservableObject
{
    private readonly record struct Entry(long Id, Music Music);
    private List<Entry> _entries = [];
    private List<Entry> _order = [];
    private long _nextId;
    private long _currentEntryId;
    private readonly Dictionary<long, int> _requestedVersions = [];
    private readonly Dictionary<long, int> _sourceScopes = [];
    private readonly Dictionary<long, int> _playlistEntries = [];
    private bool _mutating;
    public long CurrentEntryId => _currentEntryId;
    /// <summary>Published after a complete order mutation, once per bulk operation.</summary>
    public long Version { get; private set; }

    private void CommitOrderChange()
    {
        Version++;
        OnPropertyChanged(nameof(Version));
    }
    public PlaybackQueueState()
    {
        Playing = Sequential;
        Sequential.CollectionChanged += SequentialChanged;
    }
    public long EntryIdAt(int index) => index >= 0 && index < _order.Count ? _order[index].Id : 0;
    public void SelectEntry(long id, Music music)
    {
        foreach (var entry in _order)
            if (entry.Id == id && SameSong(entry.Music, music)) { _currentEntryId = id; return; }
        int index = IndexOf(music);
        _currentEntryId = EntryIdAt(index);
    }
    // Snapshots are cold save-path allocations; playback navigation only scans the existing entry array.
    public long[] CaptureEntryIds() => _entries.Select(e => e.Id).ToArray();
    public long[] CaptureOrderIds() => _order.Select(e => e.Id).ToArray();
    public int[] CaptureMusicIds() => _entries.Select(e => e.Music.Id).ToArray();
    /// <summary>保存每个出现项的精确版本意图。</summary>
    public int[] CaptureRequestedVersions() => _entries.Select(e => _requestedVersions.GetValueOrDefault(e.Id)).ToArray();
    /// <summary>保存来源范围。</summary>
    public int[] CaptureSourceScopes() => _entries.Select(e => _sourceScopes.GetValueOrDefault(e.Id, -1)).ToArray();
    /// <summary>保存来源歌单出现项。</summary>
    public int[] CapturePlaylistEntries() => _entries.Select(e => _playlistEntries.GetValueOrDefault(e.Id)).ToArray();
    /// <summary>获取来源歌单关系。</summary>
    public int PlaylistEntry(long entryId) => _playlistEntries.GetValueOrDefault(entryId);
    /// <summary>查询精确版本，零表示按歌曲策略。</summary>
    public int RequestedVersion(long entryId) => _requestedVersions.GetValueOrDefault(entryId);
    /// <summary>查询出现项的来源范围。</summary>
    public int SourceScope(long entryId) => _sourceScopes.GetValueOrDefault(entryId, -1);
    /// <summary>设置一个出现项的精确版本。</summary>
    public void SetRequestedVersion(long entryId, int versionId)
    {
        if (entryId <= 0 || !_entries.Exists(entry => entry.Id == entryId)) return;
        if (versionId > 0) _requestedVersions[entryId] = versionId;
        else _requestedVersions.Remove(entryId);
        CommitOrderChange();
    }
    /// <summary>为来源页面建立的队列保存范围，文件夹可固定全部文件。</summary>
    public void ConfigureSource(int sourceId, bool exactFiles = false)
    {
        foreach (var entry in _entries)
        {
            _sourceScopes[entry.Id] = sourceId;
            if (exactFiles) _requestedVersions[entry.Id] = entry.Music.Id;
        }
        CommitOrderChange();
    }
    /// <summary>配置歌单出现项的固定版本，保留重复位置。</summary>
    public void ConfigurePlaylist(int[] playlistEntries, int[] requestedVersions, int sourceScope)
    {
        if (playlistEntries.Length != _entries.Count || requestedVersions.Length != _entries.Count) return;
        for (int i = 0; i < _entries.Count; i++)
        {
            long id = _entries[i].Id;
            _playlistEntries[id] = playlistEntries[i];
            _sourceScopes[id] = sourceScope;
            if (requestedVersions[i] > 0) _requestedVersions[id] = requestedVersions[i];
        }
        CommitOrderChange();
    }
    public void RestoreEntries(long[]? ids, long[]? order, int[]? musicIds, long currentId,
        int[]? requestedVersions = null, int[]? sourceScopes = null, bool legacyExact = false, int[]? playlistEntries = null)
    {
        if (ids is null || order is null || musicIds is null || ids.Length != Sequential.Count || musicIds.Length != ids.Length) return;
        var seen = new HashSet<long>();
        for (int i = 0; i < ids.Length; i++)
            if (ids[i] <= 0 || !seen.Add(ids[i]) || Sequential[i].Id != musicIds[i]) return;
        if (order.Length != ids.Length || !seen.SetEquals(order) || order.Distinct().Count() != order.Length) return;
        _entries = new(ids.Length);
        _requestedVersions.Clear();
        _sourceScopes.Clear();
        _playlistEntries.Clear();
        var byId = new Dictionary<long, Entry>(ids.Length);
        for (int i = 0; i < ids.Length; i++)
        {
            var entry = new Entry(ids[i], Sequential[i]);
            _entries.Add(entry);
            byId.Add(entry.Id, entry);
            _nextId = Math.Max(_nextId, entry.Id);
            int requested = requestedVersions?.Length == ids.Length ? requestedVersions[i] : legacyExact ? musicIds[i] : 0;
            if (requested > 0) _requestedVersions[entry.Id] = requested;
            if (sourceScopes?.Length == ids.Length) _sourceScopes[entry.Id] = sourceScopes[i];
            if (playlistEntries?.Length == ids.Length) _playlistEntries[entry.Id] = playlistEntries[i];
        }
        _currentEntryId = seen.Contains(currentId) ? currentId : 0;
        _order = Mode == PlayMode.RandomLoop ? order.Select(id => byId[id]).ToList() : new(_entries);
        PublishOrder();
    }
    /// <summary>库刷新按条目批量替换引用，保留顺序和游标；避免每首更新都重建整条队列。</summary>
    public void ReconcileLibrary(IReadOnlyDictionary<int, Music> songs, Music? current)
    {
        var updated = new Dictionary<long, Entry>(_entries.Count);
        int written = 0;
        for (int i = 0; i < _entries.Count; i++)
        {
            var entry = _entries[i];
            if (entry.Music.Id != current?.Id)
            {
                if (!songs.TryGetValue(entry.Music.Id, out var music)) continue;
                entry = entry with { Music = music };
            }
            _entries[written++] = entry;
            updated.Add(entry.Id, entry);
        }
        _entries.RemoveRange(written, _entries.Count - written);
        written = 0;
        for (int i = 0; i < _order.Count; i++)
            if (updated.TryGetValue(_order[i].Id, out var entry)) _order[written++] = entry;
        _order.RemoveRange(written, _order.Count - written);
        Sequential.CollectionChanged -= SequentialChanged;
        Sequential = new(_entries.Select(entry => entry.Music));
        Sequential.CollectionChanged += SequentialChanged;
        OnPropertyChanged(nameof(Sequential));
        PublishOrder();
    }
    private void SequentialChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_mutating) return;
        if (e.Action == NotifyCollectionChangedAction.Remove && e.OldStartingIndex >= 0)
            _entries.RemoveRange(e.OldStartingIndex, e.OldItems!.Count);
        else if (e.Action == NotifyCollectionChangedAction.Move && e.OldItems?.Count == 1)
        {
            var moved = _entries[e.OldStartingIndex];
            _entries.RemoveAt(e.OldStartingIndex);
            _entries.Insert(e.NewStartingIndex, moved);
        }
        // Compatibility collection mutations retain identities by occurrence in O(n).
        var available = new Dictionary<Music, Queue<Entry>>(ReferenceEqualityComparer.Instance);
        foreach (var entry in _entries)
        {
            if (!available.TryGetValue(entry.Music, out var bucket)) available.Add(entry.Music, bucket = new());
            bucket.Enqueue(entry);
        }
        var updated = new List<Entry>(Sequential.Count);
        foreach (var music in Sequential)
            updated.Add(available.TryGetValue(music, out var bucket) && bucket.Count > 0 ? bucket.Dequeue() : new(++_nextId, music));
        _entries = updated;
        if (Mode == PlayMode.RandomLoop)
        {
            var remaining = new HashSet<long>(_entries.Select(entry => entry.Id));
            _order.RemoveAll(entry => !remaining.Contains(entry.Id));
            foreach (var entry in _order) remaining.Remove(entry.Id);
            foreach (var entry in _entries) if (remaining.Contains(entry.Id)) _order.Add(entry);
            PublishOrder();
        }
        else
        {
            _order = new(_entries);
            PruneEntryOptions();
            CommitOrderChange();
        }
    }
    private void PublishOrder()
    {
        PruneEntryOptions();
        Playing = Mode == PlayMode.RandomLoop ? new(_order.Select(entry => entry.Music)) : Sequential;
        OnPropertyChanged(nameof(Playing));
        CommitOrderChange();
    }

    private void PruneEntryOptions()
    {
        if (_requestedVersions.Count == 0 && _sourceScopes.Count == 0 && _playlistEntries.Count == 0) return;
        var retained = new HashSet<long>(_entries.Select(static entry => entry.Id));
        foreach (var key in _requestedVersions.Keys.ToArray()) if (!retained.Contains(key)) _requestedVersions.Remove(key);
        foreach (var key in _sourceScopes.Keys.ToArray()) if (!retained.Contains(key)) _sourceScopes.Remove(key);
        foreach (var key in _playlistEntries.Keys.ToArray()) if (!retained.Contains(key)) _playlistEntries.Remove(key);
    }
    public BulkObservableCollection<Music> Sequential { get; private set; } = [];
    public BulkObservableCollection<Music> Playing { get; private set; } = [];
    private PlayMode _mode = PlayMode.ListLoop;
    public PlayMode Mode
    {
        get => _mode;
        set
        {
            if (!SetProperty(ref _mode, value)) return;
            RebuildOrder();
        }
    }

    public void Replace(BulkObservableCollection<Music>? songs)
    {
        if (ReferenceEquals(Sequential, songs)) return;
        Sequential.CollectionChanged -= SequentialChanged;
        Sequential = songs ?? [];
        Sequential.CollectionChanged += SequentialChanged;
        _entries = new(Sequential.Count);
        _requestedVersions.Clear();
        _sourceScopes.Clear();
        _playlistEntries.Clear();
        foreach (var music in Sequential) _entries.Add(new(++_nextId, music));
        _currentEntryId = 0;
        OnPropertyChanged(nameof(Sequential));
        RebuildOrder();
    }

    private void RebuildOrder()
    {
        _order = new(_entries);
        if (Mode == PlayMode.RandomLoop)
            for (int i = _order.Count - 1; i > 0; i--)
            {
                int j = Random.Shared.Next(i + 1);
                (_order[i], _order[j]) = (_order[j], _order[i]);
            }
        PublishOrder();
    }

    /// <summary>优先对象身份；库外 Id=0 必须比较路径，不能把任意外部文件视为同一曲。</summary>
    public int IndexOf(Music? current)
    {
        if (current is null) return -1;
        for (int i = 0; i < _order.Count; i++)
            if (_order[i].Id == _currentEntryId && SameSong(_order[i].Music, current)) return i;
        for (int i = 0; i < Playing.Count; i++)
            if (ReferenceEquals(Playing[i], current)) return i;
        for (int i = 0; i < Playing.Count; i++)
            if (current.Id != 0 ? Playing[i].Id == current.Id
                : Playing[i].Id == 0 && string.Equals(Playing[i].Path, current.Path, StringComparison.OrdinalIgnoreCase)) return i;
        if (current.SongId > 0)
            for (int i = 0; i < Playing.Count; i++) if (Playing[i].SongId == current.SongId) return i;
        return -1;
    }

    private static bool SameSong(Music left, Music right) => ReferenceEquals(left, right) || left.Id > 0 && left.Id == right.Id ||
        left.SongId > 0 && left.SongId == right.SongId;

    public void InsertNext(Music? current, IEnumerable<Music> songs)
    {
        List<Music> batch = [];
        foreach (var song in songs) if (song is not null) batch.Add(song);
        if (batch.Count == 0) return;
        int index = IndexOf(current);
        int insertion = index < 0 ? Playing.Count : index + 1;
        var entries = new List<Entry>(batch.Count);
        foreach (var music in batch) entries.Add(new(++_nextId, music));
        _mutating = true;
        try
        {
            if (ReferenceEquals(Sequential, Playing))
            {
                _entries.InsertRange(insertion, entries);
                _order.InsertRange(insertion, entries);
                Sequential.InsertRange(insertion, batch);
            }
            else
            {
                _entries.AddRange(entries);
                _order.InsertRange(insertion, entries);
                Sequential.AddRange(batch);
                Playing.InsertRange(insertion, batch);
            }
        }
        finally { _mutating = false; }
        CommitOrderChange();
    }
}
