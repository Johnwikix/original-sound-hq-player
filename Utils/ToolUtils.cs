using ABI.Microsoft.UI.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.International.Converters.PinYinConverter;
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
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TagLib;
using Windows.Devices.Enumeration;
using Windows.Graphics.Imaging;
using Windows.Media.Devices;
using Windows.Storage;
using Windows.Storage.Streams;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.OnlineAPIs.CloudMusicAPI;
using WinUIMusicPlayer.Reader;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.ViewModel;
using WinUIMusicPlayer.WebService;
using DependencyObject = Microsoft.UI.Xaml.DependencyObject;
using Window = Microsoft.UI.Xaml.Window;

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

        //private static readonly StringComparer StringComparer = StringComparer.OrdinalIgnoreCase;
        private static readonly StringComparer StringComparer =
       StringComparer.CurrentCultureIgnoreCase;

        // 预定义的排序策略字典，避免字符串比较
        private static readonly Dictionary<string, Func<IEnumerable<Music>, IEnumerable<Music>>> SortStrategies =
            new Dictionary<string, Func<IEnumerable<Music>, IEnumerable<Music>>>(StringComparer)
            {
                ["A-Z"] = musicList => musicList.OrderBy(m => m.Title, StringComparer),
                ["Artist"] = musicList => musicList.OrderBy(m => m.Author, StringComparer),
                ["Album"] = musicList => musicList.GroupBy(m => m.Album)
                                                  .OrderBy(g => g.Key, StringComparer)
                                                  .SelectMany(g => g
                                                        .OrderBy(m => m.DiskNumber) 
                                                        .ThenBy(m => m.TrackNumber)      
                                                   )
            };

        // 预定义的类型默认排序策略
        private static readonly Dictionary<string, Func<IEnumerable<Music>, IEnumerable<Music>>> TypeDefaultSortStrategies =
            new Dictionary<string, Func<IEnumerable<Music>, IEnumerable<Music>>>(StringComparer)
            {
                ["song"] = musicList => musicList.OrderBy(m => m.Title, StringComparer),
                ["folderCover"] = musicList => musicList.OrderBy(m => m.LastLevelFolderPath, StringComparer),
                ["folder"] = musicList => musicList.GroupBy(m => m.Album)
                                                   .OrderBy(g => g.Key, StringComparer)
                                                   .SelectMany(g => g
                                                        .OrderBy(m => m.DiskNumber)
                                                        .ThenBy(m => m.TrackNumber)
                                                   ),
                ["artistCover"] = musicList => musicList.OrderBy(m => m.Author, StringComparer),
                ["artist"] = musicList => musicList.OrderBy(m => m.Album, StringComparer)
                                                    .ThenBy(m => m.DiskNumber)
                                                    .ThenBy(m => m.TrackNumber),
                ["albumCover"] = musicList => musicList.OrderBy(m => m.Album, StringComparer),
                ["album"] = musicList => musicList.OrderBy(m => m.DiskNumber)
                                            .ThenBy(m => m.TrackNumber),
                ["favour"] = musicList => musicList.OrderByDescending(m => m.Order),
                ["playList"] = musicList => musicList.OrderByDescending(m => m.PlayListOrder)
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

        public static void RefreshIcon(ObservableCollection<Music> musicList, string type = "album")
        {
            foreach (var item in musicList)
            {
                App.MainWindow.DispatcherQueue.TryEnqueue(() => {
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
                });
            }
        }

        public static async Task<BitmapImage> GetAlbumCover(Music album,int coverSize = 150)
        {
            BitmapImage newCover = album.Cover;
            var musics = MusicDatabaseService.GetAlbumMusicFromMem(album.Album);
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


        public static async Task<BitmapImage> ReadBitmapImageAsync(IPicture picture, int maxSize = 0)
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

        

        public static IEnumerable<Music> SortMusicList(string type, string sortOrder, IEnumerable<Music> musicList)
        {
            if (musicList == null) return Enumerable.Empty<Music>();

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
            if (musicList == null || musicList.Count <= 1) return;

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

        public static AudioFileInfo GetAudioFileInfo(string filePath)
        {
            try
            {
                using (var reader = new MediaFoundationReader(filePath))
                {
                    return new AudioFileInfo
                    {
                        SampleRate = reader.WaveFormat.SampleRate,
                        ChannelCount = reader.WaveFormat.Channels,
                        BitRate = (int)(reader.Length * 8 / reader.TotalTime.TotalSeconds / 1000),
                        BitDepth = reader.WaveFormat.BitsPerSample,
                        Duration = reader.TotalTime
                    };
                }
            }
            catch (Exception ex)
            {
                try {
                    using (var reader = new FFmpegAudioReader(filePath))
                    {
                        return new AudioFileInfo
                        {
                            SampleRate = reader.WaveFormat.SampleRate,
                            ChannelCount = reader.WaveFormat.Channels,
                            BitRate = (int)(reader.Length * 8 / reader.TotalTime.TotalSeconds / 1000),
                            BitDepth = reader.WaveFormat.BitsPerSample,
                            Duration = reader.TotalTime
                        };
                    }
                }
                catch (Exception ex2)
                {
                    Debug.WriteLine($"获取音频文件信息失败: {ex.Message} | {ex2.Message}");
                    return new AudioFileInfo();
                }                
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
                }
                else
                {
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
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

                    if (pinyinCollection != null && pinyinCollection.Count > 0)
                    {
                        // 获取第一个拼音，去掉声调数字，取首字母
                        string firstPinyin = pinyinCollection[0];
                        if (!string.IsNullOrEmpty(firstPinyin))
                        {
                            // 去掉声调数字和其他符号，只保留字母
                            string cleanPinyin = new string(firstPinyin.Where(char.IsLetter).ToArray());
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

        public static void OpenFileInExplorer(string filePath) {
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
            var usbMusicGroups = AppData.musicOnUsbDevice
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

        public static void AlbumPageLoadCoverAsync(List<MusicGroup> groupedByFirstLetter) {
            _ = Task.Run(async () =>
            {
                var semaphore = new SemaphoreSlim(AppSettings.CoverLoadThreadCount, Environment.ProcessorCount);
                var allMusicItems = groupedByFirstLetter.SelectMany(group => group).ToList();
                var visibleTasks = allMusicItems.Select(music => Task.Run(async () =>
                {
                    await semaphore.WaitAsync();
                    try
                    {
                        if (AppData.albumCoverCache.TryGetValue(music.Album, out var cachedCover))
                        {
                            App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                            {
                                music.Cover = cachedCover;
                            });
                        }
                        else
                        {
                            BitmapImage cover = await ToolUtils.GetAlbumCover(music, AppSettings.CoverSize);
                            App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                            {
                                music.Cover = cover;
                            });
                            if (AppSettings.isCoverCacheEnabled && cover != null)
                            {
                                AppData.albumCoverCache.SetValue(music.Album, cover);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"加载专辑封面失败: {ex.Message}");
                    }
                    finally
                    {
                        semaphore.Release(); // 释放信号量
                    }
                })).ToArray();
                try
                {
                    await Task.WhenAll(visibleTasks);
                }
                finally
                {
                    semaphore.Dispose();
                }
            });
        }

        public static async Task<string> GetLyricsFromNet(Music musicDetail)
        {
            string lyrics = string.Empty;
            if (string.IsNullOrEmpty(AppSettings.LrcAPISource) || AppSettings.LrcAPISource == "https://api.lrc.cx")
            {
                lyrics = await CloudMusicSearchHelper.GetSongLyrics(musicDetail.Title, musicDetail.Album, musicDetail.Author);
            }
            else
            {
                lyrics = await LrcService.GetLyricsAsync(musicDetail.Title, musicDetail.Album, musicDetail.Author);
            }
            return lyrics;
        }

        public static string GetLyricsFromFile(string filePath)
        {
            try
            {
                using (TagLib.File audioFile = TagLib.File.Create(filePath))
                {
                    Tag tag = audioFile.Tag;
                    return tag.Lyrics;
                }
            }
            catch (Exception ex)
            {
                return null;
            }
        }

    }
}
