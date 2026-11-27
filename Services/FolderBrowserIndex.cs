using System;
using System.Collections.Generic;
using System.IO;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Services;

/// <summary>从曲库和扫描根重建目录层级，不枚举文件系统或触发远程请求。</summary>
public sealed class FolderBrowserIndex
{
    private readonly record struct FolderKey(int SourceId, string Path);
    private sealed class FolderKeyComparer : IEqualityComparer<FolderKey>
    {
        private static StringComparer PathComparer(int sourceId) => sourceId == 0 ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        public bool Equals(FolderKey x, FolderKey y) => x.SourceId == y.SourceId && PathComparer(x.SourceId).Equals(x.Path, y.Path);
        public int GetHashCode(FolderKey key) => HashCode.Combine(key.SourceId, PathComparer(key.SourceId).GetHashCode(key.Path));
    }
    private Dictionary<FolderKey, FolderBrowserNode> _nodes = new(new FolderKeyComparer());
    private Dictionary<FolderKey, FolderBrowserNode> _prior = new(new FolderKeyComparer());
    private readonly List<(FolderBrowserNode Node, bool HadChildren)> _reused = [];
    public List<FolderBrowserNode> Roots { get; } = [];
    private readonly List<FolderLocation> _pending = [];

    public List<FolderLocation> GetExpandedLocations()
    {
        List<FolderLocation> result = [];
        foreach (var node in _nodes.Values)
            if (node.IsExpanded) result.Add(node.Location);
        return result;
    }
    public FolderBrowserNode? Find(FolderLocation? location)
        => location is not null && _nodes.TryGetValue(new(location.SourceId, location.Path), out var node) ? node : null;

    public void Rebuild(IReadOnlyList<Music> songs, List<Folder> folders, int sourceFilter)
    {
        (_nodes, _prior) = (_prior, _nodes);
        _nodes.Clear();
        _reused.Clear();
        Roots.Clear();
        folders.Sort(static (a, b) => (a.Path?.Length ?? 0).CompareTo(b.Path?.Length ?? 0));
        if (sourceFilter is -1 or 0)
            foreach (var folder in folders)
                if (!folder.IsExternalImport && !string.IsNullOrWhiteSpace(folder.Path)) AddLocalRoot(folder);

        // .NET 10+：每个目录只构建一次；逐曲计数复用节点，不复制各级歌曲集合。
        foreach (var music in songs)
        {
            if (!LibraryQueries.IsBrowsable(music) || !LibraryQueries.MatchesSource(music, sourceFilter)) continue;
            var path = FolderLocation.Normalize(music.FolderPath, music.SourceId);
            if (path.Length == 0) continue;
            var node = EnsureDirectory(music.SourceId, path, music.LastLevelFolderPath, music.Album);
            node.PendingDirectSongCount++;
            for (var ancestor = node; ancestor is not null; ancestor = ancestor.Parent) ancestor.PendingSongCount++;
        }
        Roots.Sort(CompareNodes);
        foreach (var node in _nodes.Values)
        {
            node.Children.Sort(CompareNodes);
            node.PublishCounts();
        }
        foreach (var (node, hadChildren) in _reused) node.NotifyChildrenChanged(hadChildren);
        _reused.Clear();
        _prior.Clear();
    }

    private void AddLocalRoot(Folder folder)
    {
        var path = FolderLocation.Normalize(folder.Path, 0);
        FolderBrowserNode? parent = null;
        var ancestorPath = Directory.GetParent(path)?.FullName;
        while (ancestorPath is not null)
        {
            if (_nodes.TryGetValue(new(0, ancestorPath), out parent)) break;
            ancestorPath = Directory.GetParent(ancestorPath)?.FullName;
        }
        if (parent is not null) EnsureDirectory(0, path, string.Empty);
        else Add(new(0, path, string.IsNullOrEmpty(folder.Name) ? path : folder.Name), null);
    }

