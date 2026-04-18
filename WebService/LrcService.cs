using Lyricify.Lyrics.Helpers;
using Lyricify.Lyrics.Models;
using Lyricify.Lyrics.Searchers;
using Lyricify.Lyrics.Searchers.Helpers;
using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Protection.PlayReady;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.OnlineAPIs.CloudMusicAPI;

namespace WinUIMusicPlayer.WebService
{

    public class LrcService:IDisposable
    {
        private HttpClient _httpClient;
        private readonly HttpClientHandler _clientHandler;
        public LrcService() {
            _clientHandler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.None,
                UseCookies = true,
            };
            _httpClient = new HttpClient(_clientHandler);
        }

        public async Task<byte[]?> GetCoverImageAsync(Music music, CancellationToken cancellationToken = default)
        {
            try
            {
                var search = await SearchHelper.Search(new TrackMultiArtistMetadata()
                {
                    Album = music.Album,
                    Artists = [music.Author],
                    DurationMs = (int)music.Duration.TotalMilliseconds,
                    Title = music.Title,
                }, Searchers.Netease, CompareHelper.MatchType.Medium);
                cancellationToken.ThrowIfCancellationRequested();
                return await GetSongAlbumPicData(search?.AlbumPicUrl, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception)
            {
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
            catch (Exception)
            {
                return (string.Empty, string.Empty);
            }
        }

        public async Task<(string,string)> GetLyricsAsync(Music music,Searchers searchers = Searchers.QQMusic, CancellationToken cancellationToken = default)
        {
            try
            {
                var search = await SearchHelper.Search(new TrackMultiArtistMetadata()
                {
                    Album = music.Album,
                    Artists = [music.Author],
                    DurationMs = (int)music.Duration.TotalMilliseconds,
                    Title = music.Title,
                }, searchers, CompareHelper.MatchType.Medium);
                cancellationToken.ThrowIfCancellationRequested();
                if (search is NeteaseSearchResult neteaseSearch)
                {
                    var res = await ProviderHelper.NeteaseApi.GetLyric(neteaseSearch.Id);
                    cancellationToken.ThrowIfCancellationRequested();
                    return (res?.Lrc.Lyric ?? string.Empty, res?.Tlyric.Lyric ?? string.Empty);
                }
                else if (search is QQMusicSearchResult qQMusicSearchResult) {
                    var res = await ProviderHelper.QQMusicApi.GetLyric(qQMusicSearchResult.Mid);
                    cancellationToken.ThrowIfCancellationRequested();
                    return (res?.Lyric ?? string.Empty, res?.Trans ?? string.Empty);
                }
                return (string.Empty, string.Empty);
            }
            catch (OperationCanceledException)
            {
                return (string.Empty, string.Empty);
            }
            catch
            {
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
                    return (res?.Lyrics ?? string.Empty, res?.Trans ?? string.Empty);
                }
                return (string.Empty, string.Empty);
            }
            catch (OperationCanceledException)
            {
                return (string.Empty, string.Empty);
            }
            catch
            {
                return (string.Empty, string.Empty);
            }
        }

        public async Task<byte[]?> GetSongAlbumPicData(string? url, CancellationToken cancellationToken = default)
        {
            try
            {
                if(string.IsNullOrEmpty(url)) return null;
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
