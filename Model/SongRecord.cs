using SQLite;

namespace WinUIMusicPlayer.Model;

/// <summary>保存歌曲分组及共享用户状态，文件身份始终由 Music.Id 表示。</summary>
[Table("Song")]
public sealed class SongRecord
{
    /// <summary>获取或设置稳定歌曲标识。</summary>
    [PrimaryKey, AutoIncrement] public int Id { get; set; }
    /// <summary>获取或设置分组标题。</summary>
    public string Title { get; set; } = "";
    /// <summary>获取或设置分组艺术家。</summary>
    public string Artist { get; set; } = "";
    /// <summary>获取或设置分组专辑。</summary>
    public string Album { get; set; } = "";
    /// <summary>获取或设置手动首选版本；空值表示自动选择。</summary>
    public int? PreferredMusicId { get; set; }
    /// <summary>获取或设置共享收藏状态。</summary>
    public bool IsFavorite { get; set; }
    /// <summary>获取或设置收藏排序。</summary>
    public int FavoriteOrder { get; set; }
    /// <summary>获取或设置共享修改修订。</summary>
    public long Revision { get; set; } = 1;
    /// <summary>获取或设置合并后的目标标识；保留旧身份供恢复。</summary>
    public int? MergedIntoSongId { get; set; }
    /// <summary>获取或设置旧歌词的惰性迁移来源。</summary>
    public int LyricsSourceMusicId { get; set; }
}

/// <summary>保存共享歌词；完整保留原格式及伴随文本。</summary>
[Table("SongLyrics")]
public sealed class SongLyricsRecord
{
    /// <summary>获取或设置歌曲标识。</summary>
    [PrimaryKey] public int SongId { get; set; }
    /// <summary>获取或设置原文。</summary>
    public string Lyrics { get; set; } = "";
    /// <summary>获取或设置原文格式。</summary>
    public LyricsFormat LyricsFormat { get; set; }
    /// <summary>获取或设置翻译。</summary>
    public string? TranslatedLyrics { get; set; }
    /// <summary>获取或设置发音。</summary>
    public string? PronunciationLyrics { get; set; }
    /// <summary>获取或设置来源种类。</summary>
    public string SourceKind { get; set; } = "";
    /// <summary>获取或设置来源标识。</summary>
    public string SourceKey { get; set; } = "";
    /// <summary>获取或设置并发修订。</summary>
    public long Revision { get; set; } = 1;
    /// <summary>获取或设置迁移诊断。</summary>
    public string Diagnostic { get; set; } = "";
    /// <summary>创建不含界面状态的快照。</summary>
    public LyricsSnapshot Snapshot() => new(new(new(Lyrics, LyricsFormat), TranslatedLyrics, PronunciationLyrics), Revision,
        SourceKind, SourceKey, Diagnostic, SongId);
    /// <summary>从既有文档创建独立记录。</summary>
    public static SongLyricsRecord From(int songId, LyricsSnapshot snapshot) => new()
    {
        SongId = songId, Lyrics = snapshot.Document.Original.Content, LyricsFormat = snapshot.Document.Original.Format,
        TranslatedLyrics = snapshot.Document.TranslationLrc, PronunciationLyrics = snapshot.Document.PronunciationLrc,
        SourceKind = snapshot.SourceKind, SourceKey = snapshot.SourceKey, Revision = snapshot.Revision, Diagnostic = snapshot.Diagnostic
    };
}

/// <summary>保存无法直接与共享时间轴共用的版本歌词。</summary>
[Table("VersionLyricsOverride")]
public sealed class VersionLyricsOverrideRecord
{
    /// <summary>获取或设置版本标识。</summary>
    [PrimaryKey] public int MusicId { get; set; }
    /// <summary>获取或设置原文。</summary>
    public string Lyrics { get; set; } = "";
    /// <summary>获取或设置原文格式。</summary>
    public LyricsFormat LyricsFormat { get; set; }
    /// <summary>获取或设置翻译。</summary>
    public string? TranslatedLyrics { get; set; }
    /// <summary>获取或设置发音。</summary>
    public string? PronunciationLyrics { get; set; }
    /// <summary>获取或设置并发修订。</summary>
    public long Revision { get; set; } = 1;
    /// <summary>获取或设置原始来源，保留用户明确清空歌词的意图。</summary>
    public string SourceKind { get; set; } = "VersionOverride";
    /// <summary>获取或设置原始来源标识。</summary>
    public string SourceKey { get; set; } = "";
    /// <summary>获取或设置原始迁移诊断。</summary>
    public string Diagnostic { get; set; } = "";
    /// <summary>创建带独立所有者标识的快照。</summary>
    public LyricsSnapshot Snapshot() => new(new(new(Lyrics, LyricsFormat), TranslatedLyrics, PronunciationLyrics), Revision,
        SourceKind, SourceKey, Diagnostic, -MusicId);
}

/// <summary>保存可恢复分组操作的前后快照。</summary>
[Table("SongVersionOperation")]
public sealed class SongVersionOperationRecord
{
    /// <summary>获取或设置操作标识。</summary>
    [PrimaryKey, AutoIncrement] public int Id { get; set; }
    /// <summary>获取或设置关联歌曲。</summary>
    [Indexed] public int SongId { get; set; }
    /// <summary>获取或设置另一相关歌曲，拆分后仍可查找恢复记录。</summary>
    [Indexed] public int RelatedSongId { get; set; }
    /// <summary>获取或设置操作类型。</summary>
    public string Kind { get; set; } = "";
    /// <summary>获取或设置前状态。</summary>
    public string BeforeJson { get; set; } = "";
    /// <summary>获取或设置后状态。</summary>
    public string AfterJson { get; set; } = "";
    /// <summary>获取或设置是否已经撤销。</summary>
    public bool Undone { get; set; }
}

/// <summary>标记版本管理数据库的支持结构。</summary>
[Table("SongVersionSchema")]
public sealed class SongVersionSchemaRecord
{
    public const int CurrentVersion = 4;
    /// <summary>获取或设置唯一记录键。</summary>
    [PrimaryKey] public int Id { get; set; } = 1;
    /// <summary>获取或设置已完成结构版本。</summary>
    public int Version { get; set; } = CurrentVersion;
}

/// <summary>保存歌曲的规范化自动关联键及用户拆分例外；不改变文件标签。</summary>
[Table("SongAutoLink")]
public sealed class SongAutoLinkRecord
{
    /// <summary>获取或设置歌曲身份。</summary>
    [PrimaryKey] public int SongId { get; set; }
    /// <summary>获取或设置完整三项标签的关联键；不完整时为空。</summary>
    [Indexed] public string? MatchKey { get; set; }
    /// <summary>获取或设置是否因手动拆分或撤销合并而跳过自动关联。</summary>
    public bool IsExcluded { get; set; }
}

/// <summary>持久化需要重新计算关联键的歌曲，事务提交后可继续处理。</summary>
[Table("SongAutoLinkDirty")]
public sealed class SongAutoLinkDirtyRecord
{
    /// <summary>获取或设置待处理歌曲身份。</summary>
    [PrimaryKey] public int SongId { get; set; }
}

/// <summary>持久化可能有重复项的关联键，歌词读取失败或退出后可重试。</summary>
[Table("SongAutoLinkPending")]
public sealed class SongAutoLinkPendingRecord
{
    /// <summary>获取或设置待处理的关联键。</summary>
    [PrimaryKey] public string MatchKey { get; set; } = "";
}
