using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.WebService
{

    public class LrcService
    {
        private static HttpClient _httpClient = new HttpClient();

        public static async Task<byte[]> GetCoverImageAsync(string title, string album, string artist)
        {
            try
            {
                if (!string.IsNullOrEmpty(AppSettings.LrcAPIAuth))
                {
                    _httpClient.DefaultRequestHeaders.Add("Authorization", AppSettings.LrcAPIAuth);
                }
                var requestUrl = $"{AppSettings.LrcAPISource}/cover?title={Uri.EscapeDataString(title)}&album={Uri.EscapeDataString(album)}&artist={Uri.EscapeDataString(artist)}";
                var response = await _httpClient.GetAsync(requestUrl);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsByteArrayAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error fetching image: {ex.Message}");
                return null;
            }
        }

        public static async Task<string> GetLyricsAsync(string title, string album, string artist, CancellationToken cancellationToken = default)
        {
            try
            {
                if (!string.IsNullOrEmpty(AppSettings.LrcAPIAuth))
                {
                    _httpClient.DefaultRequestHeaders.Add("Authorization", AppSettings.LrcAPIAuth);
                }
                var requestUrl = $"{AppSettings.LrcAPISource}/lyrics?title={Uri.EscapeDataString(title)}&album={Uri.EscapeDataString(album)}&artist={Uri.EscapeDataString(artist)}";
                var response = await _httpClient.GetAsync(requestUrl, cancellationToken);
                response.EnsureSuccessStatusCode();
                var result = await response.Content.ReadAsStringAsync(cancellationToken);
                return result;
            }
            catch (OperationCanceledException)
            {
                Debug.WriteLine("GetLyricsAsync获取歌词的任务已被取消。");
                return null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"GetLyricsAsync Error fetching lyrics: {ex.Message}");
                return null;
            }
        }

    }
}
