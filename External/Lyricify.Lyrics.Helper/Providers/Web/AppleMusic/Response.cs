#nullable disable
using System.Text.Json.Serialization;

namespace Lyricify.Lyrics.Providers.Web.AppleMusic
{
    // ===== /v1/me/storefront =====
    public class StorefrontResponse
    {
        [JsonPropertyName("data")]
        public StorefrontData[] Data { get; set; }
    }

    public class StorefrontData
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("attributes")]
        public StorefrontAttributes Attributes { get; set; }
    }

    public class StorefrontAttributes
    {
        [JsonPropertyName("defaultLanguageTag")]
        public string DefaultLanguageTag { get; set; }
    }

    // ===== /v1/catalog/{storefront}/search =====
    public class SearchResponse
    {
        [JsonPropertyName("results")]
        public SearchResults Results { get; set; }
    }

    public class SearchResults
    {
        [JsonPropertyName("songs")]
        public SongsContainer Songs { get; set; }
    }

    public class SongsContainer
    {
        [JsonPropertyName("data")]
        public SongData[] Data { get; set; }
    }

    public class SongData
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("attributes")]
        public SongAttributes Attributes { get; set; }
    }

    public class SongAttributes
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("artistName")]
        public string ArtistName { get; set; }

        [JsonPropertyName("albumName")]
        public string AlbumName { get; set; }

        [JsonPropertyName("durationInMillis")]
        public int DurationInMillis { get; set; }
    }

    public class LyricResponse
    {
        [JsonPropertyName("data")]
        public LyricSongData[] Data { get; set; }

        /// <summary>
        /// 归一化输出
        /// </summary>
        [JsonIgnore]
        public string Ttml { get; private set; }

        public void NormalizeTtml()
        {
            Ttml = null;

            if (Data == null || Data.Length == 0)
                return;

            var song = Data[0];
            var rel = song?.Relationships;
            if (rel == null)
                return;

            var syll = rel.SyllableLyrics;
            if (syll?.Data == null || syll.Data.Length == 0)
                return;

            var attr = syll.Data[0]?.Attributes;
            if (attr == null)
                return;

            // 1) 优先使用 ttmlLocalizations
            var ttml = attr.TtmlLocalizations;

            // 2) 回退到 ttml
            if (string.IsNullOrWhiteSpace(ttml))
                ttml = attr.Ttml;

            // 3) 校验是否包含时间轴
            if (!string.IsNullOrWhiteSpace(ttml)
                && ttml.Contains("begin=")
                && ttml.Contains("end="))
            {
                Ttml = ttml;
            }
        }
    }

    public class LyricSongData
    {
        [JsonPropertyName("relationships")]
        public LyricRelationships Relationships { get; set; }
    }

    public class LyricRelationships
    {
        [JsonPropertyName("syllable-lyrics")]
        public LyricContainer SyllableLyrics { get; set; }

        [JsonPropertyName("lyrics")]
        public LyricContainer Lyrics { get; set; }
    }

    public class LyricContainer
    {
        [JsonPropertyName("data")]
        public LyricData[] Data { get; set; }
    }

    public class LyricData
    {
        [JsonPropertyName("attributes")]
        public LyricAttributes Attributes { get; set; }
    }

    public class LyricAttributes
    {
        [JsonPropertyName("ttml")]
        public string Ttml { get; set; }

        [JsonPropertyName("ttmlLocalizations")]
        public string TtmlLocalizations { get; set; }
    }
}
