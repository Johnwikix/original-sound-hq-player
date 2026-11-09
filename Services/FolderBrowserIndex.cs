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
    private readonly Dictionary<FolderKey, FolderBrowserNode> _nodes = new(new FolderKeyComparer());
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
        _nodes.Clear();
        Roots.Clear();
        folders.Sort(static (a, b) => (a.Path?.Length ?? 0).CompareTo(b.Path?.Length ?? 0));
        if (sourceFilter is -1 or 0)
            foreach (var folder in folders)
                if (!folder.IsExternalImport && !string.IsNullOrWhiteSpace(folder.Path)) AddLocalRoot(folder);

        // .NET 10+：每个目录只构建一次；逐曲计数复用节点，不复制各级歌曲集合。
        foreach (var music in songs)
        {
            if (!LibraryQueries.MatchesSource(music, sourceFilter)) continue;
            var path = FolderLocation.Normalize(music.FolderPath, music.SourceId);
            if (path.Length == 0) continue;
            var node = EnsureDirectory(music.SourceId, path, music.LastLevelFolderPath);
            node.DirectSongCount++;
            for (var ancestor = node; ancestor is not null; ancestor = ancestor.Parent) ancestor.SongCount++;
        }
        Roots.Sort(CompareNodes);
        foreach (var node in _nodes.Values) FinishNode(node);
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

    private FolderBrowserNode EnsureDirectory(int sourceId, string path, string displayPath)
    {
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
                int authorityEnd = path.IndexOf('/', "webdav://".Length);
                int slash = path.LastIndexOf('/');
                parentPath = authorityEnd >= 0 && slash >= authorityEnd ? path[..slash] : null;
                name = parentPath is null ? RemoteName(displayPath, sourceId) : Uri.UnescapeDataString(path[(slash + 1)..]);
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

    private FolderBrowserNode Add(FolderLocation location, FolderBrowserNode? parent)
    {
        if (_nodes.TryGetValue(new(location.SourceId, location.Path), out var existing)) return existing;
        var node = new FolderBrowserNode(location, parent);
        _nodes.Add(new(location.SourceId, location.Path), node);
        if (parent is null) Roots.Add(node);
        else parent.Children.Add(node);
        return node;
    }

    private static string RemoteName(string displayPath, int sourceId)
    {
        int separator = displayPath.IndexOf(" / ", StringComparison.Ordinal);
        return separator > 0 ? displayPath[..separator] : "WebDAV " + sourceId;
    }
    private static void FinishNode(FolderBrowserNode node) => node.Children.Sort(CompareNodes);
    private static int CompareNodes(FolderBrowserNode a, FolderBrowserNode b)
        => StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name);
}
