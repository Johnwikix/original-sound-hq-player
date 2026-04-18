using Lyricify.Lyrics.Helpers;
using Lyricify.Lyrics.Models;
using Lyricify.Lyrics.Searchers;
using Lyricify.Lyrics.Searchers.Helpers;
using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.OnlineAPIs.CloudMusicAPI;

namespace WinUIMusicPlayer.WebService
{

    public class LrcService
    {
        public static async Task<byte[]> GetCoverImageAsync(string title, string album, string artist, CancellationToken cancellationToken = default)
        {
            try
            {
                return await CloudMusicSearchHelper.GetSongAlbum(title, album, artist, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception)
            {
                return await CloudMusicSearchHelper.GetSongAlbum(title, album, artist, cancellationToken);
            }
        }
        public static async Task<(string, string)> GetMixedLyricsAsync(Music music, CancellationToken cancellationToken = default)
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

        public static async Task<(string,string)> GetLyricsAsync(Music music,Searchers searchers = Searchers.QQMusic, CancellationToken cancellationToken = default)
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

        public static async Task<(string, string)> GetKrcLyricsAsync(Music music, CancellationToken cancellationToken = default)
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

    }
}
