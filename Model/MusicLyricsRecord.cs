using SQLite;

namespace WinUIMusicPlayer.Model;

[Table("MusicLyricsV2")]
public sealed class MusicLyricsRecord
{
    [PrimaryKey] public int MusicId { get; set; }
    public string Lyrics { get; set; } = "";
    public LyricsFormat LyricsFormat { get; set; }
    public string? TranslatedLyrics { get; set; }
    public string? PronunciationLyrics { get; set; }
    public string SourceKind { get; set; } = "";
    public string SourceKey { get; set; } = "";
    public long Revision { get; set; } = 1;
    public int SchemaVersion { get; set; } = 2;
    public string MigratedFromHash { get; set; } = "";
    public string Diagnostic { get; set; } = "";

    public LyricsSnapshot Snapshot() => new(new(new(Lyrics, LyricsFormat), TranslatedLyrics, PronunciationLyrics), Revision, SourceKind, SourceKey, Diagnostic);
}

[Table("LyricsSearchState")]
public sealed class LyricsSearchStateRecord
{
    [PrimaryKey] public string Key { get; set; } = "";
    [Indexed] public int MusicId { get; set; }
    public string Channel { get; set; } = "";
    public string QueryHash { get; set; } = "";
    public string Status { get; set; } = "";
}
