using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.VisualBasic.FileIO;
using Microsoft.Windows.ApplicationModel.Resources;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using System.Threading.Tasks;
using TagLib;
using Windows.Devices.Enumeration;
using Windows.Graphics.Imaging;
using Windows.Media.Devices;
using Windows.Storage;
using Windows.Storage.Streams;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;

namespace WinUIMusicPlayer.Utils
{
    public class ToolUtils
    {
        private ResourceLoader resourceLoader = new ResourceLoader();
        public static readonly Dictionary<string, float> FrequencyMap = new Dictionary<string, float>
        {
            ["32Hz"] = 32f,
            ["64Hz"] = 64f,
            ["125Hz"] = 125f,
            ["250Hz"] = 250f,
            ["500Hz"] = 500f,
            ["1kHz"] = 1000f,
            ["2kHz"] = 2000f,
            ["4kHz"] = 4000f,
            ["8kHz"] = 8000f,
            ["16kHz"] = 16000f
        };

        public static readonly Dictionary<float, string> FloatToString = new Dictionary<float, string>
        {
            [32f] = "32Hz",
            [64f] = "64Hz",
            [125f] = "125Hz",
            [250f] = "250Hz",
            [500f] = "500Hz",
            [1000f] = "1kHz",
            [2000f] = "2kHz",
            [4000f] = "4kHz",
            [8000f] = "8kHz",
            [16000f] = "16kHz"
        };

        public static string GetString(string key)
        {
            try
            {
                return new ResourceLoader().GetString(key);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"获取字符串失败: {ex.Message}");
                return key;
            }
        }
        public enum PlayMode
        {
            SingleLoop,
            ListLoop,
            RandomLoop,
            RepeatOff
        }