    private FolderBrowserNode EnsureDirectory(int sourceId, string path, string displayPath, string album = "")
    {
        string trackFolder = path;
        FolderBrowserNode? parent = null;
        _pending.Clear();
        while (!_nodes.TryGetValue(new(sourceId, path), out parent))
        {
            string? parentPath;
            string name;
            if (sourceId == 0)
            {
                parentPath = Directory.GetParent(path)?.FullName;
                name = System.IO.Path.GetFileName(path);
                if (name.Length == 0) name = path;
            }
            else
            {
                int schemeEnd = path.IndexOf("://", StringComparison.Ordinal);
                int authorityEnd = schemeEnd < 0 ? -1 : path.IndexOf('/', schemeEnd + 3);
                int slash = path.LastIndexOf('/');
                bool catalog = path.StartsWith("remote://", StringComparison.Ordinal);
                parentPath = authorityEnd >= 0 && slash >= authorityEnd ? path[..slash] : null;
                if (parentPath is null)
                    name = catalog ? CatalogSourceName(displayPath, album, trackFolder.Length != path.Length)
                        : WebDavSourceName(displayPath, trackFolder, sourceId);
                else if (catalog)
                    name = album;
                else
                    name = Uri.UnescapeDataString(path[(slash + 1)..]);
            }
            _pending.Add(new(sourceId, path, name));
            if (parentPath is null) break;
            path = parentPath;
        }
        // 用迭代回填任意深度的远程目录，避免路径层级消耗调用栈。
        for (int i = _pending.Count - 1; i >= 0; i--) parent = Add(_pending[i], parent);
        _pending.Clear();
        return parent!;
    }

    private static string CatalogSourceName(string displayPath, string album, bool isAlbumFolder)
    {
        if (!isAlbumFolder) return displayPath;
        int separator = FindSourceNameEnd(displayPath, album);
        // 加载曲库时可能把未知专辑名称换成本地化文本，旧值本身不含分隔符。
        if (separator < 0) separator = displayPath.LastIndexOf(" / ", StringComparison.Ordinal);
        return separator >= 0 ? displayPath[..separator] : displayPath;
    }

    private static string WebDavSourceName(string displayPath, string path, int sourceId)
    {
        int hrefStart = path.IndexOf('/', "webdav://".Length);
        string href = hrefStart < 0 ? "/" : Uri.UnescapeDataString(path[hrefStart..]);
        int separator = FindSourceNameEnd(displayPath.AsSpan().TrimEnd('/'), href.AsSpan().TrimEnd('/'));
        if (separator < 0) separator = displayPath.IndexOf(" / ", StringComparison.Ordinal);
        return separator > 0 ? displayPath[..separator] : "WebDAV " + sourceId;
    }

    // 先匹配已知目录或专辑字段，名称本身可以包含显示分隔符。
    private static int FindSourceNameEnd(ReadOnlySpan<char> displayPath, ReadOnlySpan<char> suffix)
    {
        const string separator = " / ";
        int nameEnd = displayPath.Length - suffix.Length - separator.Length;
        return nameEnd > 0 && displayPath.EndsWith(suffix, StringComparison.Ordinal) &&
            displayPath.Slice(nameEnd, separator.Length).SequenceEqual(separator) ? nameEnd : -1;
    }

    private FolderBrowserNode Add(FolderLocation location, FolderBrowserNode? parent)
    {
        if (_nodes.TryGetValue(new(location.SourceId, location.Path), out var existing)) return existing;
        var key = new FolderKey(location.SourceId, location.Path);
        FolderBrowserNode node;
        if (_prior.TryGetValue(key, out var prior) && prior.Location == location && ReferenceEquals(prior.Parent, parent))
        {
            node = prior;
            _reused.Add((node, node.HasChildren));
            node.Children.Clear();
            node.PendingSongCount = 0;
            node.PendingDirectSongCount = 0;
        }
        else node = new FolderBrowserNode(location, parent);
        _nodes.Add(key, node);
        if (parent is null) Roots.Add(node);
        else parent.Children.Add(node);
        return node;
    }

    private static int CompareNodes(FolderBrowserNode a, FolderBrowserNode b)
        => StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name);
}
