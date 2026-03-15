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
                return await CloudMusicSearchHelper.GetSongAlbum(title, album, artist);
            }
        }

        public static async Task<(string,string)> GetLyricsAsync(string title, string album, string artist, CancellationToken cancellationToken = default)
        {
            try
            {
                return await CloudMusicSearchHelper.GetSongLyrics(title, album, artist, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Debug.WriteLine("GetLyricsAsync获取歌词的任务已被取消。");
                return (string.Empty, string.Empty);
            }
            catch
            {
                return (string.Empty, string.Empty);
            }
        }

    }
}
