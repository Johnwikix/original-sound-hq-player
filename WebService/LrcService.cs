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
        private static HttpClient _httpClient = new HttpClient();

        public static async Task<byte[]> GetCoverImageAsync(string title, string album, string artist, CancellationToken cancellationToken = default)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(AppSettings.LrcAPIAuth))
                {
                    _httpClient.DefaultRequestHeaders.Add("Authorization", AppSettings.LrcAPIAuth);
                }
                if (string.IsNullOrWhiteSpace(AppSettings.LrcAPISource) || AppSettings.LrcAPISource == "http://music.163.com") {
                    return await CloudMusicSearchHelper.GetSongAlbum(title, album, artist, cancellationToken);
                }
                var requestUrl = $"{AppSettings.LrcAPISource}/cover?title={Uri.EscapeDataString(title)}&album={Uri.EscapeDataString(album)}&artist={Uri.EscapeDataString(artist)}";
                var response = await _httpClient.GetAsync(requestUrl);
                response.EnsureSuccessStatusCode();
                byte[] result = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                if (result == null) {
                    return await CloudMusicSearchHelper.GetSongAlbum(title, album, artist, cancellationToken);
                }
                return result;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception ex)
            {
                return await CloudMusicSearchHelper.GetSongAlbum(title, album, artist);
            }
        }

        public static async Task<string> GetLyricsAsync(string title, string album, string artist, CancellationToken cancellationToken = default)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(AppSettings.LrcAPIAuth))
                {
                    _httpClient.DefaultRequestHeaders.Add("Authorization", AppSettings.LrcAPIAuth);
                }
                if (string.IsNullOrWhiteSpace(AppSettings.LrcAPISource) || AppSettings.LrcAPISource == "http://music.163.com")
                {
                    return await CloudMusicSearchHelper.GetSongLyrics(title, album, artist, cancellationToken);
                }
                var requestUrl = $"{AppSettings.LrcAPISource}/lyrics?title={Uri.EscapeDataString(title)}&album={Uri.EscapeDataString(album)}&artist={Uri.EscapeDataString(artist)}";
                var response = await _httpClient.GetAsync(requestUrl, cancellationToken);
                response.EnsureSuccessStatusCode();
                var result = await response.Content.ReadAsStringAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(result)) {
                    return await CloudMusicSearchHelper.GetSongLyrics(title, album, artist, cancellationToken);
                }
                result += await CloudMusicSearchHelper.GetTranslateSongLyrics(title, album, artist, cancellationToken);
                return result;
            }
            catch (OperationCanceledException)
            {
                Debug.WriteLine("GetLyricsAsync获取歌词的任务已被取消。");
                return null;
            }
            catch (Exception ex)
            {
                return await CloudMusicSearchHelper.GetSongLyrics(title, album, artist, cancellationToken);
            }
        }

    }
}
