using Lyricify.Lyrics.Providers.Web.Netease;
using Lyricify.Lyrics.Searchers.Helpers;

namespace Lyricify.Lyrics.Searchers
{
    public class NeteaseSearchResult : ISearchResult
    {
        public ISearcher Searcher => new NeteaseSearcher();

        public NeteaseSearchResult(string title, string[] artists, string album, string albumId, string albumPicUrl, string[]? albumArtists, int durationMs, string id)
        {
            Title = title;
            Artists = artists;
            Album = album;
            AlbumId = albumId;
            AlbumPicUrl = albumPicUrl;
            AlbumArtists = albumArtists;
            DurationMs = durationMs;
            Id = id;
        }

        public NeteaseSearchResult(Song song) : this(
            song.Name,
            song.Artists.Select(s => s.Name).ToArray(),
            song.Album.Name,
            song.Album.Id.ToString(),
            song.Album.PicUrl,
            null,
            (int)song.Duration,
            song.Id
            )
        { }

        public string Title { get; }

        public string[] Artists { get; }

        public string Album { get; }
        public string? AlbumId { get; }
        public string? AlbumPicUrl { get; }

        public string Id { get; }

        public string[]? AlbumArtists { get; }

        public int? DurationMs { get; }

        public CompareHelper.MatchType? MatchType { get; set; }
    }
}
