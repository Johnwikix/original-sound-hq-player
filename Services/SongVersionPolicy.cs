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
    /// <summary>判断三项完整元数据是否一致，保留版本限定词。</summary>
    public static bool Matches(Music left, Music right) => Known(left.Title) && Known(left.Author) && Known(left.Album)
        && Known(right.Title) && Known(right.Author) && Known(right.Album)
        && Equal(left.Title, right.Title) && Equal(left.Author, right.Author) && Equal(left.Album, right.Album);

    private static bool Equal(string left, string right) => string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
    private static readonly HashSet<string> UnknownLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        "Unknown", "Unknown Title", "Unknown Artist", "Unknown Album", "未知专辑", "未知專輯", "未知艺术家", "未知藝術家",
        "Álbum desconocido", "Artista desconocido", "不明なアルバム", "不明なアーティスト", "Неизвестный альбом", "Неизвестный артист",
        "Unbekanntes Album", "Unbekannter Künstler", "Bilinmeyen Albüm", "Bilinmeyen Sanatçı"
    };
    private static bool Known(string value) => !string.IsNullOrWhiteSpace(value) && !UnknownLabels.Contains(Normalize(value));
    private static string Normalize(string value)
    {
        // 候选检索是冷路径；不在音频进度或歌词帧中规范化字符串。
        var normalized = value.Normalize(NormalizationForm.FormKC).AsSpan().Trim();
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
        for (int kind = 0; kind < 3; kind++)
        for (int source = 0; source < 3; source++)
        {
            // 先建立稳定编码组，避免“同编码比码率，否则比 Id”的非传递比较器。
            var groups = new SortedDictionary<int, List<Music>>();
            Dictionary<(string Codec, int Rate, int Channels), int> keys = new();
            for (int i = 0; i < versions.Count; i++)
            {
                var music = versions[i];
                if (music.IsDetached || Kind(music) != kind || Source(music, forPlayback) != source) continue;
                var key = (Codec(music), music.SampleRate, music.Channel);
                if (!keys.TryGetValue(key, out int first))
                {
                    first = music.Id;
                    keys.Add(key, first);
                    groups.Add(first, new());
                }
                else if (music.Id < first)
                {
                    var group = groups[first];
                    groups.Remove(first);
                    first = music.Id;
                    keys[key] = first;
                    groups.Add(first, group);
                }
                groups[first].Add(music);
            }
            foreach (var group in groups.Values)
            {
                group.Sort(kind == 1 ? LossyComparer : IdComparer);
                result.AddRange(group);
            }
        }
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

    private static readonly IComparer<Music> IdComparer = Comparer<Music>.Create((a, b) => a.Id.CompareTo(b.Id));
    private static readonly IComparer<Music> LossyComparer = Comparer<Music>.Create((a, b) =>
    {
        int rate = b.BitRate.CompareTo(a.BitRate);
        return rate != 0 ? rate : a.Id.CompareTo(b.Id);
    });
    private static int Source(Music music, bool playback) => !music.IsRemote ? 0 : playback && music.IsRemoteCached ? 1 : 2;
    private static string Codec(Music music) => !string.IsNullOrEmpty(music.CodecId) ? music.CodecId.ToUpperInvariant() : (music.Extension ?? "").ToUpperInvariant();
    private static int Kind(Music music) => music.EncodingKind switch { 1 => 0, 2 => 1, _ => 2 };
}
