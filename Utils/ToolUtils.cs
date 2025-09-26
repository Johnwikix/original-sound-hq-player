using ATL;
using ManagedBass;
using ManagedBass.Dsd;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.International.Converters.PinYinConverter;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.VisualBasic.FileIO;
using Microsoft.Windows.ApplicationModel.Resources;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TagLib;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using WinUIMusicPlayer.Manager;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Parser;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.ViewModel;
using WinUIMusicPlayer.WebService;
using ZLinq;
using DependencyObject = Microsoft.UI.Xaml.DependencyObject;
using Path = System.IO.Path;

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

        public static readonly Dictionary<string, int> FrequencyIndexMap = new Dictionary<string, int>
        {
            ["32Hz"] = 0,
            ["64Hz"] = 1,
            ["125Hz"] = 2,
            ["250Hz"] = 3,
            ["500Hz"] = 4,
            ["1kHz"] = 5,
            ["2kHz"] = 6,
            ["4kHz"] = 7,
            ["8kHz"] = 8,
            ["16kHz"] = 9
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

        //private static readonly StringComparer StringComparer = StringComparer.OrdinalIgnoreCase;
        private static readonly StringComparer StringComparer = StringComparer.CurrentCultureIgnoreCase;

        // 预定义的排序策略字典，避免字符串比较
        private static readonly Dictionary<string, Func<IEnumerable<Music>, IEnumerable<Music>>> SortStrategies =
            new Dictionary<string, Func<IEnumerable<Music>, IEnumerable<Music>>>(StringComparer)
            {
                ["A-Z"] = musicList => musicList.AsValueEnumerable().OrderBy(m => m.Title, StringComparer).ToImmutableList(),
                ["Artist"] = musicList => musicList.AsValueEnumerable().OrderBy(m => m.Author, StringComparer).ToImmutableList(),
                ["Album"] = musicList => musicList.AsValueEnumerable().GroupBy(m => m.Album)
                                                  .OrderBy(g => g.Key, StringComparer)
                                                  .SelectMany(g => g
                                                        .OrderBy(m => m.DiskNumber)
                                                        .ThenBy(m => m.TrackNumber)
                                                   ).ToImmutableList(),
                ["CreateTimeASC"] = musicList => musicList.AsValueEnumerable().OrderBy(m => m.CreateTime).ToImmutableList(),
                ["CreateTimeDESC"] = musicList => musicList.AsValueEnumerable().OrderByDescending(m => m.CreateTime).ToImmutableList(),
                ["UpdateTimeASC"] = musicList => musicList.AsValueEnumerable().OrderBy(m => m.UpdateTime).ToImmutableList(),
                ["UpdateTimeDESC"] = musicList => musicList.AsValueEnumerable().OrderByDescending(m => m.UpdateTime).ToImmutableList()
            };

        // 预定义的类型默认排序策略
        private static readonly Dictionary<string, Func<IEnumerable<Music>, IEnumerable<Music>>> TypeDefaultSortStrategies =
            new Dictionary<string, Func<IEnumerable<Music>, IEnumerable<Music>>>(StringComparer)
            {
                ["song"] = musicList => musicList.AsValueEnumerable().OrderBy(m => m.Title, StringComparer).ToImmutableList(),
                ["folderCover"] = musicList => musicList.AsValueEnumerable().OrderBy(m => m.LastLevelFolderPath, StringComparer).ToImmutableList(),
                ["folder"] = musicList => musicList.AsValueEnumerable().GroupBy(m => m.Album)
                                                   .OrderBy(g => g.Key, StringComparer)
                                                   .SelectMany(g => g
                                                        .OrderBy(m => m.DiskNumber)
                                                        .ThenBy(m => m.TrackNumber)
                                                   ).ToImmutableList(),
                ["artistCover"] = musicList => musicList.AsValueEnumerable().OrderBy(m => m.Author, StringComparer).ToImmutableList(),
                ["artist"] = musicList => musicList.AsValueEnumerable().OrderBy(m => m.Album, StringComparer)
                                                    .ThenBy(m => m.DiskNumber)
                                                    .ThenBy(m => m.TrackNumber).ToImmutableList(),
                ["albumCover"] = musicList => musicList.AsValueEnumerable().OrderBy(m => m.Album, StringComparer).ToImmutableList(),
                ["album"] = musicList => musicList.AsValueEnumerable().OrderBy(m => m.DiskNumber)
                                            .ThenBy(m => m.TrackNumber).ToImmutableList(),
                ["favour"] = musicList => musicList.AsValueEnumerable().OrderByDescending(m => m.Order).ToImmutableList(),
                ["playList"] = musicList => musicList.AsValueEnumerable().OrderByDescending(m => m.PlayListOrder).ToImmutableList()
            };

        public static string GetString(string key)
        {
            try
            {
                return new ResourceLoader().GetString(key);
            }
            catch (Exception ex)
            {
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

        //public static async Task RefreshDevice()
        //{
        //    try
        //    {
        //        string selector = MediaDevice.GetAudioRenderSelector();
        //        DeviceInformationCollection devices = await DeviceInformation.FindAllAsync(selector);
        //        AppSettings.outputDeviceList.Clear();
        //        foreach (DeviceInformation device in devices)
        //        {
        //            AppSettings.outputDeviceList.Add(device.Name);
        //        }
        //    }
        //    catch (Exception ex)
        //    {
        //        System.Diagnostics.Debug.WriteLine($"刷新音频设备失败: {ex.Message}");
        //    }
        //}

        //public static Microsoft.UI.Windowing.AppWindow GetAppWindowForCurrentWindow(Window window)
        //{
        //    var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        //    var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        //    return Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
        //}

        public static T FindParent<T>(DependencyObject child) where T : DependencyObject
        {
            DependencyObject parent = VisualTreeHelper.GetParent(child);
            if (parent is null)
                return null;
            T parentAsT = parent as T;
            return parentAsT ?? FindParent<T>(parent);
        }

        public static void RefreshIcon(ObservableCollection<Music> musicList, string type = "album")
        {
            foreach (var item in musicList)
            {
                App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                {
                    if (type == "album")
                    {
                        if (AppData.musicOnUsbDevice.AsValueEnumerable().Any(usbMusic => usbMusic.Album == item.Album))
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
                        if (AppData.musicOnUsbDevice.AsValueEnumerable().Any(usbMusic => usbMusic.Author == item.Author))
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
                        var allSongList = AppData.allSongs.AsValueEnumerable().Where(m => m.LastLevelFolderPath == item.LastLevelFolderPath);
                        item.IsExistOnDevice = 0;
                        foreach (var songs in allSongList)
                        {
                            if (AppData.musicOnUsbDevice.AsValueEnumerable().Any(usbMusic => usbMusic.Title == item.Title))
                            {
                                item.IsExistOnDevice = 1;
                                break;
                            }
                        }
                    }
                });
            }
        }

        //public static async Task<BitmapImage> GetAlbumCover(Music album, int coverSize = 150)
        //{
        //    BitmapImage newCover = album.Cover;
        //    var musics = MusicDatabaseService.GetAlbumMusicFromMem(album.Album);
        //    if (album.Album != "未知专辑")
        //    {
        //        if (musics is null || musics.Count() == 0)
        //        {
        //            return null;
        //        }
        //        foreach (var song in musics)
        //        {
        //            try
        //            {
        //                using (var file = TagLib.File.Create(song.Path))
        //                {
        //                    if (file.Tag.Pictures.Length > 0)
        //                    {
        //                        var picture = file.Tag.Pictures[0];
        //                        newCover = await ReadBitmapImageAsync(picture, coverSize);
        //                    }
        //                    else
        //                    {
        //                        newCover = null;
        //                    }
        //                    return newCover;
        //                }
        //            }
        //            catch (Exception ex)
        //            {
        //                //newCover = await DefaultAlbumCover();
        //                Debug.WriteLine($"读取专辑 {album.Album} 封面失败: {ex.Message}");
        //                return null;
        //            }
        //        }
        //    }
        //    else
        //    {
        //        //newCover = await DefaultAlbumCover();
        //        return null;
        //    }
        //    return newCover;
        //}

        public static async Task<BitmapImage> ReadBitmapImageAsync(IPicture picture, int maxSize = 0)
        {
            byte[] imageData = picture.Data.Data.ToArray();
            if (picture?.Data?.Data is null)
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
                        var bitmapImage = new BitmapImage { DecodePixelWidth = maxSize };
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
            if (data is null || data.Length < 10) return false;

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
                    if (foundChild is not null)
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
                if (imageData is null || imageData.Length == 0)
                    return null;
                using (var stream = new InMemoryRandomAccessStream())
                {
                    using (var dataWriter = new DataWriter(stream.GetOutputStreamAt(0)))
                    {
                        dataWriter.WriteBytes(imageData);
                        await dataWriter.StoreAsync();
                    }
                    var bitmapImage = maxSize == 0 ? new BitmapImage() : new BitmapImage { DecodePixelWidth = maxSize };
                    await bitmapImage.SetSourceAsync(stream);
                    return bitmapImage;
                }
            }
            catch (Exception ex)
            {
                return null;
            }
        }

        public static async Task<byte[]> GetRawImage(Music music, bool isManual = false)
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
                        if (AppSettings.isAutoLyricsEnabled && !isManual)
                        {
                            var cancellationToken = new CancellationTokenSource();
                            if (!Directory.Exists(AppSettings.MusicCoverCache))
                            {
                                Directory.CreateDirectory(AppSettings.MusicCoverCache);
                            }
                            byte[] picture = null;
                            string fileName = $"{music.Title}_{music.Album}_{music.Author}";
                            string invalidChars = new string(System.IO.Path.GetInvalidFileNameChars()) + new string(System.IO.Path.GetInvalidPathChars());
                            fileName = Regex.Replace(fileName, $"[{Regex.Escape(invalidChars)}]", "_");
                            string filePath = System.IO.Path.Combine(AppSettings.MusicCoverCache, fileName + ".png");
                            if (System.IO.File.Exists(filePath))
                            {
                                picture = System.IO.File.ReadAllBytes(filePath);
                            }
                            picture ??= await LrcService.GetCoverImageAsync(music.Title, music.Album, music.Author, cancellationToken.Token);
                            if (picture is not null)
                            {
                                System.IO.File.WriteAllBytes(filePath, picture);
                            }
                            return picture;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"发生错误: {ex.Message}");
            }
            return null;
        }

        //public static List<Music> UpdateFavouriteMusic(List<Music> musicList, Music music)
        //{
        //    if (musicList is not null && musicList.Count > 0)
        //    {
        //        var index = musicList.FindIndex(m => m.Id == music.Id);
        //        if (index != -1)
        //        {
        //            musicList[index].IsFavorite = music.IsFavorite;
        //        }
        //    }
        //    return musicList;
        //}



        public static IEnumerable<Music> SortMusicList(string type, string sortOrder, IEnumerable<Music> musicList)
        {
            if (musicList is null) return Enumerable.Empty<Music>();

            // 如果没有数据，直接返回空集合，避免不必要的计算
            if (!musicList.Any()) return musicList;

            // 优先检查通用排序策略
            if (!string.IsNullOrEmpty(sortOrder) && SortStrategies.TryGetValue(sortOrder, out var sortFunc))
            {
                return sortFunc(musicList);
            }

            // 检查类型默认排序，只有当sortOrder为DefaultOrder或空时才使用
            if ((string.IsNullOrEmpty(sortOrder) || sortOrder == "DefaultOrder") &&
                !string.IsNullOrEmpty(type) &&
                TypeDefaultSortStrategies.TryGetValue(type, out var typeFunc))
            {
                return typeFunc(musicList);
            }

            // 如果没有匹配的排序策略，返回原列表
            return musicList;
        }

        /// <summary>
        /// 就地排序ObservableCollection，避免创建新对象
        /// </summary>
        /// <param name="type">排序类型</param>
        /// <param name="sortOrder">排序方式</param>
        /// <param name="musicList">要排序的ObservableCollection</param>
        public static void SortMusicListInPlace(string type, string sortOrder, ObservableCollection<Music> musicList)
        {
            if (musicList is null || musicList.Count <= 1) return;

            var sortedList = SortMusicList(type, sortOrder, musicList).ToList();

            // 只有当排序结果与原列表不同时才进行更新
            if (!AreListsEqual(musicList, sortedList))
            {
                musicList.Clear();
                foreach (var music in sortedList)
                {
                    musicList.Add(music);
                }
            }
        }

        /// <summary>
        /// 比较两个列表是否相等（顺序相同）
        /// </summary>
        private static bool AreListsEqual(IList<Music> list1, IList<Music> list2)
        {
            if (list1.Count != list2.Count) return false;

            for (int i = 0; i < list1.Count; i++)
            {
                if (!ReferenceEquals(list1[i], list2[i]))
                    return false;
            }
            return true;
        }

        public static AudioFileInfo GetAudioInfo(string filePath)
        {
            BassManager.Initialize();
            int stream = 0;
            AudioFileInfo fileInfo = new();            
            try
            {
                if (Path.GetExtension(filePath) == ".dff")
                {
                    var dict = DffId3v2Parser.ReadId3v2TagsFromDff(filePath);
                    stream = BassDsd.CreateStream(filePath, 0, 0, BassFlags.DSDOverPCM | BassFlags.Float | BassFlags.Decode | BassFlags.AsyncFile);
                    Bass.ChannelGetInfo(stream, out ChannelInfo info);
                    Bass.ChannelGetAttribute(
                                        stream,
                                        ChannelAttribute.Bitrate,
                                        out var bitrate
                    );
                    double totalSeconds = Bass.ChannelBytes2Seconds(stream, Bass.ChannelGetLength(stream));
                    Bass.StreamFree(stream);
                    fileInfo.Title = dict?.TextTags["TIT2"] ?? Path.GetFileNameWithoutExtension(filePath);
                    fileInfo.Album = dict?.TextTags["TALB"] ?? "未知专辑";
                    fileInfo.Artist = dict?.TextTags["TPE1"] ?? "未知艺术家";
                    fileInfo.Year = int.TryParse(dict?.TextTags["TYER"], out int year) ? year : 0;
                    fileInfo.TrackNumber = int.TryParse(dict?.TextTags["TRCK"], out int track) ? track : 0;
                    fileInfo.BitDepth = 1;
                    fileInfo.BitRate = (int)bitrate;
                    fileInfo.SampleRate = info.Frequency * 16;
                    fileInfo.Duration = TimeSpan.FromSeconds(totalSeconds);
                }
                else {
                    Track track = new(filePath);
                    fileInfo.Title = string.IsNullOrEmpty(track?.Title) ? Path.GetFileNameWithoutExtension(filePath) : track?.Title;
                    fileInfo.Album = string.IsNullOrEmpty(track?.Album) ? "未知专辑" : track?.Album;
                    fileInfo.Artist = string.IsNullOrEmpty(track?.Artist) ? "未知艺术家" : track?.Artist;
                    fileInfo.Duration = TimeSpan.FromSeconds(track?.Duration ?? 0);
                    fileInfo.SampleRate = (int)track?.SampleRate;
                    fileInfo.ChannelCount = track?.ChannelsArrangement.NbChannels ?? 0;
                    fileInfo.BitDepth = track?.BitDepth ?? 0;
                    fileInfo.BitRate = track?.Bitrate ?? 0;
                    fileInfo.Year = track?.Year ?? 0;
                    fileInfo.Lyrics = track?.Lyrics?.Count() > 0 ? track?.Lyrics[0].UnsynchronizedLyrics : string.Empty;
                    fileInfo.TrackNumber = track?.TrackNumber ?? 0;
                    fileInfo.DiskNumber = track?.DiscNumber ?? 0;
                }               
                return fileInfo;
            }
            catch (Exception) {
                fileInfo.Title = Path.GetFileNameWithoutExtension(filePath);
                return fileInfo;
            }            
        }

        //public static AudioFileInfo GetAudioFileInfo(string filePath)
        //{
        //    try
        //    {
        //        using (var reader = new MediaFoundationReader(filePath))
        //        {
        //            return new AudioFileInfo
        //            {
        //                SampleRate = reader.WaveFormat.SampleRate,
        //                ChannelCount = reader.WaveFormat.Channels,
        //                BitRate = (int)(reader.Length * 8 / reader.TotalTime.TotalSeconds / 1000),
        //                BitDepth = reader.WaveFormat.BitsPerSample,
        //                Duration = reader.TotalTime
        //            };
        //        }
        //    }
        //    catch (Exception ex)
        //    {
        //        try
        //        {
        //            using (var reader = new FFmpegAudioReader(filePath))
        //            {
        //                return new AudioFileInfo
        //                {
        //                    SampleRate = reader.WaveFormat.SampleRate,
        //                    ChannelCount = reader.WaveFormat.Channels,
        //                    BitRate = (int)(reader.Length * 8 / reader.TotalTime.TotalSeconds / 1000),
        //                    BitDepth = reader.WaveFormat.BitsPerSample,
        //                    Duration = reader.TotalTime
        //                };
        //            }
        //        }
        //        catch (Exception ex2)
        //        {
        //            Debug.WriteLine($"获取音频文件信息失败: {ex.Message} | {ex2.Message}");
        //            return new AudioFileInfo();
        //        }
        //    }
        //}

        public static bool IsMusicFile(string fileType)
        {
            var musicExtensions = new[] { ".mp3", ".wav", ".flac", ".wma", ".aac", ".ogg", ".oga", ".aiff", ".aif", ".m4a", ".dsf", ".dff", ".ape", ".opus", ".wv" };
            return musicExtensions.Contains(fileType.ToLower());
        }

        //public static List<Music> UpdateMusicInList(List<Music> musicList, Music newMusic)
        //{
        //    for (int i = 0; i < musicList.Count; i++)
        //    {
        //        if (musicList[i].Id == newMusic.Id)
        //        {
        //            musicList[i] = newMusic;
        //        }
        //    }
        //    return musicList;
        //}

        //public static async Task<byte[]> ImageToByteArray(Microsoft.UI.Xaml.Controls.Image imageControl, double scaleFactor = 1)
        //{
        //    byte[] buffer = null;
        //    if (imageControl.Source is BitmapImage bitmapImage)
        //    {
        //        // 使用 RenderTargetBitmap 捕获图像
        //        var renderTargetBitmap = new RenderTargetBitmap();
        //        await renderTargetBitmap.RenderAsync(imageControl, (int)(bitmapImage.PixelWidth / scaleFactor), (int)(bitmapImage.PixelHeight / scaleFactor));
        //        Debug.WriteLine($"bitmapImage长宽:{bitmapImage.PixelHeight} {bitmapImage.PixelWidth}");
        //        // 获取像素
        //        var pixelBuffer = await renderTargetBitmap.GetPixelsAsync();
        //        var pixels = pixelBuffer.ToArray();
        //        // 创建编码器并写入流
        //        var stream = new InMemoryRandomAccessStream();
        //        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        //        // 设置像素数据
        //        encoder.SetPixelData(
        //            BitmapPixelFormat.Bgra8,
        //            BitmapAlphaMode.Premultiplied,
        //            (uint)renderTargetBitmap.PixelWidth,
        //            (uint)renderTargetBitmap.PixelHeight,
        //            96.0, // DPI X
        //            96.0, // DPI Y
        //            pixels);

        //        // 刷新编码器
        //        await encoder.FlushAsync();
        //        // 读取流到字节数组
        //        stream.Seek(0);
        //        buffer = new byte[stream.Size];
        //        await stream.AsStream().ReadAsync(buffer, 0, buffer.Length);

        //    }
        //    return buffer;
        //}

        // 将字典转换为JSON字符串的方法
        public static string ConvertToJson(Dictionary<string, double> dict)
        {
            if (dict is null)
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

        public static string GetPlayModeText(PlayMode playMode)
        {
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

        public static bool DeleteFileFromDisk(string path)
        {
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
                    return true;
                }
                else
                {
                    return true;
                }
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                return false;
            }
        }

        public static string GetFirstLetterAdvanced(string text)
        {
            if (string.IsNullOrEmpty(text))
                return "#";

            char firstChar = text.Trim()[0];

            // 处理英文字符
            if (firstChar >= 'A' && firstChar <= 'Z')
                return firstChar.ToString();
            if (firstChar >= 'a' && firstChar <= 'z')
                return firstChar.ToString().ToUpper();

            // 处理数字
            if (char.IsDigit(firstChar))
                return "#";

            // 处理中文字符
            if (ChineseChar.IsValidChar(firstChar))
            {
                try
                {
                    ChineseChar chineseChar = new ChineseChar(firstChar);
                    // 获取拼音集合
                    var pinyinCollection = chineseChar.Pinyins;

                    if (pinyinCollection is not null && pinyinCollection.Count > 0)
                    {
                        // 获取第一个拼音，去掉声调数字，取首字母
                        string firstPinyin = pinyinCollection[0];
                        if (!string.IsNullOrEmpty(firstPinyin))
                        {
                            // 去掉声调数字和其他符号，只保留字母
                            string cleanPinyin = new string(firstPinyin.AsValueEnumerable().Where(char.IsLetter).ToArray());
                            if (!string.IsNullOrEmpty(cleanPinyin))
                            {
                                return cleanPinyin[0].ToString().ToUpper();
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    // 如果转换失败，返回默认值
                    Console.WriteLine($"拼音转换失败: {ex.Message}");
                    return "中";
                }
            }

            // 其他字符（符号等）
            return "#";
        }

        public static string ConvertLyrics(string lyrics)
        {
            Regex timeRegex = new Regex(@"\[(\d{2}):(\d{2})\.(\d{2,3})\]");
            string[] lines = lyrics.Split(new string[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            for (int i = 0; i < lines.Length; i++)
            {
                Match timeMatch = timeRegex.Match(lines[i]);
                while (timeMatch.Success)
                {
                    string timePart = timeMatch.Value;
                    string minutes = timeMatch.Groups[1].Value;
                    string seconds = timeMatch.Groups[2].Value;
                    string milliseconds = timeMatch.Groups[3].Value;

                    if (milliseconds.Length == 3)
                    {
                        milliseconds = milliseconds.Substring(0, 2);
                    }

                    string newTimePart = $"[{minutes}:{seconds}.{milliseconds}]";
                    lines[i] = lines[i].Replace(timePart, newTimePart);
                    timeMatch = timeMatch.NextMatch();
                }
            }

            return string.Join("\r\n", lines);
        }

        public static string SanitizeFileName(string name, char[] invalidChars)
        {
            foreach (char c in invalidChars)
            {
                name = name.Replace(c, '_');
            }
            return name;
        }

        public static void OpenFileInExplorer(string filePath)
        {
            if (System.IO.File.Exists(filePath))
            {
                try
                {
                    Process.Start("explorer.exe", $"/select,\"{filePath}\"");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"打开资源管理器时出错: {ex.Message}");
                }
            }
            else
            {
                Debug.WriteLine($"文件不存在: {filePath}");
            }
        }

        public static void RefreshUsbDeviceMusicList(ObservableCollection<Music> MusicList)
        {
            var usbMusicGroups = AppData.musicOnUsbDevice.AsValueEnumerable()
                            .GroupBy(u => u.Title)
                            .ToDictionary(g => g.Key, g => g.ToList());
            foreach (var music in MusicList)
            {
                music.IsExistOnDevice = 0;

                if (usbMusicGroups.TryGetValue(music.Title, out var matchingItems))
                {
                    music.IsExistOnDevice = 1;
                    foreach (var usbMusic in matchingItems)
                    {
                        if (music.Author == usbMusic.Author &&
                            music.Album == usbMusic.Album &&
                            music.Extension == usbMusic.Extension)
                        {
                            music.IsExistOnDevice = 2;
                            break;
                        }
                    }
                }
            }
        }

        public static void ClearAllUsbStatus()
        {
            App.Services.GetRequiredService<SongCollectionViewModel>().ClearUsbDeviceMusicList(null, null);
            App.Services.GetRequiredService<SongListViewModel>().ClearUsbDeviceMusicList(null, null);
            App.Services.GetRequiredService<FavouritePlayListViewModel>().ClearUsbDeviceMusicList();
            App.Services.GetRequiredService<PlayListSongViewModel>().ClearUsbDeviceMusicList(null, null);
            App.Services.GetRequiredService<AlbumViewModel>().UpdateUsbIcon();
            App.Services.GetRequiredService<ArtistViewModel>().UpdateUsbIcon();
            App.Services.GetRequiredService<FolderViewModel>().UpdateUsbIcon();
        }

        public static void RefreshAllUsbStatus()
        {
            App.Services.GetRequiredService<SongCollectionViewModel>().RefreshUsbDeviceMusicList(null, null);
            App.Services.GetRequiredService<SongListViewModel>().RefreshUsbDeviceMusicList(null, null);
            App.Services.GetRequiredService<FavouritePlayListViewModel>().RefreshUsbDeviceMusicList();
            App.Services.GetRequiredService<PlayListSongViewModel>().RefreshUsbDeviceMusicList(null, null);
            App.Services.GetRequiredService<AlbumViewModel>().UpdateUsbIcon();
            App.Services.GetRequiredService<ArtistViewModel>().UpdateUsbIcon();
            App.Services.GetRequiredService<FolderViewModel>().UpdateUsbIcon();
        }

        public static async Task LoadImageAsync(string filePath, string album, BitmapImage bitmap, Music music)
        {
            await Task.Run(async () =>
            {
                try
                {
                    using (var file = TagLib.File.Create(filePath))
                    {
                        byte[] picture = file.Tag.Pictures.FirstOrDefault()?.Data.Data;
                        if (picture is null)
                        {
                            if (!Directory.Exists(AppSettings.MusicCoverCache))
                            {
                                Directory.CreateDirectory(AppSettings.MusicCoverCache);
                            }
                            string fileName = $"{music.Title}_{music.Album}_{music.Author}";
                            string invalidChars = new string(System.IO.Path.GetInvalidFileNameChars()) + new string(System.IO.Path.GetInvalidPathChars());
                            fileName = Regex.Replace(fileName, $"[{Regex.Escape(invalidChars)}]", "_");
                            string filePath = System.IO.Path.Combine(AppSettings.MusicCoverCache, fileName + ".png");
                            if (System.IO.File.Exists(filePath))
                            {
                                picture = System.IO.File.ReadAllBytes(filePath);
                            }
                            if (!AppData.UnknownAlbums.Contains(album))
                            {
                                if (AppSettings.isAutoLyricsEnabled)
                                {
                                    picture ??= await LrcService.GetCoverImageAsync(music.Title, music.Album, music.Author);
                                    if (picture is not null)
                                    {
                                        System.IO.File.WriteAllBytes(filePath, picture);
                                    }
                                }
                            }
                        }
                        if (picture is not null)
                        {
                            DecodePicture(picture, album, bitmap);
                        }
                    }
                }
                catch (Exception)
                {
                    if (music.Extension.ToLower() == "dff")
                    {
                        var res = DffId3v2Parser.ReadId3v2TagsFromDff(filePath);
                        byte[] picture = res?.Pictures.Count() > 0 ? res.Pictures[0].ImageData:null;
                        if (picture is not null)
                        {
                            DecodePicture(picture, album, bitmap);
                        }
                    }
                    else {
                        Track track = new(filePath);
                        PictureInfo pic = track.EmbeddedPictures.Count() > 0 ? track.EmbeddedPictures[0] : null;
                        if (pic is not null)
                        {
                            DecodePicture(pic.PictureData, album, bitmap);
                        }
                    }                    
                }
            });
        }

        private static async void DecodePicture(byte[] picture,string album,BitmapImage bitmap) {
            using (var originalStream = new MemoryStream(picture))
            {
                // 解码原始图像
                var decoder = await BitmapDecoder.CreateAsync(originalStream.AsRandomAccessStream());
                double aspectRatio = (double)decoder.PixelWidth / decoder.PixelHeight;
                uint newWidth = (uint)AppSettings.CoverSize;
                uint newHeight = (uint)(newWidth / aspectRatio);
                var resizedStream = new InMemoryRandomAccessStream();
                var encoder = await BitmapEncoder.CreateForTranscodingAsync(resizedStream, decoder);
                encoder.BitmapTransform.ScaledWidth = newWidth;
                encoder.BitmapTransform.ScaledHeight = newHeight;
                encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
                await encoder.FlushAsync();
                // 在UI线程中设置bitmap源
                App.MainWindow.DispatcherQueue.TryEnqueue(async () =>
                {
                    try
                    {
                        resizedStream.Seek(0);
                        await bitmap.SetSourceAsync(resizedStream);
                        if (!AppData.UnknownAlbums.Contains(album) && AppSettings.isCoverCacheEnabled)
                        {
                            AppData.albumCoverCache.TryAdd(album, bitmap);
                        }
                    }
                    catch (Exception)
                    {
                    }
                    finally
                    {
                        resizedStream.Dispose();
                    }
                });
            }
        }

        public static async Task<string> GetLyricsFromNet(Music musicDetail)
        {
            return await LrcService.GetLyricsAsync(musicDetail.Title, musicDetail.Album, musicDetail.Author);
        }

        public static DateTime GetSafeFileCreateTime(string filePath)
        {
            try
            {
                return System.IO.File.GetCreationTime(filePath);
            }
            catch (Exception ex)
            {
                return DateTime.Now;
            }
        }

        public static DateTime GetSafeFileUpdateTime(string filePath)
        {
            try
            {
                return System.IO.File.GetLastWriteTime(filePath);
            }
            catch (Exception ex)
            {
                return DateTime.Now;
            }
        }

        public static async Task<Music> GetMusicInfo(StorageFile file, string folderPath)
        {
            string lastLevelDirectory = Path.GetDirectoryName(file.Path);
            DirectoryInfo directoryInfo = new DirectoryInfo(lastLevelDirectory);
            string lastLevelFolderPath = directoryInfo.Name;
            try
            {
                using (TagLib.File audioFile = TagLib.File.Create(file.Path))
                {
                    Tag tag = audioFile.Tag;

                    string title = "未知标题";
                    string artist = "未知艺术家";
                    string album = "未知专辑";
                    int trackNumber = 0;
                    int diskNumber = 0;
                    int sampleRate = 0;
                    int bitDepth = 0;
                    int bitRate = 0;
                    int year = 0;
                    int channelCount = 0;
                    string lyrics = string.Empty;
                    TimeSpan duration = TimeSpan.Zero;
                    Properties audioProperties = audioFile.Properties;
                    trackNumber = (int)tag.Track;
                    diskNumber = (int)tag.Disc;
                    title = !string.IsNullOrWhiteSpace(tag.Title) ?
                       tag.Title : Path.GetFileNameWithoutExtension(file.Name);

                    string[] artists = audioFile.Tag.Performers;
                    if (artists.Length > 0)
                    {
                        artist = artists[0]; // 取第一个艺术家
                        Console.WriteLine("艺术家: " + string.Join(", ", artists));
                    }
                    if (!string.IsNullOrWhiteSpace(tag.Album))
                    {
                        album = tag.Album;
                    }
                    sampleRate = audioProperties.AudioSampleRate;
                    bitDepth = audioProperties.BitsPerSample == 0 ? await GetBitDepth(file) : audioProperties.BitsPerSample;
                    bitRate = audioProperties.AudioBitrate == 0 ? await GetBitRate(file) : audioProperties.AudioBitrate;
                    year = (int)tag.Year;
                    duration = audioProperties.Duration;
                    channelCount = audioProperties.AudioChannels;
                    lyrics = tag.Lyrics;
                    if (GarbledTextFixer.IsGbkToIso88591Garbled(title))
                    {
                        title = GarbledTextFixer.FixGbkToIso88591(title);
                    }
                    if (GarbledTextFixer.IsGbkToIso88591Garbled(artist))
                    {
                        artist = GarbledTextFixer.FixGbkToIso88591(artist);
                    }
                    if (GarbledTextFixer.IsGbkToIso88591Garbled(album))
                    {
                        album = GarbledTextFixer.FixGbkToIso88591(album);
                    }


                    var music = new Music
                    {
                        Path = file.Path,
                        Title = title,
                        Author = artist,
                        Album = album,
                        Duration = duration,
                        FolderPath = lastLevelDirectory,
                        Order = 0,
                        LastLevelFolderPath = lastLevelFolderPath,
                        Extension = file.FileType.TrimStart('.').ToUpper(),
                        BitDepth = bitDepth,
                        BitRate = bitRate,
                        SampleRate = sampleRate,
                        Channel = channelCount,
                        TrackNumber = trackNumber,
                        DiskNumber = diskNumber,
                        Year = year,
                        Lyrics = lyrics,
                        CreateTime = ToolUtils.GetSafeFileCreateTime(file.Path),
                        UpdateTime = ToolUtils.GetSafeFileUpdateTime(file.Path)
                    };
                    return music;
                }
            }
            catch (Exception ex)
            {
                AudioFileInfo wavFileInfo = ToolUtils.GetAudioInfo(file.Path);
                try
                {
                    var music = new Music
                    {
                        Path = file.Path,
                        Title = wavFileInfo.Title,
                        Author = wavFileInfo.Artist,
                        Duration = wavFileInfo.Duration,
                        Album = wavFileInfo.Album,
                        FolderPath = lastLevelDirectory,
                        Order = 0,
                        LastLevelFolderPath = lastLevelFolderPath,
                        Extension = file.FileType.TrimStart('.').ToUpper(),
                        BitDepth = wavFileInfo.BitDepth,
                        BitRate = wavFileInfo.BitRate,
                        SampleRate = wavFileInfo.SampleRate,
                        Year = wavFileInfo.Year,
                        Channel = wavFileInfo.ChannelCount,
                        TrackNumber = wavFileInfo.TrackNumber,
                        DiskNumber = wavFileInfo.DiskNumber,
                        Lyrics = wavFileInfo.Lyrics,
                        CreateTime = ToolUtils.GetSafeFileCreateTime(file.Path),
                        UpdateTime = ToolUtils.GetSafeFileUpdateTime(file.Path)
                    };
                    return music;
                }
                catch (Exception innerEx)
                {
                    System.Diagnostics.Debug.WriteLine($"创建基本音乐条目时出错: {file.Path}, 错误: {innerEx.Message}");                     // 返回null以指示错误
                }

            }
            return null;
        }

        private static async Task<int> GetBitDepth(StorageFile file)
        {
            var bitDepth = 16;
            try
            {
                // 其余代码保持不变...
                var audioProps = await file.Properties.RetrievePropertiesAsync(new string[] {
                                "System.Audio.SampleSize"
                             });
                // 处理位深度
                if (audioProps.ContainsKey("System.Audio.SampleSize") && audioProps["System.Audio.SampleSize"] is not null)
                {
                    var sampleSize = Convert.ToInt32(audioProps["System.Audio.SampleSize"]);
                    bitDepth = sampleSize;
                }
            }
            catch (Exception ex)
            {
                // 处理异常
                bitDepth = 16;
                System.Diagnostics.Debug.WriteLine($"获取音频属性时出错: {ex.Message}");
            }
            return bitDepth;
        }

        private static async Task<int> GetBitRate(StorageFile file)
        {
            var bitRate = 0;
            try
            {
                // 其余代码保持不变...
                var audioProps = await file.Properties.RetrievePropertiesAsync(new string[] {
                                "System.Audio.EncodingBitrate"
                             });
                if (audioProps.ContainsKey("System.Audio.EncodingBitrate") && audioProps["System.Audio.EncodingBitrate"] is not null)
                {
                    int rawBitrate = Convert.ToInt32(audioProps["System.Audio.EncodingBitrate"]);
                    bitRate = rawBitrate > 0 ? rawBitrate / 1000 : 0;
                }
            }
            catch (Exception ex)
            {
                // 处理异常
                bitRate = 0;
                System.Diagnostics.Debug.WriteLine($"获取音频属性时出错: {ex.Message}");
            }
            return bitRate;
        }

        public async static Task<PlayList> OpenM3u8File()
        {
            var picker = new FileOpenPicker();
            // 设置文件选择器的视图
            picker.ViewMode = PickerViewMode.List;
            picker.SuggestedStartLocation = PickerLocationId.MusicLibrary;

            // 添加m3u8文件筛选器
            picker.FileTypeFilter.Add(".m3u8");

            // 在WinUI3中需要设置文件选择器的窗口句柄
            WinRT.Interop.InitializeWithWindow.Initialize(picker, AppData.m_hWnd);

            // 显示文件选择器并获取结果
            var file = await picker.PickSingleFileAsync();
            if (file is not null)
            {
                try
                {
                    PlayList playList = await MusicDatabaseService.GetPlayListByName(Path.GetFileNameWithoutExtension(file.Name));
                    int playListId = 0;
                    if (playList is not null)
                    {
                        playListId = playList.Id;
                    }
                    else
                    {
                        playList = new() { Name = Path.GetFileNameWithoutExtension(file.Name) };
                        playListId = await MusicDatabaseService.InsertPlayList(playList);
                    }

                    string fileContent = await FileIO.ReadTextAsync(file);
                    if (!string.IsNullOrEmpty(fileContent))
                    {
                        //Debug.WriteLine(fileContent);
                        ParseM3u8Content(fileContent, playListId);
                    }
                    return playList;
                }
                catch (Exception ex)
                {
                    return null;
                }
            }
            return null;
        }

        public async static void ParseM3u8Content(string fileContent, int playListId)
        {
            if (string.IsNullOrEmpty(fileContent))
                return;
            // 按行分割内容
            var lines = fileContent.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            // 验证是否是有效的m3u8文件
            if (lines.Length == 0 || !lines[0].Equals("#EXTM3U", StringComparison.OrdinalIgnoreCase))
            {
                throw new FormatException("无效的m3u8文件格式，必须以#EXTM3U开头");
            }
            // 从第二行开始解析
            for (int i = lines.Length - 1; i >= 1; i--)
            {
                var line = lines[i].Trim();
                // 处理歌曲路径行（非注释行）
                if (!line.StartsWith("#"))
                {
                    Debug.WriteLine(Path.GetFileName(line));
                    Music? music = AppData.allSongs.FirstOrDefault(m => m.Path.Contains(Path.GetFileName(line)));
                    if (music is not null)
                    {
                        await MusicDatabaseService.AddMusicToPlayList(playListId, music.Id);
                    }
                }
            }
        }

        public static string GenerateM3U8Content(IEnumerable<Music> musics, string playlistName)
        {
            var sb = new StringBuilder();
            sb.AppendLine("#EXTM3U");
            sb.AppendLine($"# Playlist: {playlistName}");
            sb.AppendLine($"# Created: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            foreach (var music in musics)
            {
                sb.AppendLine($"#EXTINF:{music.Author} - {music.Title}");
                sb.AppendLine(music.Path);
            }
            return sb.ToString();
        }

        public static string GetCleanFontName(string fontSource)
        {
            if (string.IsNullOrEmpty(fontSource)) return fontSource;
            var index = fontSource.IndexOf(',');
            return index > 0 ? fontSource.Substring(0, index).Trim() : fontSource.Trim();
        }
        public static List<FontInfo> GetSystemFontsInternal()
        {
            try
            {
                var language = new string[] { CultureInfo.CurrentUICulture.Name.ToLowerInvariant() };
                var names = CanvasTextFormat.GetSystemFontFamilies();
                var displayNames = CanvasTextFormat.GetSystemFontFamilies(language);
                var list = new List<FontInfo>();
                for (var i = 0; i < names.Length; i++)
                {
                    try
                    {
                        list.Add(
                            new FontInfo
                            {
                                Name = names[i],
                                DisplayName = displayNames[i],
                                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily(names[i]),
                            }
                        );
                    }
                    catch (Exception)
                    {
                    }
                }
                return [.. list.OrderBy(f => f.Name)];
            }
            catch (Exception)
            {
                return new List<FontInfo>();
            }
        }

        public static TextAlignment ConvertStringToTextAlignment(string alignment)
        {
            return Enum.TryParse(alignment, true, out TextAlignment result) ? result : TextAlignment.Left;
        }
        public static string ConvertTextAlignmentToString(TextAlignment alignment)
        {
            return alignment.ToString();
        }

        public static async void ExportPlayList(PlayList playList)
        {
            try
            {
                var savePicker = new Windows.Storage.Pickers.FileSavePicker();
                WinRT.Interop.InitializeWithWindow.Initialize(savePicker, AppData.m_hWnd);
                savePicker.FileTypeChoices.Add("M3U8 播放列表", new List<string>() { ".m3u8" });
                savePicker.SuggestedFileName = playList.Name;
                var file = await savePicker.PickSaveFileAsync();
                if (file is not null)
                {
                    IEnumerable<Music> musics = MusicDatabaseService.GetMusicByPlayListIdFromMem(playList.Id);
                    var m3u8Content = ToolUtils.GenerateM3U8Content(musics, playList.Name);
                    await Windows.Storage.FileIO.WriteTextAsync(file, m3u8Content, Windows.Storage.Streams.UnicodeEncoding.Utf8);
                }
            }
            catch (Exception)
            {
            }
        }
    }
}
