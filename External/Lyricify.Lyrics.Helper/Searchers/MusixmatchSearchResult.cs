using Lyricify.Lyrics.Providers.Web.Musixmatch;
using Lyricify.Lyrics.Searchers.Helpers;

namespace Lyricify.Lyrics.Searchers
{
    public class MusixmatchSearchResult : ISearchResult
    {
        public ISearcher Searcher => new MusixmatchSearcher();

        public MusixmatchSearchResult(string title, string[] artists, string album,string albumId, string[]? albumArtists, int durationMs, int id, string isrc, string vanityId)
        {
            Title = title;
            Artists = artists;
            Album = album;
            AlbumId = albumId;
            AlbumArtists = albumArtists;
            DurationMs = durationMs;
            Id = id;
            Isrc = isrc;
            VanityId = vanityId;
        }

        public MusixmatchSearchResult(GetTrackResponse.Track track) : this(
            track.TrackName,
            track.ArtistName.Split(new string[] { " feat. ", " & " }, StringSplitOptions.RemoveEmptyEntries),
            track.AlbumName,
            track.AlbumId.ToString(),
            null,
            track.TrackLength * 1000,
            track.TrackId,
            track.TrackIsrc,
            track.CommontrackVanityId
            )
        { }

        public string Title { get; }

        public string[] Artists { get; }

        public string Album { get; }
        public string? AlbumId { get; }
        public string? AlbumPicUrl { get; }

        public int Id { get; }

        public string Isrc { get; }

        public string[]? AlbumArtists { get; }

        public int? DurationMs { get; }

        public string VanityId { get; set; }

        public CompareHelper.MatchType? MatchType { get; set; }
    }
}
