using Lyricify.Lyrics.Helpers;
using Lyricify.Lyrics.Models;
using Lyricify.Lyrics.Searchers;
using Lyricify.Lyrics.Searchers.Helpers;
using OriginalSound.Plugin;
using TrackMetadata = OriginalSound.Plugin.TrackMetadata;

namespace OriginalSound.Plugins;

public sealed class LyricsSearchPlugin : IPlayerPlugin
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    public ValueTask InitializeAsync(string dataDirectory, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public async ValueTask<PluginReply> HandleAsync(PluginRequest request, CancellationToken ct)
    {
        if (request.Track is not { } track) return new() { Result = PluginResult.NoResult };
        if (request.Method == "cover") return await CoverAsync(track, ct);
        if (request.Method is not ("lyrics" or "search")) return new() { Result = PluginResult.Unsupported };
        var candidates = new List<LyricsCandidate>();
        bool failed = false;
        if (request.WordSynced || request.Method == "search")
            await AddAsync(Searchers.QQMusic, true);
        if (!request.WordSynced || request.Method == "search")
        {
            await AddAsync(Searchers.Netease, false);
            if (candidates.Count == 0 || request.Method == "search") await AddAsync(Searchers.QQMusic, false);
        }
        return new() { Result = candidates.Count > 0 ? PluginResult.Found : failed ? PluginResult.NetworkError : PluginResult.NoResult, Lyrics = candidates.ToArray() };

        async Task AddAsync(Searchers provider, bool wordSynced)
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                var metadata = new TrackMultiArtistMetadata
                {
                    Title = track.Title, Album = track.Album, Artists = [track.Artist],
                    DurationMs = (int)Math.Clamp(track.DurationMs ?? 0, 0, int.MaxValue)
                };
                var match = await SearchHelper.Search(metadata, provider,
                    wordSynced ? CompareHelper.MatchType.Medium : CompareHelper.MatchType.Low);
                ct.ThrowIfCancellationRequested();
                string text = "", translation = "", id = "";
                if (match is QQMusicSearchResult qq)
                {
                    id = qq.Mid;
                    if (wordSynced)
                    {
                        var result = await ProviderHelper.QQMusicApi.GetLyricsAsync(qq.Id);
                        text = result?.Lyrics ?? "";
                        translation = result?.Trans ?? "";
                    }
                    else
                    {
                        var result = await ProviderHelper.QQMusicApi.GetLyric(qq.Mid);
                        text = result?.Lyric ?? "";
                        translation = result?.Trans ?? "";
                    }
                }
                else if (match is NeteaseSearchResult ne)
                {
                    id = ne.Id.ToString();
                    var result = await ProviderHelper.NeteaseApi.GetLyric(ne.Id);
                    text = result?.Lrc?.Lyric ?? "";
                    translation = result?.Tlyric?.Lyric ?? "";
                }
                ct.ThrowIfCancellationRequested();
                if (!string.IsNullOrWhiteSpace(text))
                    candidates.Add(new($"{provider}:{id}:{wordSynced}", track.Title, provider.ToString(),
                        wordSynced ? "qrc" : "lrc", text, track.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? translation : ""));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { failed = true; }
        }
    }

    private async Task<PluginReply> CoverAsync(TrackMetadata track, CancellationToken ct)
    {
        bool failed = false;
        foreach (var provider in new[] { Searchers.Netease, Searchers.QQMusic })
        {
            try
            {
                var match = await SearchHelper.Search(new TrackMultiArtistMetadata { Title = track.Title, Album = track.Album },
                    provider, CompareHelper.MatchType.Low);
                ct.ThrowIfCancellationRequested();
                string? url = match switch
                {
                    NeteaseSearchResult ne => ne.AlbumPicUrl,
                    QQMusicSearchResult qq when !string.IsNullOrEmpty(qq.AlbumId) => $"https://y.qq.com/music/photo_new/T002R800x800M000{qq.AlbumId}.jpg",
                    _ => null
                };
                if (url is null) continue;
                using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
                using var bytes = new MemoryStream();
                using var body = await response.Content.ReadAsStreamAsync(ct);
                byte[] buffer = new byte[16384];
                int count;
                while ((count = await body.ReadAsync(buffer, ct)) != 0)
                {
                    if (bytes.Length + count > 4 * 1024 * 1024) throw new InvalidDataException("Cover too large.");
                    bytes.Write(buffer, 0, count);
                }
                if (bytes.Length > 0) return new() { Result = PluginResult.Found, Cover = bytes.ToArray() };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { failed = true; }
        }
        return new() { Result = failed ? PluginResult.NetworkError : PluginResult.NoResult };
    }
    public ValueTask DisposeAsync() { _http.Dispose(); return ValueTask.CompletedTask; }
}
