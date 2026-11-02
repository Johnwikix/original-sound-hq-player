namespace WinUIMusicPlayer.Services;

/// <summary>专辑级标签批量修改；null 表示该字段未改动，保存时跳过，不做无谓重写。</summary>
public sealed record AlbumTagEdit(string? Album = null, int? Year = null, string? AlbumArtist = null,
    string? Genre = null, byte[]? Cover = null)
{
    /// <summary>获取是否没有任何字段被改动。</summary>
    public bool IsEmpty => Album is null && Year is null && AlbumArtist is null && Genre is null && Cover is null;
}
