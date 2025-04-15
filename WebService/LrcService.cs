using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.WebService
{
    
    public class LrcService
    {
        private readonly HttpClient _httpClient;
        public LrcService() {
            _httpClient = new HttpClient();
            if (!string.IsNullOrEmpty(AppSettings.LrcAPIAuth))
            {
                _httpClient.DefaultRequestHeaders.Add("Authorization", AppSettings.LrcAPIAuth);
            }            
        }

        public async Task<BitmapImage> GetCoverImageAsync(string title, string album, string artist)
        {
            try
            {
                var requestUrl = $"{AppSettings.LrcAPISource}/cover?title={Uri.EscapeDataString(title)}&album={Uri.EscapeDataString(album)}&artist={Uri.EscapeDataString(artist)}";
                var response = await _httpClient.GetAsync(requestUrl);
                response.EnsureSuccessStatusCode();

                var stream = await response.Content.ReadAsStreamAsync();
                var bitmapImage = new BitmapImage();
                using (var memStream = new MemoryStream())
                {
                    await stream.CopyToAsync(memStream);
                    memStream.Position = 0;
                    await bitmapImage.SetSourceAsync(memStream.AsRandomAccessStream());
                }

                return bitmapImage;
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
