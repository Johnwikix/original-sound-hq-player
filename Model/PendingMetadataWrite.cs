using SQLite;

namespace WinUIMusicPlayer.Model;

/// <summary>保存可跨重启恢复的标签写入快照；同一路径以最后一次编辑为准。</summary>
public sealed class PendingMetadataWrite
{
    /// <summary>获取或设置规范化文件路径。</summary>
    [PrimaryKey, Collation("NOCASE")]
    public string Path { get; set; } = "";
    /// <summary>获取或设置歌曲标题。</summary>
    public string Title { get; set; } = "";
    /// <summary>获取或设置专辑。</summary>
    public string Album { get; set; } = "";
    /// <summary>获取或设置艺术家。</summary>
    public string Author { get; set; } = "";
    /// <summary>获取或设置曲序。</summary>
    public int TrackNumber { get; set; }
    /// <summary>获取或设置碟序。</summary>
    public int DiskNumber { get; set; }
    /// <summary>获取或设置年份。</summary>
    public int Year { get; set; }
    /// <summary>获取或设置封面快照。</summary>
    public byte[]? Cover { get; set; }
    /// <summary>获取或设置歌词。</summary>
    public string? Lyrics { get; set; }
    /// <summary>获取或设置 KRC 歌词。</summary>
    public string? Krc { get; set; }
    /// <summary>获取或设置已经报告的错误，避免重复通知。</summary>
    public string? LastError { get; set; }
}
