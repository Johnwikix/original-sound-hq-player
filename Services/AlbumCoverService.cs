using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.Services
{
    public class AlbumCoverService
    {
        private static Dictionary<string, BitmapImage> _albumCoverCache = new Dictionary<string, BitmapImage>();

        public static async Task LoadAlbumCoversAsync(List<Music> musics)
        {
            try
            {
                var groupedAlbums = musics.GroupBy(m => m.Album)
                                           .Select(g => g.First())
                                           .ToList();
                foreach (var album in groupedAlbums)
                {
                    if (_albumCoverCache.TryGetValue(album.Album, out var cachedCover))
                    {
                        album.Cover = cachedCover;
                    }
                    else
                    {
                        BitmapImage cover = await ToolUtils.GetAlbumCover(album, musics);
                        album.Cover = cover;
                        _albumCoverCache[album.Album] = cover;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"加载专辑封面失败: {ex.Message}");
            }
        }

        public static void ClearCache()
        {
            _albumCoverCache.Clear();
        }
    }
}
