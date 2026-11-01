using System;
using ATL;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Utils;

/// <summary>仅使用 ATL 已提供的统一属性读写标签，不添加格式分支或扩展标签。</summary>
public static class AudioMetadataTags
{
    /// <summary>获取文件已有的可选标签，缺失值保留为空。</summary>
    public static AdditionalMusicMetadata Read(Track track) => new(Clean(track.AlbumArtist), Clean(track.Genre), Clean(track.Composer), Clean(track.Lyricist), Clean(track.Conductor), Clean(track.CatalogNumber));

    /// <summary>将已知的可选标签写入已打开的文件；空值用于显式清除。</summary>
    public static void Write(Track track, AdditionalMusicMetadata metadata)
    {
        track.AlbumArtist = metadata.AlbumArtist ?? "";
        track.Genre = metadata.Genre ?? "";
        track.Composer = metadata.Composer ?? "";
        track.Lyricist = metadata.Lyricist ?? "";
        track.Conductor = metadata.Conductor ?? "";
        track.CatalogNumber = metadata.CatalogNumber ?? "";
    }

    private static string Clean(string? value) => string.IsNullOrWhiteSpace(value) ? "" : value.Trim();
}