        public static async Task RefreshDevice()
        {
            try
            {
                string selector = MediaDevice.GetAudioRenderSelector();
                DeviceInformationCollection devices = await DeviceInformation.FindAllAsync(selector);
                AppSettings.outputDeviceList.Clear();
                foreach (DeviceInformation device in devices)
                {
                    AppSettings.outputDeviceList.Add(device.Name);
                }                
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"刷新音频设备失败: {ex.Message}");
            }
        }

        public static Microsoft.UI.Windowing.AppWindow GetAppWindowForCurrentWindow(Window window)
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            return Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
        }

        public static T FindParent<T>(DependencyObject child) where T : DependencyObject
        {
            DependencyObject parent = VisualTreeHelper.GetParent(child);
            if (parent == null)
                return null;
            T parentAsT = parent as T;
            return parentAsT ?? FindParent<T>(parent);
        }

        //private static async Task<BitmapImage> DefaultAlbumCover(int size = 150)
        //{
        //    var tcs = new TaskCompletionSource<BitmapImage>();

        //    App.MainWindow.DispatcherQueue.TryEnqueue(() =>
        //    {
        //        try
        //        {
        //            var uri = new Uri("ms-appx:///Assets/Album.png");
        //            var bitmapImage = new BitmapImage(uri);
        //            if (size != 0) {
        //                bitmapImage.DecodePixelWidth = size;
        //                bitmapImage.DecodePixelHeight = size;
        //            }                    
        //            tcs.SetResult(bitmapImage);
        //        }
        //        catch (Exception ex)
        //        {
        //            tcs.SetException(ex);
        //        }
        //    });
        //    return await tcs.Task;
        //}



        public static void RefreshIcon(ObservableCollection<Music> musicList, string type = "album")
        {
            foreach (var item in musicList)
            {
                if (type == "album")
                {
                    if (AppData.musicOnUsbDevice.Any(usbMusic => usbMusic.Album == item.Album))
                    {
                        item.IsExistOnDevice = 1;
                    }
                    else
                    {
                        item.IsExistOnDevice = 0;
                    }
                }
                else if (type == "artist")
                {
                    if (AppData.musicOnUsbDevice.Any(usbMusic => usbMusic.Author == item.Author))
                    {
                        item.IsExistOnDevice = 1;
                    }
                    else
                    {
                        item.IsExistOnDevice = 0;
                    }
                }
                else if (type == "folder")
                {
                    var allSongList = AppData.allSongs.Where(m => m.LastLevelFolderPath == item.LastLevelFolderPath).ToList();
                    item.IsExistOnDevice = 0;
                    foreach (var songs in allSongList)
                    {
                        if (AppData.musicOnUsbDevice.Any(usbMusic => usbMusic.Title == item.Title))
                        {
                            item.IsExistOnDevice = 1;
                            break;
                        }
                    }
                }

            }
        }

        public static async Task<BitmapImage> GetAlbumCover(Music album,int coverSize = 150)
        {
            BitmapImage newCover = album.Cover;
            List<Music> musics = MusicDatabaseService.GetAlbumMusicFromMem(album.Album);
            if (album.Album != "未知专辑")
            {
                if (musics == null || musics.Count() == 0)
                {
                    return null;
                }
                foreach (var song in musics)
                {
                    try
                    {
                        using (var file = TagLib.File.Create(song.Path))
                        {
                            if (file.Tag.Pictures.Length > 0)
                            {
                                var picture = file.Tag.Pictures[0];
                                newCover = await ReadBitmapImageAsync(picture, coverSize);
                            }
                            else
                            {
                                newCover = null;
                            }
                            return newCover;
                        }
                    }
                    catch (Exception ex)
                    {
                        //newCover = await DefaultAlbumCover();
                        Debug.WriteLine($"读取专辑 {album.Album} 封面失败: {ex.Message}");
                        return null;
                    }
                }
            }
            else
            {
                //newCover = await DefaultAlbumCover();
                return null;
            }
            return newCover;
        }


        public static async Task<BitmapImage> ReadBitmapImageAsync(IPicture picture, int maxSize = 0,int defaultImgSize = 150)
        {
            byte[] imageData = picture.Data.Data.ToArray();
            if (picture?.Data?.Data == null)
            {
                return null;
            }
            if (!IsValidImageData(imageData))
            {
                return null;
            }
            var tcs = new TaskCompletionSource<BitmapImage>();
            App.MainWindow.DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    using (var ms = new MemoryStream(imageData))
                    {
                        var bitmapImage = new BitmapImage { DecodePixelWidth = defaultImgSize };
                        await bitmapImage.SetSourceAsync(ms.AsRandomAccessStream());
                        tcs.SetResult(bitmapImage);
                    }
                }
                catch (COMException ex)
                {
                    tcs.SetException(new InvalidOperationException("图片格式无效或已损坏", ex));
                }
                catch (TaskCanceledException)
                {
                    tcs.SetCanceled();
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });
            return await tcs.Task;
        }

        private static bool IsValidImageData(byte[] data)
        {
            if (data == null || data.Length < 10) return false;

            // 检查常见图像文件头
            return IsPng(data) || IsJpeg(data) || IsGif(data) || IsBmp(data) || IsWebP(data) || IsTiff(data);
        }

        private static bool IsTiff(byte[] data)
        {
            // II* or MM*
            return data.Length >= 4 &&
                  ((data[0] == 0x49 && data[1] == 0x49 && data[2] == 0x2A && data[3] == 0x00) ||
                   (data[0] == 0x4D && data[1] == 0x4D && data[2] == 0x00 && data[3] == 0x2A));
        }

        private static bool IsWebP(byte[] data)
        {
            // RIFF....WEBP
            return data.Length >= 12 &&
                   data[0] == 0x52 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x46 &&
                   data[8] == 0x57 && data[9] == 0x45 && data[10] == 0x42 && data[11] == 0x50;
        }

        private static bool IsPng(byte[] data)
        {
            return data.Length >= 8 &&
                   data[0] == 0x89 &&
                   data[1] == 0x50 &&
                   data[2] == 0x4E &&
                   data[3] == 0x47 &&
                   data[4] == 0x0D &&
                   data[5] == 0x0A &&
                   data[6] == 0x1A &&
                   data[7] == 0x0A;
        }

        private static bool IsJpeg(byte[] data)
        {
            return data.Length >= 2 &&
                   data[0] == 0xFF &&
                   data[1] == 0xD8;
        }

        private static bool IsGif(byte[] data)
        {
            return data.Length >= 6 &&
                   data[0] == 0x47 &&
                   data[1] == 0x49 &&
                   data[2] == 0x46 &&
                   data[3] == 0x38 &&
                  (data[4] == 0x37 || data[4] == 0x39) &&
                   data[5] == 0x61;
        }

        private static bool IsBmp(byte[] data)
        {
            return data.Length >= 2 &&
                   data[0] == 0x42 &&
                   data[1] == 0x4D;
        }

        public static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typedChild)
                {
                    return typedChild;
                }
                else
                {
                    var foundChild = FindVisualChild<T>(child);
                    if (foundChild != null)
                    {
                        return foundChild;
                    }
                }
            }
            return null;
        }


        public static async Task<BitmapImage> ConvertByteArrayToBitmapImage(byte[] imageData, int maxSize = 0)
        {
            try
            {
                if (imageData == null || imageData.Length == 0)
                    return null;
                using (var stream = new InMemoryRandomAccessStream())
                {
                    using (var dataWriter = new DataWriter(stream.GetOutputStreamAt(0)))
                    {
                        dataWriter.WriteBytes(imageData);
                        await dataWriter.StoreAsync();
                    }
                    var bitmapImage = maxSize==0? new BitmapImage():new BitmapImage { DecodePixelWidth = maxSize };
                    await bitmapImage.SetSourceAsync(stream);
                    return bitmapImage;
                }
            }
            catch (Exception ex)
            {
                return null;
            }
        }

        public static async Task<byte[]> GetRawImage(Music music)
        {
            try
            {
                using (TagLib.File audioFile = TagLib.File.Create(music.Path))
                {
                    // 获取音频文件中的图片数组
                    IPicture[] pictures = audioFile.Tag.Pictures;

                    if (pictures.Length > 0)
                    {
                        // 取第一张图片作为封面
                        IPicture coverPicture = pictures[0];

                        // 获取封面图片的字节数组
                        byte[] coverBytes = coverPicture.Data.Data;
                        return coverBytes;
                    }
                    else
                    {
                        return null;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"发生错误: {ex.Message}");
            }
            return null;
        }        

        public static async Task<BitmapImage> GetImageFromMusic(Music music, int size = 150)
        {
            try
            {
                using (var file = TagLib.File.Create(music.Path))
                {
                    if (file.Tag.Pictures.Length > 0)
                    {
                        IPicture picture = file.Tag.Pictures[0];
                        return await ReadBitmapImageAsync(picture, size,0);
                    }
                    else
                    {
                        var uri = new Uri("ms-appx:///Assets/Album.png");
                        var bitmapImage = new BitmapImage(uri);
                        return bitmapImage;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"封面读取失败: {ex.Message}");
                var uri = new Uri("ms-appx:///Assets/Album.png");
                var bitmapImage = new BitmapImage(uri);
                return bitmapImage;
            }
        }

        public static List<Music> UpdateFavouriteMusic(List<Music> musicList, Music music)
        {
            if (musicList != null && musicList.Count > 0)
            {
                var index = musicList.FindIndex(m => m.Id == music.Id);
                if (index != -1)
                {
                    musicList[index].IsFavorite = music.IsFavorite;
                }
            }
            return musicList;
        }

        public static List<Music> SortMusicList(string type, string sortOrder,  ObservableCollection<Music> musicList)
        {
            if (sortOrder == "A-Z")
            {
                return musicList.OrderBy(m => m.Title).ToList();
            }
            if (sortOrder == "Artist")
            {
                return musicList.OrderBy(m => m.Author).ToList();
            }
            if (sortOrder == "Album")
            {
                return musicList.GroupBy(m => m.Album)
                                .OrderBy(g => g.Key)
                                .SelectMany(g => g.OrderBy(m => m.TrackNumber))
                                .ToList();
            }
            switch (type)
            {
                case "song":
                    switch (sortOrder)
                    {
                        case "DefaultOrder":
                            return musicList.OrderBy(m => m.Title).ToList();
                        default:
                            return musicList.ToList();
                    }
                case "folderCover":
                    switch (sortOrder)
                    {
                        case "DefaultOrder":
                            return musicList.OrderBy(m => m.LastLevelFolderPath).ToList();
                        default:
                            return musicList.ToList();
                    }
                case "folder":
                    switch (sortOrder)
                    {
                        case "DefaultOrder":
                            return musicList.GroupBy(m => m.Album)
                                            .OrderBy(g => g.Key)
                                            .SelectMany(g => g.OrderBy(m => m.TrackNumber))
                                            .ToList();
                        default:
                            return musicList.ToList();
                    }
                case "artistCover":
                    switch (sortOrder)
                    {
                        case "DefaultOrder":
                            return musicList.OrderBy(m => m.Author).ToList();
                        default:
                            return musicList.ToList();
                    }
                case "artist":
                    switch (sortOrder)
                    {
                        case "DefaultOrder":
                            return musicList.OrderBy(m => m.Album).ToList();
                        default:
                            return musicList.ToList();
                    }
                case "albumCover":
                    switch (sortOrder)
                    {
                        case "DefaultOrder":
                            return musicList.OrderBy(m => m.Album).ToList();
                        default:
                            return musicList.ToList();
                    }
                case "album":
                    switch (sortOrder)
                    {
                        case "DefaultOrder":
                            return musicList.OrderBy(m => m.TrackNumber).ToList();
                        default:
                            return musicList.ToList();
                    }
                case "favour":
                    switch (sortOrder)
                    {
                        case "DefaultOrder":
                            return musicList.OrderByDescending(m => m.Order).ToList();
                        default:
                            return musicList.ToList();
                    }
                case "playList":
                    switch (sortOrder)
                    {
                        case "DefaultOrder":
                            return musicList.OrderByDescending(m => m.PlayListOrder).ToList();
                        default:
                            return musicList.ToList();
                    }
                default:
                    return musicList.ToList();
            }
        }

        public static AudioFileInfo GetAudioFileInfo(string filePath)
        {
            try
            {
                using (var reader = new MediaFoundationReader(filePath))
                {
                    int sampleRate = reader.WaveFormat.SampleRate;
                    int channelCount = reader.WaveFormat.Channels;
                    int bitDepth = reader.WaveFormat.BitsPerSample;
                    TimeSpan duration = reader.TotalTime;
                    int bitRate = (int)(reader.Length * 8 / duration.TotalSeconds / 1000);
                    return new AudioFileInfo
                    {
                        SampleRate = sampleRate,
                        ChannelCount = channelCount,
                        BitRate = bitRate,
                        BitDepth = bitDepth,
                        Duration = duration
                    };
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"读取 WAV 文件时出错: {ex.Message}");
                return new AudioFileInfo();
            }
        }

        public static bool IsMusicFile(string fileType)
        {
            var musicExtensions = new[] { ".mp3", ".wav", ".flac", ".wma", ".aac", ".ogg",".oga", ".aiff",".aif", ".m4a", ".dsf", ".dff", ".amr" ,".au",".ape",".opus",".wv"};
            return musicExtensions.Contains(fileType.ToLower());
        }

        public static List<Music> UpdateMusicInList(List<Music> musicList, Music newMusic)
        {
            for (int i = 0; i < musicList.Count; i++)
            {
                if (musicList[i].Id == newMusic.Id)
                {
                    musicList[i] = newMusic;
                }
            }
            return musicList;
        }

        public static async Task<BitmapImage> LoadAlbumCover(Music music)
        {
            if (AppData.albumCoverCache.TryGetValue(music.Album, out var cachedCover))
            {
                return cachedCover;
            }
            else
            {
                return await GetImageFromMusic(music);
            }
        }

        public static async Task<byte[]> ImageToByteArray(Microsoft.UI.Xaml.Controls.Image imageControl, double scaleFactor = 1)
        {
            byte[] buffer = null;
            if (imageControl.Source is BitmapImage bitmapImage)
            {
                // 使用 RenderTargetBitmap 捕获图像
                var renderTargetBitmap = new RenderTargetBitmap();
                await renderTargetBitmap.RenderAsync(imageControl, (int)(bitmapImage.PixelWidth / scaleFactor), (int)(bitmapImage.PixelHeight / scaleFactor));
                Debug.WriteLine($"bitmapImage长宽:{bitmapImage.PixelHeight} {bitmapImage.PixelWidth}");
                // 获取像素
                var pixelBuffer = await renderTargetBitmap.GetPixelsAsync();
                var pixels = pixelBuffer.ToArray();

                // 创建编码器并写入流
                var stream = new InMemoryRandomAccessStream();
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
                Debug.WriteLine($"renderTargetBitmap长宽:{(uint)renderTargetBitmap.PixelHeight} {(uint)renderTargetBitmap.PixelWidth}");
                // 设置像素数据
                encoder.SetPixelData(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    (uint)renderTargetBitmap.PixelWidth,
                    (uint)renderTargetBitmap.PixelHeight,
                    96.0, // DPI X
                    96.0, // DPI Y
                    pixels);

                // 刷新编码器
                await encoder.FlushAsync();

                // 读取流到字节数组
                stream.Seek(0);
                buffer = new byte[stream.Size];
                await stream.AsStream().ReadAsync(buffer, 0, buffer.Length);

            }
            return buffer;
        }

        // 将字典转换为JSON字符串的方法
        public static string ConvertToJson(Dictionary<string, double> dict)
        {
            if (dict == null)
            {
                throw new ArgumentNullException(nameof(dict), "字典不能为空");
            }

            try
            {
                // 使用System.Text.Json进行序列化
                return JsonSerializer.Serialize(dict);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"序列化时出错: {ex.Message}");
                throw;
            }
        }

        // 将JSON字符串转回字典的方法
        public static Dictionary<string, double> ConvertToDictionary(string jsonString)
        {
            if (string.IsNullOrEmpty(jsonString))
            {
                return new Dictionary<string, double>
               {
                   {"32Hz", 0},
                   {"64Hz", 0},
                   {"125Hz", 0},
                   {"250Hz", 0},
                   {"500Hz", 0},
                   {"1kHz", 0},
                   {"2kHz", 0},
                   {"4kHz", 0},
                   {"8kHz", 0},
                   {"16kHz", 0}
               };
            }

            try
            {
                // 使用System.Text.Json进行反序列化  
                return JsonSerializer.Deserialize<Dictionary<string, double>>(jsonString);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"反序列化时出错: {ex.Message}");
                return new Dictionary<string, double>
               {
                   {"32Hz", 0},
                   {"64Hz", 0},
                   {"125Hz", 0},
                   {"250Hz", 0},
                   {"500Hz", 0},
                   {"1kHz", 0},
                   {"2kHz", 0},
                   {"4kHz", 0},
                   {"8kHz", 0},
                   {"16kHz", 0}
               };
            }
            
        }

        public static string GetPlayModeText(PlayMode playMode) {
            switch (playMode)
            {
                case PlayMode.SingleLoop:
                    return GetString("IconSingleTuneCirculation");
                case PlayMode.ListLoop:
                    return GetString("IconListLoop");
                case PlayMode.RandomLoop:
                    return GetString("IconRandomLoop");
                case PlayMode.RepeatOff:
                    return GetString("IconSinglePlayback");
                default:
                    return GetString("IconListLoop");
            }
        }

        public static void DeleteFileFromDisk(string path) {
            try
            {
                if (System.IO.File.Exists(path))
                {
                    // 删除文件到回收站
                    FileSystem.DeleteFile(
                        path,
                        UIOption.OnlyErrorDialogs,
                        RecycleOption.SendToRecycleBin
                    );
                    Console.WriteLine("文件已移动到回收站！");
                }
                else
                {
                    Console.WriteLine("文件不存在，无法删除。");
                }
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("操作已被用户取消。");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"错误：{ex.Message}");
            }
        }
    }
}
