using System;
using System.Collections.Generic;
using System.Text;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Services;

/// <summary>定义重复文件候选和稳定自动选版；不读取文件、不访问网络。</summary>
public static class SongVersionPolicy
{
    /// <summary>为完整三项标签生成无分隔符碰撞的索引键，保留 Live、Remaster 等限定词。</summary>
    public static string? GetMatchKey(string title, string artist, string album)
    {
        string t = Normalize(title ?? ""), a = Normalize(artist ?? ""), b = Normalize(album ?? "");
        if (t.Length == 0 || a.Length == 0 || b.Length == 0 || UnknownLabels.Contains(t) || UnknownLabels.Contains(a) || UnknownLabels.Contains(b)) return null;
        t = t.ToUpperInvariant();
        a = a.ToUpperInvariant();
        b = b.ToUpperInvariant();
        return string.Concat(t.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), ":", t,
            a.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), ":", a,
            b.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), ":", b);
    }
    internal static string? FileMatchKey(string title, string artist, string album, string path, bool? fromFileName)
    {
        if (fromFileName == true) return null;
        if (fromFileName is null && !string.IsNullOrEmpty(path))
        {
            // 老库没有记录标题来源。遇到同名文件时等待下一次读标签。
            if (path.StartsWith("webdav://", StringComparison.OrdinalIgnoreCase)) path = Uri.UnescapeDataString(path);
            if (string.Equals(System.IO.Path.GetFileNameWithoutExtension(path), title, StringComparison.OrdinalIgnoreCase)) return null;
        }
        return GetMatchKey(title, artist, album);
    }

    /// <summary>预览和提交均重新检查文件的当前标签与解绑状态。</summary>
    public static bool Matches(Music left, Music right) => !left.IsDetached && !right.IsDetached &&
        FileMatchKey(left.Title, left.Author, left.Album, left.Path, left.TitleFromFileName) is { } key &&
        key == FileMatchKey(right.Title, right.Author, right.Album, right.Path, right.TitleFromFileName);

    private static readonly HashSet<string> UnknownLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        "Unknown", "Unknown Title", "Unknown Artist", "Unknown Album", "未知专辑", "未知專輯", "未知艺术家", "未知藝術家",
        "Álbum desconocido", "Artista desconocido", "不明なアルバム", "不明なアーティスト", "Неизвестный альбом", "Неизвестный артист",
        "Unbekanntes Album", "Unbekannter Künstler", "Bilinmeyen Albüm", "Bilinmeyen Sanatçı"
    };
    private static string Normalize(string value)
    {
        // 候选检索是冷路径；不在音频进度或歌词帧中规范化字符串。
        string normalizedText = value.Normalize(NormalizationForm.FormKC).Trim();
        // 常见的标签已经只有单个普通空格，直接复用规范化结果。
        bool collapse = false;
        for (int i = 0; i < normalizedText.Length; i++)
            if (char.IsWhiteSpace(normalizedText[i]) && (normalizedText[i] != ' ' || i > 0 && normalizedText[i - 1] == ' '))
            { collapse = true; break; }
        if (!collapse) return normalizedText;
        var normalized = normalizedText.AsSpan();
        var text = new StringBuilder(normalized.Length);
        bool space = false;
        foreach (char character in normalized)
        {
            if (char.IsWhiteSpace(character)) { space = text.Length > 0; continue; }
            if (space) text.Append(' ');
            space = false;
            text.Append(character);
        }
        return text.ToString();
    }

    /// <summary>返回按稳定分桶排序的候选；可用性必须由播放服务另行验证。</summary>
    public static List<Music> Order(IReadOnlyList<Music> versions, int? preferredMusicId = null, bool forPlayback = false)
    {
        if (versions.Count == 0) return [];
        if (versions.Count == 1) return versions[0].IsDetached ? [] : [versions[0]];
        List<Music> result = new(versions.Count);
        // 同来源、同类的可比规格组成一组。组间使用最早入库身份，排序保持传递性。
        var groups = new Dictionary<(int Source, int Kind, string Codec, int Rate, int Channels), List<Music>>();
        foreach (var music in versions)
        {
            if (music.IsDetached) continue;
            int kind = Kind(music);
            bool pcm = kind == 0 && !IsDsd(music);
            var key = (Source(music, forPlayback), kind, pcm ? "" : Codec(music),
                kind == 1 ? music.SampleRate : 0, music.Channel);
            if (!groups.TryGetValue(key, out var group)) groups.Add(key, group = []);
            group.Add(music);
        }
        var ordered = new List<(int Source, int Kind, int First, List<Music> Items)>(groups.Count);
        foreach (var (key, group) in groups)
        {
            int first = group[0].Id;
            foreach (var member in group) first = Math.Min(first, member.Id);
            group.Sort(key.Kind == 1 ? LossyComparer : key.Kind == 0 && !IsDsd(group[0]) ? PcmComparer : IdComparer);
            ordered.Add((key.Source, key.Kind, first, group));
        }
        ordered.Sort(static (a, b) => (a.Source, a.Kind, a.First).CompareTo((b.Source, b.Kind, b.First)));
        foreach (var group in ordered) result.AddRange(group.Items);
        if (preferredMusicId is int preferred)
        {
            for (int i = 0; i < result.Count; i++)
                if (result[i].Id == preferred)
                {
                    var music = result[i];
                    result.RemoveAt(i);
                    result.Insert(0, music);
                    break;
                }
        }
        return result;
    }

    private static bool IsDsd(Music music) => music.BitDepth == 1 ||
        music.Extension?.TrimStart('.').Equals("DSF", StringComparison.OrdinalIgnoreCase) == true ||
        music.Extension?.TrimStart('.').Equals("DFF", StringComparison.OrdinalIgnoreCase) == true ||
        music.CodecId?.Contains("DSD", StringComparison.OrdinalIgnoreCase) == true;
    private static readonly IComparer<Music> PcmComparer = Comparer<Music>.Create(static (a, b) =>
    {
        int depth = b.BitDepth.CompareTo(a.BitDepth);
        if (depth != 0) return depth;
        int rate = b.SampleRate.CompareTo(a.SampleRate);
        return rate != 0 ? rate : a.Id.CompareTo(b.Id);
    });
    private static readonly IComparer<Music> IdComparer = Comparer<Music>.Create((a, b) => a.Id.CompareTo(b.Id));
    private static readonly IComparer<Music> LossyComparer = Comparer<Music>.Create((a, b) =>
    {
        int rate = b.BitRate.CompareTo(a.BitRate);
        return rate != 0 ? rate : a.Id.CompareTo(b.Id);
    });
    internal static bool InScope(Music music, int sourceId) => !music.IsDetached &&
        (sourceId == -1 || sourceId == music.SourceId || sourceId == -2 && music.IsRemote);

    private static int Source(Music music, bool playback)
    {
        bool cached = music.IsRemote && music.IsRemoteCached;
        int source = !music.IsRemote ? 0 : playback && cached ? 1 : 2;
        if (!music.IsMissing) return source;
        if (cached) return playback ? source : 3;
        return source + 4;
    }
    private static string Codec(Music music) => !string.IsNullOrEmpty(music.CodecId) ? music.CodecId.ToUpperInvariant() : (music.Extension ?? "").ToUpperInvariant();
    private static int Kind(Music music) => music.EncodingKind switch { 1 => 0, 2 => 1, _ => 2 };
}
