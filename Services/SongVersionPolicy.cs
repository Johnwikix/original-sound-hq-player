using System;
using System.Collections.Generic;
using System.Text;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Services;

/// <summary>定义重复文件候选和稳定自动选版；不读取文件、不访问网络。</summary>
public static class SongVersionPolicy
{
    /// <summary>按标题和专辑缩小候选范围，艺术家在同一候选范围内按完整署名比较。</summary>
    internal static string? GetBucketKey(string title, string album)
    {
        string t = Normalize(title ?? "").ToUpperInvariant();
        string b = Normalize(album ?? "").ToUpperInvariant();
        if (t.Length == 0 || b.Length == 0 || UnknownLabels.Contains(t) || UnknownLabels.Contains(b)) return null;
        return string.Concat(t.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), ":", t, b);
    }

    internal static HashSet<string> ArtistCredits(string artist)
    {
        // 使用固定的标签分隔符，避免用户调整艺术家页显示设置时改变持久归组。
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (var part in Normalize(artist ?? "").Split(['/', ';', '、'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (UnknownLabels.Contains(part)) return [];
            names.Add(part.ToUpperInvariant());
        }
        return names;
    }

    /// <summary>标题与专辑相同，且一方所有署名均出现于另一方。</summary>
    public static bool Matches(Music left, Music right)
    {
        if (left.IsDetached || right.IsDetached || GetBucketKey(left.Title, left.Album) is not { } key ||
            key != GetBucketKey(right.Title, right.Album)) return false;
        var a = ArtistCredits(left.Author);
        var b = ArtistCredits(right.Author);
        return a.Count > 0 && b.Count > 0 && (a.IsSubsetOf(b) || b.IsSubsetOf(a));
    }

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
