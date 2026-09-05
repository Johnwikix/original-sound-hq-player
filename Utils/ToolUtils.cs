using ATL;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.International.Converters.PinYinConverter;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.VisualBasic.FileIO;
using Microsoft.Win32;
using Microsoft.Windows.ApplicationModel.Resources;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Hashing;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Streams;
using WinUIMusicPlayer.Behaviors;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Reader;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.ViewModel;
using WinUIMusicPlayer.WebService;
using ZLinq;
using DependencyObject = Microsoft.UI.Xaml.DependencyObject;
using Path = System.IO.Path;

namespace WinUIMusicPlayer.Utils
{
    public partial class ToolUtils
    {
        private static ILogger<ToolUtils> _logger = App.GetLogger<ToolUtils>();

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

        private static readonly HashSet<string> FastReadExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            "mp3", "flac", "m4a", "wav","ogg","opus","oga"
        };
        private static readonly Regex InvalidFileNameCharsRegex = new(
            "[" + Regex.Escape(new string(Path.GetInvalidFileNameChars()) + new string(Path.GetInvalidPathChars())) + "]",
            RegexOptions.Compiled);
        private static readonly ResourceLoader _resourceLoader = new();
        public static string GetString(string key)
        {
            try
            {
                return _resourceLoader.GetString(key);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"GetString 获取资源字符串失败: {ex.Message}");
                return key;
            }
        }

        /// <summary>有损格式的码率档位（kbps）。</summary>
        public static readonly int[] ConvertBitrates = [320, 256, 192, 160, 128, 96];

        /// <summary>
        /// 「转换为」右键菜单的格式子项：无损格式直接转换；有损格式带二级码率子菜单。
        /// <paramref name="tagFactory"/> 决定叶子 Tag：默认 "格式" / "格式:码率"（由
        /// MusicBrowseViewModel 解析）；USB 发送等调用方传入自己的包装（如 UsbSendTarget）。
        /// 新增格式时在 AudioConverterService.FormatExtensionMap 与 FFmpegAudioConverter.SelectEncoder 同步支持。
        /// 注意 resw 的 key 不能带 ".Text" 后缀——那是 x:Uid 专用编目，运行时 ResourceLoader 取不到。
        /// </summary>
        public static ObservableCollection<MenuModel> BuildConvertMenuChildren(
            System.Windows.Input.ICommand command, Func<string, int, object>? tagFactory = null)
        {
            object DefaultTag(string format, int bitrate) => bitrate == 320 ? format : $"{format}:{bitrate}";
            var makeTag = tagFactory ?? DefaultTag;

            ObservableCollection<MenuModel> LossyChildren(string format)
            {
                ObservableCollection<MenuModel> children = [];
                foreach (int bitrate in ConvertBitrates)
                {
                    children.Add(new() { Title = $"{bitrate} kbps", Tag = makeTag(format, bitrate), Command = command });
                }
                return children;
            }

            return
            [
                new() { Title = GetString("FlyoutConvertWav"), Tag = makeTag("wav", 320), Command = command },
                new() { Title = GetString("FlyoutConvertFlac"), Tag = makeTag("flac", 320), Command = command },
                new() { Title = GetString("FlyoutConvertAlac"), Tag = makeTag("alac", 320), Command = command },
                new() { Title = GetString("FlyoutConvertMp3"), Tag = makeTag("mp3", 320), Children = LossyChildren("mp3") },
                new() { Title = GetString("FlyoutConvertAac"), Tag = makeTag("aac", 320), Children = LossyChildren("aac") },
                new() { Title = GetString("FlyoutConvertOgg"), Tag = makeTag("ogg", 320), Children = LossyChildren("ogg") },
                new() { Title = GetString("FlyoutConvertOpus"), Tag = makeTag("opus", 320), Children = LossyChildren("opus") },
                new() { Title = GetString("FlyoutConvertWma"), Tag = makeTag("wma", 320), Children = LossyChildren("wma") },
            ];
        }

        /// <summary>
        /// 同步「发送到 USB 设备」菜单项到指定菜单集合：无设备时移除，有设备时确保存在
        /// 并整体替换子树。七处列表页 ViewModel 的 UpDateUsbDeviceMenuflyout 统一走这里。
        /// </summary>
        public static void UpdateUsbSendMenu(ObservableCollection<MenuModel> options, System.Windows.Input.ICommand command)
        {
            var usbFlyout = options.AsValueEnumerable().FirstOrDefault(m => (string)m.Tag == "SendToUsbDevice");
            var devices = App.Services.GetRequiredService<UsbDeviceService>().Devices;
            if (devices.Count == 0)
            {
                if (usbFlyout is not null) options.Remove(usbFlyout);
                return;
            }
            if (usbFlyout is null)
            {
                usbFlyout = new MenuModel { Title = GetString("SendToUsbDevice"), Tag = "SendToUsbDevice", Children = [] };
                options.Add(usbFlyout);
            }
            usbFlyout.Children = BuildUsbSendMenuChildren(devices, command);
        }

        /// <summary>
        /// 「发送到 USB 设备」菜单子项：每个设备一层子菜单，首项为原格式直传，
        /// 其后直接跟转换格式（有损格式再带码率层）——一层到位，避免设备/格式双重嵌套。
        /// </summary>
        public static ObservableCollection<MenuModel> BuildUsbSendMenuChildren(
            IReadOnlyList<UsbStorageDevice> devices, System.Windows.Input.ICommand command)
        {
            ObservableCollection<MenuModel> children = [];
            foreach (var usb in devices)
            {
                var deviceItem = new MenuModel
                {
                    Title = $"{usb.Name} , {GetString("Path")}：{usb.Path} , {GetString("FreeSpace")}：{usb.FreeSpaceInGB}GB",
                    Children =
                    [
                        new()
                        {
                            Title = GetString("SendOriginalFormat"),
                            Tag = new UsbSendTarget { Device = usb },
                            Command = command,
                        },
                    ],
                };
                foreach (var formatItem in BuildConvertMenuChildren(command, (format, bitrate) => new UsbSendTarget
                {
                    Device = usb,
                    Format = format,
                    BitrateKbps = bitrate,
                }))
                {
                    deviceItem.Children.Add(formatItem);
                }
                children.Add(deviceItem);
            }
            return children;
        }

        public enum PlayMode
        {
            SingleLoop,
            ListLoop,
            RandomLoop,
            RepeatOff
        }

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
            App.MainWindow.DispatcherQueue.TryEnqueue(() =>
            {
                HashSet<string>? usbAlbums = null;
                HashSet<string>? usbAuthors = null;
                if (type == "album")
                {
                    usbAlbums = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var usbMusic in App.Services.GetRequiredService<UsbDeviceService>().MusicOnDevice)
                    {
                        if (usbMusic.Album is { } a) usbAlbums.Add(a);
                    }
                }
                else if (type == "artist")
                {
                    usbAuthors = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var usbMusic in App.Services.GetRequiredService<UsbDeviceService>().MusicOnDevice)
                    {
                        if (usbMusic.Author is { } a) usbAuthors.Add(a);
                    }
                }

                foreach (var item in musicList)
                {
                    if (type == "album")
                    {
                        item.IsExistOnDevice = (usbAlbums?.Contains(item.Album) ?? false) ? 1 : 0;
                    }
                    else if (type == "artist")
                    {
                        item.IsExistOnDevice = (usbAuthors?.Contains(item.Author) ?? false) ? 1 : 0;
                    }
                    else if (type == "folder")
                    {
                        var allSongList = App.Services.GetRequiredService<AppViewModel>().SongsSource.AsValueEnumerable().Where(m => m.LastLevelFolderPath == item.LastLevelFolderPath);
                        item.IsExistOnDevice = 0;
                        foreach (var songs in allSongList)
                        {
                            if (App.Services.GetRequiredService<UsbDeviceService>().MusicOnDevice.AsValueEnumerable().Any(usbMusic => usbMusic.Title == item.Title))
                            {
                                item.IsExistOnDevice = 1;
                                break;
                            }
                        }
                    }
                }
            });
        }

        public static void SaveMetaData(Music music, string filePath, byte[] pic, string? lyricsText = null, string? krcText = null)
        {
            Settings.FileBufferSize = 1024 * 256;
            Track theTrack = new(filePath)
            {
                Title = music.Title,
                Album = music.Album,
                Artist = music.Author,
                TrackNumber = music.TrackNumber,
                DiscNumber = music.DiskNumber,
                Year = music.Year
            };
            theTrack.EmbeddedPictures.Clear();
            theTrack.EmbeddedPictures.Add(PictureInfo.fromBinaryData(pic));
            string[] lines = (lyricsText ?? "").Split([Environment.NewLine], StringSplitOptions.None);
            if (lines.Length == 0)
            {
                lines = (krcText ?? "").Split([Environment.NewLine], StringSplitOptions.None);
            }
            theTrack.Lyrics = new List<LyricsInfo>(lines.Length);
            foreach (string line in lines)
            {
                theTrack.Lyrics.Add(new LyricsInfo { UnsynchronizedLyrics = line });
            }
            theTrack.Save();
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


        public static async Task<BitmapImage?> ConvertByteArrayToBitmapImage(byte[] imageData, int maxSize = 0)
        {
            try
            {
                if (imageData is null || imageData.Length == 0)
                    return null;
                using var memStream = new MemoryStream(imageData, writable: false);
                using var stream = memStream.AsRandomAccessStream();
                var bitmapImage = maxSize == 0 ? new BitmapImage() : new BitmapImage { DecodePixelWidth = maxSize };
                await bitmapImage.SetSourceAsync(stream);
                return bitmapImage;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"ConvertByteArrayToBitmapImage 转换失败: {ex.Message}");
                return null;
            }
        }

        public static bool GetIsLightTheme()
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key != null)
            {
                var value = key.GetValue("AppsUseLightTheme");
                if (value is int { } v)
                {
                    return v > 0;
                }
            }

            return true; // 默认为浅色模式
        }

        public static async Task<byte[]> GetRawImage(Music music, bool isManual = false)
        {
            try
            {
                // 磁盘缓存查找（raw bytes，避免重复从音频文件读取内嵌封面）
                if (!string.IsNullOrEmpty(AppSettings.MusicCoverCache)
                    && !string.IsNullOrEmpty(music.ImageHash))
                {
                    var cachePath = GetRawCachePath(music.ImageHash);
                    if (File.Exists(cachePath))
                    {
                        if (File.GetLastWriteTime(cachePath) > File.GetLastWriteTime(music.Path))
                            return File.ReadAllBytes(cachePath);

                        // 缓存过期：清理该 hash 的所有旧格式缓存 (_raw.bin / .bmp / .bgra8 / .jpg)
                        DeleteRawCaches(music.ImageHash);
                    }
                }

                byte[]? picture = [];
                if (FastReadExtensions.Contains(music.Extension))
                {
                    picture = AudioCoverReader.ReadCover(music.Path);
                    //if (picture is null || picture.Length == 0)
                    //{
                    //    Track track = new(music.Path);
                    //    if (track?.EmbeddedPictures is not null && track?.EmbeddedPictures.Count > 0)
                    //    {
                    //        picture = track.EmbeddedPictures.AsValueEnumerable().FirstOrDefault()?.PictureData;
                    //    }
                    //}
                }
                else
                {
                    Track track = new(music.Path);
                    if (track?.EmbeddedPictures is not null && track?.EmbeddedPictures.Count > 0)
                    {
                        picture = track.EmbeddedPictures.AsValueEnumerable().FirstOrDefault()?.PictureData;
                    }
                }
                if (picture is null || picture.Length == 0)
                {
                    picture = await GetPicByteFromNet(music, isManual) ?? [];
                }
                if (picture.Length > 0)
                {
                    Span<byte> hashSpan = stackalloc byte[8];
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(hashSpan, XxHash64.HashToUInt64(picture));
                    var imageHash = Convert.ToHexString(hashSpan);
                    if (music.ImageHash != imageHash)
                    {
                        music.ImageHash = imageHash;
                        _ = App.Services.GetRequiredService<MusicDatabaseService>().UpdateMusicInfo(music);
                    }

                    // 写入 raw bytes 磁盘缓存
                    if (!string.IsNullOrEmpty(AppSettings.MusicCoverCache)
                        && !string.IsNullOrEmpty(music.ImageHash))
                    {
                        try
                        {
                            var cachePath = GetRawCachePath(music.ImageHash);
                            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                            await File.WriteAllBytesAsync(cachePath, picture);
                        }
                        catch (Exception ex) { _logger.LogError(ex, "写_raw.bin缓存失败"); }
                    }
                }
                else
                {
                    if (string.IsNullOrEmpty(music.ImageHash))
                    {
                        music.ImageHash = string.Empty;
                        _ = App.Services.GetRequiredService<MusicDatabaseService>().UpdateMusicInfo(music);
                    }
                }
                return picture;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"GetRawImage 获取原始图片失败: {ex.Message}");
                try
                {
                    return await GetPicByteFromNet(music, isManual) ?? [];
                }
                catch (Exception innerEx)
                {
                    _logger.LogError(innerEx, $"GetRawImage 网络获取图片也失败: {innerEx.Message}");
                    return [];
                }
            }
        }

        internal static string GetRawCachePath(string imageHash)
            => Path.Combine(AppSettings.MusicCoverCache, "Cache", $"{imageHash}_raw.bin");

        private static void DeleteRawCaches(string imageHash)
        {
            try
            {
                var dir = Path.Combine(AppSettings.MusicCoverCache, "Cache");
                if (!Directory.Exists(dir)) return;

                foreach (var file in Directory.GetFiles(dir, $"{imageHash}_*"))
                {
                    try { File.Delete(file); }
                    catch (Exception ex) { _logger.LogError(ex, "DeleteRawCaches 清理失败: {File}", file); }
                }
            }
            catch (Exception ex) { _logger.LogError(ex, "DeleteRawCaches 失败"); }
        }

        private static readonly string[] AudioInfoProps =
        [
            "System.Audio.SampleRate",
            "System.Audio.ChannelCount",
            "System.Audio.SampleSize",
            "System.Audio.EncodingBitrate",
            "System.Media.Duration",
        ];

        /// <summary>
        /// ATL 主路径失败时的兜底探测（原实现依赖 BASS，主程序移除 BASS 后改用
        /// Windows 属性系统获取音频属性；标签字段仍尽量取自 ATL）。
        /// </summary>
        /// <summary>
        /// 文件被占用（外部程序写入中、标签重写并发等）时的指数退避重试。
        /// 大文件的容器级标签重写可达数秒，窗口需覆盖；总时长约 12 秒。
        /// </summary>
        private static T RetryOnFileBusy<T>(Func<T> factory, string path, int maxRetries = 6)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    return factory();
                }
                catch (IOException) when (attempt < maxRetries)
                {
                    _logger.LogWarning($"文件被占用，重试 {attempt + 1}/{maxRetries}: {path}");
                    Thread.Sleep(Math.Min(200 << attempt, 3200));
                }
            }
        }

        public static async Task<AudioFileInfo> GetAudioInfo(StorageFile file)
        {
            AudioFileInfo fileInfo = new();
            Track? track = null;
            try { track = RetryOnFileBusy(() => new Track(file.Path), file.Path); }
            catch (Exception ex) { _logger.LogWarning(ex, $"GetAudioInfo ATL 读取失败: {file.Path}"); }
            try
            {
                var props = await file.Properties.RetrievePropertiesAsync(AudioInfoProps);
                fileInfo.SampleRate = GetPropertyValue(props, "System.Audio.SampleRate", 0);
                fileInfo.ChannelCount = GetPropertyValue(props, "System.Audio.ChannelCount", 0);
                int bitDepth = GetPropertyValue(props, "System.Audio.SampleSize", 0);
                // DSD 位深语义与旧版保持一致（1 = DSD 单比特流）
                fileInfo.BitDepth = IsDsdExtension(file.Path) ? 1 : bitDepth;
                int bitRateRaw = GetPropertyValue(props, "System.Audio.EncodingBitrate", 0);
                fileInfo.BitRate = bitRateRaw > 0 ? bitRateRaw / 1000 : 0;
                ulong duration100ns = GetPropertyValue(props, "System.Media.Duration", 0UL);
                if (duration100ns > 0)
                    fileInfo.Duration = TimeSpan.FromMilliseconds(duration100ns / 10000.0);

                fileInfo.Title = string.IsNullOrWhiteSpace(track?.Title) ? Path.GetFileNameWithoutExtension(file.Path) : track!.Title;
                fileInfo.Album = string.IsNullOrWhiteSpace(track?.Album) ? "未知专辑" : track!.Album;
                fileInfo.Artist = string.IsNullOrWhiteSpace(track?.Artist) ? "未知艺术家" : track!.Artist;
                fileInfo.Year = track?.Year ?? 0;
                fileInfo.TrackNumber = track?.TrackNumber ?? 0;
                fileInfo.DiskNumber = track?.DiscNumber ?? 0;
                if (track?.Bitrate > 0 && fileInfo.BitRate == 0)
                    fileInfo.BitRate = track!.Bitrate;
                if (track != null && track.DurationMs > 0 && fileInfo.Duration <= TimeSpan.Zero)
                    fileInfo.Duration = TimeSpan.FromMilliseconds(track.DurationMs);
                fileInfo.Lyrics = track?.Lyrics?.AsValueEnumerable().Count() > 0
                    ? ParseLyrics(track.Lyrics[0].SynchronizedLyrics)
                    : string.Empty;
                return fileInfo;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"GetAudioInfo 获取音频信息失败: {ex.Message}");
                fileInfo.Title = Path.GetFileNameWithoutExtension(file.Path);
                return fileInfo;
            }
        }

        private static bool IsDsdExtension(string path)
        {
            var ext = Path.GetExtension(path.AsSpan());
            return ext.Equals(".dsf", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".dff", StringComparison.OrdinalIgnoreCase);
        }

        private static T GetPropertyValue<T>(IDictionary<string, object> props, string key, T fallback) where T : struct
        {
            if (props.TryGetValue(key, out var value) && value is not null)
            {
                try { return (T)Convert.ChangeType(value, typeof(T)); }
                catch { return fallback; }
            }
            return fallback;
        }

        private static string ParseLyrics(IList<LyricsInfo.LyricsPhrase> SynchronizedLyrics)
        {
            var sb = new StringBuilder();
            foreach (LyricsInfo.LyricsPhrase phrase in SynchronizedLyrics)
            {
                sb.Append('[').Append(EncodeTimecode_ms(phrase.TimestampStart)).Append("] ").Append(phrase.Text).Append('\n');
            }
            return sb.ToString();
        }

        private static string EncodeTimecode_ms(int timestampMs)
        {
            // 确保时间戳不为负数（处理可能的异常值）
            int ms = Math.Max(timestampMs, 0);

            // 拆分时间单位
            int totalSeconds = ms / 1000;
            int milliseconds = ms % 1000;

            int minutes = totalSeconds / 60;
            int seconds = totalSeconds % 60;

            // 格式化输出（分:秒.毫秒，补零对齐）
            return $"{minutes:D2}:{seconds:D2}.{milliseconds:D3}";
        }

        private static readonly HashSet<string> MusicExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".mp3", ".wav", ".flac", ".wma", ".aac", ".ogg", ".oga", ".aiff", ".aif", ".m4a", ".dsf", ".dff", ".ape", ".opus", ".wv"
        };

        public static bool IsMusicFile(string fileType)
        {
            return MusicExtensions.Contains(fileType);
        }

        // 将字典转换为JSON字符串的方法
        public static string ConvertToJson(Dictionary<string, double> dict)
        {
            if (dict is null)
            {
                throw new ArgumentNullException(nameof(dict), "字典不能为空");
            }

            try
            {
                var typeInfo = AppJsonSerializerContextHelper.Default.DictionaryStringDouble;
                // 使用System.Text.Json进行序列化
                return JsonSerializer.Serialize(dict, typeInfo);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"ConvertToJson 序列化失败: {ex.Message}");
                throw;
            }
        }

        // 将JSON字符串转回字典的方法
        public static Dictionary<string, double> ConvertToDictionary(string? jsonString)
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
                var typeInfo = AppJsonSerializerContextHelper.Default.DictionaryStringDouble;

                // 2. 使用重载的 Deserialize 方法，传入 TypeInfo
                return (Dictionary<string, double>)
                    JsonSerializer.Deserialize(jsonString, typeInfo);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"ConvertToDictionary 反序列化失败: {ex.Message}");
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
                _logger.LogError(ex, $"DeleteFileFromDisk 删除文件失败: {ex.Message}");
                return false;
            }
        }

        public static string GetFirstLetterAdvanced(string text)
        {
            if (string.IsNullOrEmpty(text))
                return CharStringCache.GetHashTag();

            string trimmedText = text.Trim();
            if (trimmedText.Length == 0)
                return CharStringCache.GetHashTag();

            char firstChar = trimmedText[0];

            if (firstChar >= 'A' && firstChar <= 'Z')
                return CharStringCache.GetLetter(firstChar);

            if (firstChar >= 'a' && firstChar <= 'z')
            {
                char upperChar = char.ToUpper(firstChar);
                return CharStringCache.GetLetter(upperChar);
            }
            if (char.IsDigit(firstChar))
                return CharStringCache.GetHashTag();
            if (ChineseChar.IsValidChar(firstChar))
            {
                try
                {
                    ChineseChar chineseChar = new ChineseChar(firstChar);
                    var pinyinCollection = chineseChar.Pinyins;

                    if (pinyinCollection is not null && pinyinCollection.Count > 0)
                    {
                        string firstPinyin = pinyinCollection[0];
                        if (!string.IsNullOrEmpty(firstPinyin))
                        {
                            char firstLetter = '\0';
                            foreach (char c in firstPinyin.AsValueEnumerable().Where(char.IsLetter))
                            {
                                firstLetter = c;
                                break;
                            }

                            if (firstLetter != '\0')
                            {
                                char upperChar = char.ToUpper(firstLetter);
                                return CharStringCache.GetLetter(upperChar);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"GetFirstLetterAdvanced 拼音转换失败: {ex.Message}");
                    return CharStringCache.GetZhongChar();
                }
            }
            return CharStringCache.GetHashTag();
        }

        [GeneratedRegex(@"\[(\d{2}):(\d{2})\.(\d{2,3})\]")]
        private static partial Regex TimeCodeRegex();

        private static readonly SpanAction<char, string> _convertLyricsWriter = ConvertLyricsCore;

        public static string ConvertLyrics(string lyrics)
        {
            var regex = TimeCodeRegex();
            var source = lyrics.AsSpan();

            int ms3Count = 0;
            foreach (var match in regex.EnumerateMatches(source))
                if (match.Length == 11) ms3Count++;

            int finalLength = source.Length - ms3Count;
            if (finalLength == source.Length)
                return lyrics;

            return string.Create(finalLength, lyrics, _convertLyricsWriter);
        }

        private static void ConvertLyricsCore(Span<char> dest, string lyrics)
        {
            var r = TimeCodeRegex();
            var src = lyrics.AsSpan();
            int srcPos = 0, destPos = 0;

            foreach (var match in r.EnumerateMatches(src))
            {
                int literalLen = match.Index - srcPos;
                if (literalLen > 0)
                {
                    src.Slice(srcPos, literalLen).CopyTo(dest.Slice(destPos));
                    destPos += literalLen;
                }

                if (match.Length == 11)
                {
                    src.Slice(match.Index, 9).CopyTo(dest.Slice(destPos));
                    destPos += 9;
                    dest[destPos++] = src[match.Index + 10];
                }
                else
                {
                    src.Slice(match.Index, 10).CopyTo(dest.Slice(destPos));
                    destPos += 10;
                }

                srcPos = match.Index + match.Length;
            }

            if (srcPos < src.Length)
                src.Slice(srcPos).CopyTo(dest.Slice(destPos));
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
                    _logger.LogError(ex, $"OpenFileInExplorer 打开资源管理器失败: {ex.Message}");
                }
            }
            else
            {
            }
        }

        private static async Task<byte[]?> GetPicByteFromNet(Music music, bool isManual = false)
        {
            try
            {
                byte[] picture = null;
                if (AppSettings.IsAutoCoverEnabled && !isManual)
                {
                    using var cancellationToken = new CancellationTokenSource();
                    if (!Directory.Exists(AppSettings.MusicCoverCache))
                    {
                        Directory.CreateDirectory(AppSettings.MusicCoverCache);
                    }
                    string fileName = $"{music.Title}_{music.Album}_{music.Author}";
                    fileName = InvalidFileNameCharsRegex.Replace(fileName, "_");
                    string filePath = System.IO.Path.Combine(AppSettings.MusicCoverCache, fileName + ".bin");
                    if (System.IO.File.Exists(filePath))
                    {
                        picture = System.IO.File.ReadAllBytes(filePath);
                    }
                    else
                    {
                        picture ??= await App.Services.GetRequiredService<LrcService>().GetMixedCoverImageAsync(music, cancellationToken.Token);
                        if (picture is not null)
                        {
                            System.IO.File.WriteAllBytes(filePath, picture);
                        }
                    }
                }
                return picture;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"GetPicByteFromNet 网络获取图片失败: {ex.Message}");
                return null;
            }
        }


        public static async Task<(string, string)> GetLyricsFromNet(Music musicDetail)
        {
            return await App.Services.GetRequiredService<LrcService>().GetMixedLyricsAsync(musicDetail);
        }

        public static async Task<(string, string)> GetKrcFromNet(Music musicDetail)
        {
            //string res = await LrcService.GetLyricsFromHelper(musicDetail.Title, musicDetail.Album, musicDetail.Author, musicDetail.Duration);
            return await App.Services.GetRequiredService<LrcService>().GetKrcLyricsAsync(musicDetail);
        }

        public static DateTime GetSafeFileCreateTime(string filePath)
        {
            try
            {
                return System.IO.File.GetCreationTime(filePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"GetSafeFileCreateTime 获取创建时间失败: {ex.Message}");
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
                _logger.LogError(ex, $"GetSafeFileUpdateTime 获取修改时间失败: {ex.Message}");
                return DateTime.Now;
            }
        }

        public static async Task<(Music? Music, string? Lyrics)> GetMusicInfo(StorageFile file)
        {
            string lastLevelDirectory = Path.GetDirectoryName(file.Path);
            DirectoryInfo directoryInfo = new DirectoryInfo(lastLevelDirectory);
            string lastLevelFolderPath = directoryInfo.Name;
            try
            {
                Track track = RetryOnFileBusy(() => new Track(file.Path), file.Path);
                string title = "未知标题";
                string artist = "未知艺术家";
                string album = "未知专辑";
                string lyrics = string.Empty;
                TimeSpan duration = TimeSpan.Zero;
                int trackNumber = track.TrackNumber ?? 0;
                int diskNumber = track.DiscNumber ?? 0;
                title = !string.IsNullOrWhiteSpace(track.Title) ?
                   track.Title : Path.GetFileNameWithoutExtension(file.Name);
                if (!string.IsNullOrWhiteSpace(track.Artist)) artist = track.Artist;
                if (!string.IsNullOrWhiteSpace(track.Album)) album = track.Album;
                int sampleRate = (int)track.SampleRate;
                int bitDepth = track.BitDepth <= 0 ? await GetBitDepth(file) : track.BitDepth;
                int bitRate = track.Bitrate == 0 ? await GetBitRate(file) : track.Bitrate;
                int year = track.Year ?? 0;
                duration = TimeSpan.FromMilliseconds(track.DurationMs);
                int channelCount = track.ChannelsArrangement.NbChannels;
                LyricsInfo? lyricsInfo = track.Lyrics.AsValueEnumerable().FirstOrDefault();
                if (lyricsInfo is not null)
                {
                    var synced = lyricsInfo.SynchronizedLyrics;
                    var sb = new StringBuilder(synced.Count * 64);
                    foreach (LyricsInfo.LyricsPhrase phrase in synced)
                    {
                        sb.Append('[').Append(EncodeTimecode_ms(phrase.TimestampStart)).Append("] ").Append(phrase.Text).Append('\n');
                    }
                    lyrics = sb.ToString();
                }
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
                    CreateTime = ToolUtils.GetSafeFileCreateTime(file.Path),
                    UpdateTime = ToolUtils.GetSafeFileUpdateTime(file.Path)
                };
                return (music, lyrics);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"GetMusicInfo 读取音乐信息失败: {ex.Message}");
                AudioFileInfo wavFileInfo = await ToolUtils.GetAudioInfo(file);
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
                        CreateTime = ToolUtils.GetSafeFileCreateTime(file.Path),
                        UpdateTime = ToolUtils.GetSafeFileUpdateTime(file.Path)
                    };
                    return (music, wavFileInfo.Lyrics);
                }
                catch (Exception innerEx)
                {
                    _logger.LogError(innerEx, $"GetMusicInfo 创建基本音乐条目失败: {file.Path}, {innerEx.Message}");
                }

            }
            return (null, null);
        }

        private static readonly string[] SampleSizeProps = ["System.Audio.SampleSize"];
        private static readonly string[] EncodingBitrateProps = ["System.Audio.EncodingBitrate"];

        private static async Task<int> GetBitDepth(StorageFile file)
        {
            var bitDepth = 16;
            try
            {
                var audioProps = await file.Properties.RetrievePropertiesAsync(SampleSizeProps);
                // 处理位深度
                if (audioProps.ContainsKey("System.Audio.SampleSize") && audioProps["System.Audio.SampleSize"] is not null)
                {
                    var sampleSize = Convert.ToInt32(audioProps["System.Audio.SampleSize"]);
                    bitDepth = sampleSize;
                }
            }
            catch (Exception ex)
            {
                bitDepth = 16;
                _logger.LogError(ex, $"GetBitDepth 获取音频位深度失败: {ex.Message}");
            }
            return bitDepth;
        }

        private static async Task<int> GetBitRate(StorageFile file)
        {
            var bitRate = 0;
            try
            {
                var audioProps = await file.Properties.RetrievePropertiesAsync(EncodingBitrateProps);
                if (audioProps.ContainsKey("System.Audio.EncodingBitrate") && audioProps["System.Audio.EncodingBitrate"] is not null)
                {
                    int rawBitrate = Convert.ToInt32(audioProps["System.Audio.EncodingBitrate"]);
                    bitRate = rawBitrate > 0 ? rawBitrate / 1000 : 0;
                }
            }
            catch (Exception ex)
            {
                bitRate = 0;
                _logger.LogError(ex, $"GetBitRate 获取音频比特率失败: {ex.Message}");
            }
            return bitRate;
        }

        public async static Task<List<PlayList>> OpenM3u8File()
        {
            var playLists = new List<PlayList>();
            var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(App.MainWindow.AppWindow.Id);
            // 添加m3u8文件筛选器
            picker.FileTypeFilter.Add(".m3u8");
            // 显示文件选择器并获取结果
            var filesPickerResult = await picker.PickMultipleFilesAsync();
            foreach (var filePickerResult in filesPickerResult)
            {
                var file = await StorageFile.GetFileFromPathAsync(filePickerResult.Path);
                if (file is not null)
                {
                    try
                    {
                        PlayList playList = await App.Services.GetRequiredService<MusicDatabaseService>().GetPlayListByName(Path.GetFileNameWithoutExtension(file.Name));
                        int playListId = 0;
                        if (playList is not null)
                        {
                            playListId = playList.Id;
                        }
                        else
                        {
                            playList = new() { Name = Path.GetFileNameWithoutExtension(file.Name) };
                            playListId = await App.Services.GetRequiredService<MusicDatabaseService>().InsertPlayList(playList);
                        }

                        string fileContent = await FileIO.ReadTextAsync(file);
                        if (!string.IsNullOrEmpty(fileContent))
                        {
                            await ParseM3u8Content(fileContent, playListId);
                        }
                        int songCount = 0;
                        for (int k = 0; k < AppData.AllPlayListMusics.Count; k++)
                        {
                            if (AppData.AllPlayListMusics[k].PlayListId == playListId)
                                songCount++;
                        }
                        playList.SongCount = songCount;
                        playLists.Add(playList);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"OpenM3u8File 处理播放列表文件失败: {ex.Message}");
                    }
                }
            }
            return playLists;
        }

        public async static Task ParseM3u8Content(string fileContent, int playListId)
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
                    Music? music = App.Services.GetRequiredService<AppViewModel>().SongsSource.AsValueEnumerable().FirstOrDefault(m => m.Path.Contains(Path.GetFileName(line)));
                    if (music is not null)
                    {
                        await App.Services.GetRequiredService<MusicDatabaseService>().AddMusicToPlayList(playListId, music.Id);
                    }
                }
            }
        }

        public static string GenerateM3U8Content(IEnumerable<PlayListMusicItem> musics, string playlistName)
        {
            var sb = new StringBuilder();
            sb.AppendLine("#EXTM3U");
            sb.AppendLine($"# Playlist: {playlistName}");
            sb.AppendLine($"# Created: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            foreach (var music in musics)
            {
                sb.AppendLine($"#EXTINF:{music.Music.Author} - {music.Music.Title}");
                sb.AppendLine(music.Music.Path);
            }
            return sb.ToString();
        }

        public static string GetCleanFontName(string fontSource)
        {
            if (string.IsNullOrEmpty(fontSource)) return fontSource;
            var index = fontSource.IndexOf(',');
            return index > 0 ? fontSource.Substring(0, index).Trim() : fontSource.Trim();
        }
        private static string? _cachedCultureName;
        private static string[]? _cachedCultureArray;

        public static List<FontInfo> GetSystemFontsInternal()
        {
            try
            {
                var cultureName = CultureInfo.CurrentUICulture.Name.ToLowerInvariant();
                if (_cachedCultureArray is null || _cachedCultureName != cultureName)
                {
                    _cachedCultureName = cultureName;
                    _cachedCultureArray = [cultureName];
                }
                var names = CanvasTextFormat.GetSystemFontFamilies();
                var displayNames = CanvasTextFormat.GetSystemFontFamilies(_cachedCultureArray);
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
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"GetSystemFontsInternal 处理字体失败: {ex.Message}");
                    }
                }
                return [.. list.AsValueEnumerable().OrderBy(f => f.Name)];
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"GetSystemFontsInternal 获取系统字体失败: {ex.Message}");
                return new List<FontInfo>();
            }
        }

        public static async Task ExportPlayList(PlayList playList)
        {
            try
            {
                var fileSavePicker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(App.MainWindow.AppWindow.Id);
                fileSavePicker.FileTypeChoices.Add("M3U8 播放列表", new List<string>() { ".m3u8" });
                fileSavePicker.SuggestedFileName = playList.Name;
                var file = await fileSavePicker.PickSaveFileAsync();
                if (file is not null)
                {
                    IEnumerable<PlayListMusicItem> musics = App.Services.GetRequiredService<MusicDatabaseService>().GetMusicByPlayListIdFromMem(playList.Id);
                    var m3u8Content = ToolUtils.GenerateM3U8Content(musics, playList.Name);
                    await System.IO.File.WriteAllTextAsync(file.Path, m3u8Content);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"ExportPlayList 导出播放列表失败: {ex.Message}");
            }
        }

        public static void CleanupStaleCacheFiles()
        {
            try
            {
                var cacheFolder = Path.Combine(AppSettings.MusicCoverCache, "Cache");
                if (!Directory.Exists(cacheFolder)) return;

                // 收集应保留的文件
                var keepFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // 当前尺寸的 .bmp 缩略图
                var thumbPattern = $"*_{CoverLoadQueue.CoverSize}.bmp";
                foreach (var f in Directory.EnumerateFiles(cacheFolder, thumbPattern))
                    keepFiles.Add(f);

                // _raw.bin 全尺寸缓存（与 coverSize 无关）
                foreach (var f in Directory.EnumerateFiles(cacheFolder, "*_raw.bin"))
                    keepFiles.Add(f);

                // 删除旧格式/旧尺寸缓存
                var cacheExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    { ".bmp", ".raw", ".bgra8", ".png", ".jpg" };

                foreach (var file in Directory.EnumerateFiles(cacheFolder))
                {
                    if (keepFiles.Contains(file)) continue;
                    if (cacheExts.Contains(Path.GetExtension(file)))
                        try { System.IO.File.Delete(file); } catch (Exception ex) { _logger.LogError(ex, $"CleanupStaleCacheFiles: {ex.Message}"); }
                }
            }
            catch (Exception ex) { _logger.LogError(ex, "CleanupStaleCacheFiles 失败"); }
        }

        public static string PlayModeToString(PlayMode playMode)
        {
            return playMode switch
            {
                PlayMode.SingleLoop => "SingleLoop",
                PlayMode.ListLoop => "ListLoop",
                PlayMode.RandomLoop => "RandomLoop",
                PlayMode.RepeatOff => "RepeatOff",
                _ => "ListLoop",
            };
        }

        public static int ComputeFastHash(byte[] b)
        {
            int len = b.Length;
            var hc = new HashCode();
            hc.Add(b[0]);
            hc.Add(b[len / 4]);
            hc.Add(b[len / 2]);
            hc.Add(b[len * 3 / 4]);
            hc.Add(b[len - 1]);
            hc.Add(len);
            return hc.ToHashCode();
        }
    }
}
