using Lyricify.Lyrics.Providers.Web.QQMusic;
using Lyricify.Lyrics.Searchers.Helpers;

namespace Lyricify.Lyrics.Searchers
{
    public class QQMusicSearchResult : ISearchResult
    {
        public ISearcher Searcher => new QQMusicSearcher();

        public QQMusicSearchResult(string title, string[] artists, string album, string albumId, string[]? albumArtists, int durationMs, string id, string mid)
        {
            Title = title;
            Artists = artists;
            Album = album;
            AlbumId = albumId;
            AlbumArtists = albumArtists;
            DurationMs = durationMs;
            Id = id;
            Mid = mid;
        }

        public QQMusicSearchResult(Song song) : this(
            WithoutHighlight(song.Title ?? song.Name),
            song.Singer?.Select(s => WithoutHighlight(s.Name)).ToArray() ?? [],
            WithoutHighlight(song.Album?.Title),
            song.Album?.Mid ?? "",
            null,
            song.Interval * 1000,
            song.Id,
            song.Mid
            )
        {
            Subtitle = WithoutHighlight(song.Subtitle);
        }

        private static string WithoutHighlight(string? text)
            => text?.Replace("<em>", "", StringComparison.Ordinal)
                .Replace("</em>", "", StringComparison.Ordinal) ?? "";

        public string? Subtitle { get; }

        public string Title { get; }

        public string[] Artists { get; }

        public string Album { get; }
        public string? AlbumId { get; }
        public string? AlbumPicUrl { get; }

        public string Id { get; }

        public string Mid { get; }

        public string[]? AlbumArtists { get; }

        public int? DurationMs { get; }

        public CompareHelper.MatchType? MatchType { get; set; }
    }
}
