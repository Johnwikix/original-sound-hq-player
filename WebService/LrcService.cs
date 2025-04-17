using System;
using System.Net.Http;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.WebService
{

    public class LrcService
    {
        private readonly HttpClient _httpClient;
        public LrcService()
        {
            _httpClient = new HttpClient();
            if (!string.IsNullOrEmpty(AppSettings.LrcAPIAuth))
            {
                _httpClient.DefaultRequestHeaders.Add("Authorization", AppSettings.LrcAPIAuth);
            }
        }

        public async Task<byte[]> GetCoverImageAsync(string title, string album, string artist)
        {
            try
            {
                var requestUrl = $"{AppSettings.LrcAPISource}/cover?title={Uri.EscapeDataString(title)}&album={Uri.EscapeDataString(album)}&artist={Uri.EscapeDataString(artist)}";
                var response = await _httpClient.GetAsync(requestUrl);
                response.EnsureSuccessStatusCode();
                // 直接将响应内容读取为字节数组
                return await response.Content.ReadAsByteArrayAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error fetching image: {ex.Message}");
                return null;
            }
        }

        public async Task<string> GetLyricsAsync(string title, string album, string artist)
        {
            try
            {
                var requestUrl = $"{AppSettings.LrcAPISource}/lyrics?title={Uri.EscapeDataString(title)}&album={Uri.EscapeDataString(album)}&artist={Uri.EscapeDataString(artist)}";
                var response = await _httpClient.GetAsync(requestUrl);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error fetching lyrics: {ex.Message}");
                return null;
            }
        }

    }
}
