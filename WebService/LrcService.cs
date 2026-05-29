using Lyricify.Lyrics.Helpers;
using Lyricify.Lyrics.Models;
using Lyricify.Lyrics.Searchers;
using Lyricify.Lyrics.Searchers.Helpers;
using Microsoft.Extensions.Logging;
using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.WebService
{

    public class LrcService : IDisposable
    {
        private HttpClient _httpClient;
        private readonly HttpClientHandler _clientHandler;
        private ILogger<LrcService> _logger;
        public LrcService(ILogger<LrcService> logger)
        {
            _logger = logger;
            _clientHandler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.None,
                UseCookies = true,
            };
            _httpClient = new HttpClient(_clientHandler);
        }
        public async Task<byte[]?> GetMixedCoverImageAsync(Music music, CancellationToken cancellationToken = default)
        {
            try
            {
                if (AppData.UnknownAlbums.Contains(music.Album))
                {
                    return null;
                }
                var imageBytes = await GetCoverImageAsync(music, Searchers.Netease, cancellationToken);
                if (imageBytes is null || imageBytes.Length == 0)
                {
                    return await GetCoverImageAsync(music, Searchers.QQMusic, cancellationToken);
                }
                return imageBytes;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"GetMixedCoverImageAsync 歌词获取失败: {ex.Message}");
                return null;
            }
        }
        public async Task<byte[]?> GetCoverImageAsync(Music music, Searchers searchers = Searchers.Netease, CancellationToken cancellationToken = default)
        {
            try
            {
                var search = await SearchHelper.Search(new TrackMultiArtistMetadata()
                {
                    Album = music.Album,
                    Title = music.Title,
                }, searchers, CompareHelper.MatchType.Low);
                cancellationToken.ThrowIfCancellationRequested();
                if (search is NeteaseSearchResult neteaseSearch)
                {
                    return await GetSongAlbumPicData(neteaseSearch?.AlbumPicUrl, cancellationToken);
                }
                else if (search is QQMusicSearchResult qQMusicSearchResult)
                {
                    return await GetSongAlbumPicDataFromQQ(qQMusicSearchResult?.AlbumId, cancellationToken);
                }
                return null;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"GetCoverImageAsync 歌词获取失败: {ex.Message}");
                return null;
            }
        }
        public async Task<(string, string)> GetMixedLyricsAsync(Music music, CancellationToken cancellationToken = default)
        {
            try
            {
                var (lyrics, trans) = await GetLyricsAsync(music, Searchers.Netease, cancellationToken);
                if (string.IsNullOrEmpty(lyrics))
                {
                    return await GetLyricsAsync(music, Searchers.QQMusic, cancellationToken);
                }
                else
                {
                    return (lyrics, trans);
                }
            }
            catch (OperationCanceledException)
            {
                return (string.Empty, string.Empty);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"GetMixedLyricsAsync 歌词获取失败: {ex.Message}");
                return (string.Empty, string.Empty);
            }
        }

        public async Task<(string, string)> GetLyricsAsync(Music music, Searchers searchers = Searchers.QQMusic, CancellationToken cancellationToken = default)
        {
            try
            {
                var search = await SearchHelper.Search(new TrackMultiArtistMetadata()
                {
                    Album = music.Album,
                    Artists = [music.Author],
                    DurationMs = (int)music.Duration.TotalMilliseconds,
                    Title = music.Title,
                }, searchers, CompareHelper.MatchType.Low);
                cancellationToken.ThrowIfCancellationRequested();
                if (search is NeteaseSearchResult neteaseSearch)
                {
                    var res = await ProviderHelper.NeteaseApi.GetLyric(neteaseSearch.Id);
                    cancellationToken.ThrowIfCancellationRequested();
                    return (res?.Lrc.Lyric ?? string.Empty, (AppData.SystemLanguage.Contains("zh") == true ? res?.Tlyric.Lyric ?? string.Empty : string.Empty));
                }
                else if (search is QQMusicSearchResult qQMusicSearchResult)
                {
                    var res = await ProviderHelper.QQMusicApi.GetLyric(qQMusicSearchResult.Mid);
                    cancellationToken.ThrowIfCancellationRequested();
                    return (res?.Lyric ?? string.Empty, (AppData.SystemLanguage.Contains("zh") == true ? res?.Trans ?? string.Empty : string.Empty));
                }
                return (string.Empty, string.Empty);
            }
            catch (OperationCanceledException)
            {
                return (string.Empty, string.Empty);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"GetLyricsAsync 歌词获取失败: {ex.Message}");
                return (string.Empty, string.Empty);
            }
        }

        public async Task<(string, string)> GetKrcLyricsAsync(Music music, CancellationToken cancellationToken = default)
        {
            try
            {
                var search = await SearchHelper.Search(new TrackMultiArtistMetadata()
                {
                    Album = music.Album,
                    Artists = [music.Author],
                    DurationMs = (int)music.Duration.TotalMilliseconds,
                    Title = music.Title,
                }, Searchers.QQMusic, CompareHelper.MatchType.Medium);
                cancellationToken.ThrowIfCancellationRequested();
                if (search is QQMusicSearchResult qQMusicSearchResult)
                {
                    var res = await ProviderHelper.QQMusicApi.GetLyricsAsync(qQMusicSearchResult.Id);
                    cancellationToken.ThrowIfCancellationRequested();
                    return (res?.Lyrics ?? string.Empty, (AppData.SystemLanguage.Contains("zh") == true ? res?.Trans ?? string.Empty : string.Empty));
                }
                return (string.Empty, string.Empty);
            }
            catch (OperationCanceledException)
            {
                return (string.Empty, string.Empty);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"GetKrcLyricsAsync 歌词获取失败: {ex.Message}");
                return (string.Empty, string.Empty);
            }
        }

        public async Task<byte[]?> GetSongAlbumPicData(string? url, CancellationToken cancellationToken = default)
        {
            try
            {
                if (string.IsNullOrEmpty(url)) return null;
                cancellationToken.ThrowIfCancellationRequested();
                byte[] imageBytes = await _httpClient.GetByteArrayAsync(url, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                return imageBytes;
            }
            catch (HttpRequestException ex)
            {
                return null;
            }
            catch (TaskCanceledException)
            {
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"GetSongAlbumPicData 歌词获取失败: {ex.Message}");
                return null;
            }
        }

        public async Task<byte[]?> GetSongAlbumPicDataFromQQ(string? albumMid, CancellationToken cancellationToken = default)
        {
            try
            {
                if (string.IsNullOrEmpty(albumMid)) return null;
                string url = $"https://y.qq.com/music/photo_new/T002R800x800M000{albumMid}.jpg";
                cancellationToken.ThrowIfCancellationRequested();
                byte[] imageBytes = await _httpClient.GetByteArrayAsync(url, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                return imageBytes;
            }
            catch (HttpRequestException ex)
            {
                return null;
            }
            catch (TaskCanceledException)
            {
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"GetSongAlbumPicDataFromQQ 歌词获取失败: {ex.Message}");
                return null;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            if (disposing)
            {
                _httpClient.Dispose();
                _clientHandler.Dispose();
            }
        }
    }
}
