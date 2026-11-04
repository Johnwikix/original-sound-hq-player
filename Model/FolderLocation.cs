using System;

namespace WinUIMusicPlayer.Model;

/// <summary>目录的稳定身份；本地路径忽略大小写，WebDAV 保留服务器大小写语义。</summary>
public sealed record FolderLocation(int SourceId, string Path, string Name)
{
    public string Identity { get; } = string.Concat(SourceId.ToString(System.Globalization.CultureInfo.InvariantCulture), ":", Path);
    public bool Matches(Music music) => music.SourceId == SourceId &&
        string.Equals(Normalize(music.FolderPath, SourceId), Path,
            SourceId == 0 ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    public static string Normalize(string? path, int sourceId)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        if (sourceId != 0) return path.TrimEnd('/');
        var local = System.IO.Path.TrimEndingDirectorySeparator(path.Replace('/', '\\'));
        return local.StartsWith("\\\\", StringComparison.Ordinal) ? local.TrimEnd('\\') : local;
    }
}
