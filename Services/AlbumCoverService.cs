using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.Services
{
    public class AlbumCoverService
    {

        public static async Task LoadAlbumCoversAsync(List<Music> musics)
        {
            try
            {
                foreach (var album in musics)
                {
                    if (AppData.albumCoverCache.TryGetValue(album.Album, out var cachedCover))
                    {
                        album.Cover = cachedCover;
                    }
                    else
                    {
                        BitmapImage cover = await ToolUtils.GetAlbumCover(album);
                        album.Cover = cover;
                        if (AppSettings.isCoverCacheEnabled)
                        {
                            AppData.albumCoverCache[album.Album] = cover;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"加载专辑封面失败: {ex.Message}");
            }
        }

        public static async Task LoadAlbumCoversInCacheAsync(List<Music> musics)
        {
            try
            {
                var groupedAlbums = musics.GroupBy(m => m.Album)
                                           .Select(g => g.First())
                                           .OrderBy(m => m.Album)
                                           .Take(AppSettings.maxCoverPreLoadNum)
                                           .ToList();
                foreach (var album in groupedAlbums)
                {
                    if (!AppData.albumCoverCache.TryGetValue(album.Album, out var cachedCover))
                    {
                        AppData.albumCoverCache[album.Album] = await ToolUtils.GetAlbumCover(album);
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
            AppData.albumCoverCache.Clear();
        }
    }
}
