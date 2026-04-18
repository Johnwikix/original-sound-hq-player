using Lyricify.Lyrics.Providers.Web.SodaMusic;
using Lyricify.Lyrics.Searchers.Helpers;

namespace Lyricify.Lyrics.Searchers
{
    public class SodaMusicSearchResult : ISearchResult
    {
        public ISearcher Searcher => new SodaMusicSearcher();

        public SodaMusicSearchResult(string title, string[] artists, string album, string albumId, string albumPicUrl, string[]? albumArtists, int durationMs, string id)
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

        public SodaMusicSearchResult(ResultGroupItem Track) : this(
            Track.Entity.Track.Name,
            Track.Entity.Track.Artists.Select(a => a.Name).ToArray(),
            Track.Entity.Track.Album.Name,
            Track.Entity.Track.Album.Id,
            Track.Entity.Track.Album.UrlPlayerBg.Uri,
            null,
            (int)Track.Entity.Track.Duration,
            Track.Entity.Track.Id
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
