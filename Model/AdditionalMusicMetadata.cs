using System;

namespace WinUIMusicPlayer.Model;

/// <summary>保存六项可选标签的不可变快照；空字段共享空字符串，不构建每曲目的标签字典。</summary>
public readonly record struct AdditionalMusicMetadata(string AlbumArtist, string Genre, string Composer, string Lyricist, string Conductor, string CatalogNumber)
{
    /// <summary>获取是否所有可选标签都为空。</summary>
    public bool IsEmpty => string.IsNullOrWhiteSpace(AlbumArtist) &&
        string.IsNullOrWhiteSpace(Genre) &&
        string.IsNullOrWhiteSpace(Composer) &&
        string.IsNullOrWhiteSpace(Lyricist) &&
        string.IsNullOrWhiteSpace(Conductor) &&
        string.IsNullOrWhiteSpace(CatalogNumber);

    /// <summary>从曲目读取标签快照。</summary>
    public static AdditionalMusicMetadata From(Music music) => new(music.AlbumArtist ?? "", music.Genre ?? "", music.Composer ?? "", music.Lyricist ?? "", music.Conductor ?? "", music.CatalogNumber ?? "");

    /// <summary>将已读取的标签发布到曲目。</summary>
    public void ApplyTo(Music music)
    {
        music.AlbumArtist = AlbumArtist ?? "";
        music.Genre = Genre ?? "";
        music.Composer = Composer ?? "";
        music.Lyricist = Lyricist ?? "";
        music.Conductor = Conductor ?? "";
        music.CatalogNumber = CatalogNumber ?? "";
        music.AdditionalMetadataRead = true;
    }

    /// <summary>以不分配拼接字符串的方式匹配任一可选标签。</summary>
    public bool Matches(ReadOnlySpan<char> search) =>
        Contains(AlbumArtist, search) ||
        Contains(Genre, search) ||
        Contains(Composer, search) ||
        Contains(Lyricist, search) ||
        Contains(Conductor, search) ||
        Contains(CatalogNumber, search);

    private static bool Contains(string? value, ReadOnlySpan<char> search) =>
        !string.IsNullOrEmpty(value) && value.AsSpan().Contains(search, StringComparison.OrdinalIgnoreCase);
}
