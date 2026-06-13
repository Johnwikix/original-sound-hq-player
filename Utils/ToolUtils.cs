using ATL;
using ManagedBass;
using ManagedBass.Dsd;
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
using WinUIMusicPlayer.Manager;
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
    public class ToolUtils
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
        public static string GetString(string key)
        {
            try
            {
                return new ResourceLoader().GetString(key);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"GetString 获取资源字符串失败: {ex.Message}");
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
                        if (AppData.MusicOnUsbDevice.AsValueEnumerable().Any(usbMusic => usbMusic.Album == item.Album))
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
                        if (AppData.MusicOnUsbDevice.AsValueEnumerable().Any(usbMusic => usbMusic.Author == item.Author))
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
                        var allSongList = App.Services.GetRequiredService<AppViewModel>().SongsSource.AsValueEnumerable().Where(m => m.LastLevelFolderPath == item.LastLevelFolderPath);
                        item.IsExistOnDevice = 0;
                        foreach (var songs in allSongList)
                        {
                            if (AppData.MusicOnUsbDevice.AsValueEnumerable().Any(usbMusic => usbMusic.Title == item.Title))
                            {
                                item.IsExistOnDevice = 1;
                                break;
                            }
                        }
                    }
                });
            }
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
                        picture = picture = track.EmbeddedPictures.AsValueEnumerable().FirstOrDefault()?.PictureData;
                    }
                }
                if (picture is null || picture.Length == 0)
                {
                    picture = await GetPicByteFromNet(music, isManual) ?? [];
                }
                if (picture.Length > 0)
                {
                    var imageHash = Convert.ToHexString(XxHash64.Hash(picture));
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

        private static string GetRawCachePath(string imageHash)
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

        public static async Task<AudioFileInfo> GetAudioInfo(StorageFile file)
        {
            BassManager.Initialize();
            int stream = 0;
            AudioFileInfo fileInfo = new();
            Track track = new(file.Path);
            try
            {
                if (Path.GetExtension(file.Path) == ".dff")
                {
                    stream = BassDsd.CreateStream(file.Path, 0, 0, BassFlags.DSDOverPCM | BassFlags.Float | BassFlags.Decode | BassFlags.AsyncFile);
                    Bass.ChannelGetInfo(stream, out ChannelInfo info);
                    Bass.ChannelGetAttribute(
                                        stream,
                                        ChannelAttribute.Bitrate,
                                        out var bitrate
                    );
                    double totalSeconds = Bass.ChannelBytes2Seconds(stream, Bass.ChannelGetLength(stream));
                    fileInfo.BitDepth = 1;
                    fileInfo.BitRate = (int)bitrate;
                    fileInfo.SampleRate = info.Frequency * 16;
                    fileInfo.Duration = TimeSpan.FromSeconds(totalSeconds);
                }
                else
                {
                    stream = Bass.CreateStream(file.Path, 0, 0, BassFlags.Default | BassFlags.AsyncFile);
                    Bass.ChannelGetInfo(stream, out ChannelInfo info);
                    fileInfo.SampleRate = (int)(track?.SampleRate ?? info.Frequency);
                    fileInfo.ChannelCount = info.Channels;
                    fileInfo.BitDepth = info.OriginalResolution;
                    fileInfo.Duration = (track?.Duration ?? 0) != 0
                                   ? TimeSpan.FromSeconds(track?.Duration ?? 0) : TimeSpan.FromSeconds(Bass.ChannelBytes2Seconds(stream, Bass.ChannelGetLength(stream)));
                }
                fileInfo.Title = string.IsNullOrEmpty(track?.Title) ? Path.GetFileNameWithoutExtension(file.Path) : track.Title;
                fileInfo.Album = string.IsNullOrEmpty(track?.Album) ? "未知专辑" : track.Album;
                fileInfo.Artist = string.IsNullOrEmpty(track?.Artist) ? "未知艺术家" : track.Artist;
                fileInfo.BitRate = track?.Bitrate ?? 0;
                fileInfo.Year = track?.Year ?? 0;
                fileInfo.TrackNumber = track?.TrackNumber ?? 0;
                fileInfo.DiskNumber = track?.DiscNumber ?? 0;
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
            finally
            {
                if (stream != 0)
                {
                    Bass.StreamFree(stream);
                }
            }
        }

        private static string ParseLyrics(IList<LyricsInfo.LyricsPhrase> SynchronizedLyrics)
        {
            string lyrics = string.Empty;
            foreach (LyricsInfo.LyricsPhrase phrase in SynchronizedLyrics)
            {
                lyrics += "[" + EncodeTimecode_ms(phrase.TimestampStart) + "] " + phrase.Text + "\n";
            }
            return lyrics;
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

        public static bool IsMusicFile(string fileType)
        {
            var musicExtensions = new[] { ".mp3", ".wav", ".flac", ".wma", ".aac", ".ogg", ".oga", ".aiff", ".aif", ".m4a", ".dsf", ".dff", ".ape", ".opus", ".wv" };
            return musicExtensions.AsValueEnumerable().Contains(fileType.ToLower());
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
                    _logger.LogError(ex, $"OpenFileInExplorer 打开资源管理器失败: {ex.Message}");
                }
            }
            else
            {
            }
        }

        public static void ClearAllUsbStatus()
        {
            App.Services.GetRequiredService<AppViewModel>().ClearUsbDevice();
            App.Services.GetRequiredService<AppViewModel>().RefreshUsbDeviceMusicList();
        }

        public static void RefreshAllUsbStatus()
        {
            App.Services.GetRequiredService<AppViewModel>().RefreshUsbDeviceMusicList();
        }
        private static async Task<byte[]?> GetPicByteFromNet(Music music, bool isManual = false)
        {
            try
            {
                byte[] picture = null;
                if (AppSettings.IsAutoCoverEnabled && !isManual)
                {
                    var cancellationToken = new CancellationTokenSource();
                    if (!Directory.Exists(AppSettings.MusicCoverCache))
                    {
                        Directory.CreateDirectory(AppSettings.MusicCoverCache);
                    }
                    string fileName = $"{music.Title}_{music.Album}_{music.Author}";
                    string invalidChars = new string(System.IO.Path.GetInvalidFileNameChars()) + new string(System.IO.Path.GetInvalidPathChars());
                    fileName = Regex.Replace(fileName, $"[{Regex.Escape(invalidChars)}]", "_");
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

        public static async Task<Music> GetMusicInfo(StorageFile file)
        {
            string lastLevelDirectory = Path.GetDirectoryName(file.Path);
            DirectoryInfo directoryInfo = new DirectoryInfo(lastLevelDirectory);
            string lastLevelFolderPath = directoryInfo.Name;
            try
            {
                Track track = new(file.Path);
                string title = "未知标题";
                string artist = "未知艺术家";
                string album = "未知专辑";
                string lyrics = string.Empty;
                string klyrics = string.Empty;
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
                    foreach (LyricsInfo.LyricsPhrase phrase in lyricsInfo.SynchronizedLyrics)
                    {
                        lyrics += "[" + EncodeTimecode_ms(phrase.TimestampStart) + "] " + phrase.Text + "\n";
                    }
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
                return music;
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
                    return music;
                }
                catch (Exception innerEx)
                {
                    _logger.LogError(innerEx, $"GetMusicInfo 创建基本音乐条目失败: {file.Path}, {innerEx.Message}");
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
                            ParseM3u8Content(fileContent, playListId);
                        }
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

        public static async void ExportPlayList(PlayList playList)
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

                var pattern = $"*_{CoverLoadQueue.CoverSize}.png";
                var validFiles = Directory.EnumerateFiles(cacheFolder, pattern).AsValueEnumerable().ToHashSet();

                foreach (var file in Directory.EnumerateFiles(cacheFolder, "*.png"))
                {
                    if (!validFiles.Contains(file))
                        try { System.IO.File.Delete(file); } catch (Exception ex) { _logger.LogError(ex, $"CleanupStaleCacheFiles 清理缓存文件失败: {ex.Message}"); }
                }
            }
            catch (Exception ex) { _logger.LogError(ex, $"CleanupStaleCacheFiles 清理过期缓存失败: {ex.Message}"); }
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
