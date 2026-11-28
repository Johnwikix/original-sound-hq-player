using Lyricify.Lyrics.Decrypter.Krc;
using Lyricify.Lyrics.Serialization;

namespace Lyricify.Lyrics.Providers.Web.Kugou
{
    public class Api : BaseApi
    {
        protected override string? HttpRefer => null;

        protected override Dictionary<string, string>? AdditionalHeaders => null;

        public Task<SearchSongResponse?> GetSearchSong(string keywords) => GetSearchSong(keywords, CancellationToken.None);

        public async Task<SearchSongResponse?> GetSearchSong(string keywords, CancellationToken cancellationToken)
        {
            var query = Uri.EscapeDataString(keywords);
            var response = await HttpClient.GetStringAsync(
                $"http://mobilecdn.kugou.com/api/v3/search/song?format=json&keyword={query}&page=1&pagesize=20&showtype=1",
                cancellationToken);
            return LyricsJson.Deserialize<SearchSongResponse>(response);
        }

        public Task<SearchLyricsResponse?> GetSearchLyrics(string? keywords = null, int? duration = null, string? hash = null)
            => GetSearchLyrics(keywords, duration, hash, CancellationToken.None);

        public async Task<SearchLyricsResponse?> GetSearchLyrics(string? keywords, int? duration, string? hash,
            CancellationToken cancellationToken)
        {
            string query = Uri.EscapeDataString(keywords ?? "");
            string durationParam = duration.HasValue ? $"&duration={duration.Value}" : "";
            string trackHash = Uri.EscapeDataString(hash ?? "");
            var response = await HttpClient.GetStringAsync(
                $"https://lyrics.kugou.com/search?ver=1&man=yes&client=pc&keyword={query}{durationParam}&hash={trackHash}",
                cancellationToken);
            return LyricsJson.Deserialize<SearchLyricsResponse>(response);
        }

        public async Task<KugouLyricsResponse?> DownloadLyrics(string id, string accessKey, CancellationToken cancellationToken)
        {
            var response = await HttpClient.GetStringAsync(
                $"https://lyrics.kugou.com/download?ver=1&client=pc&id={Uri.EscapeDataString(id)}&accesskey={Uri.EscapeDataString(accessKey)}&fmt=krc&charset=utf8",
                cancellationToken);
            return LyricsJson.Deserialize<KugouLyricsResponse>(response);
        }
    }
}
