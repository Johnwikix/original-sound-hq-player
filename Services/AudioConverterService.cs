using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using WinUIMusicPlayer.AudioConverters;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.Services
{
    /// <summary>
    /// 音频转换服务：解码/编码由进程内的 FFmpeg DLL（FFmpegAudioConverter）完成，
    /// BASS 已从主程序移除。元数据（标签/歌词/封面）随转换由 FFmpeg 内联写入，
    /// 无 ATL 后置；容器能力差异（歌词/封面可写范围）由转换器处理。
    /// 返回 true 表示转换成功；失败只记日志并保证进度事件达 100。
    /// </summary>
    public class AudioConverterService
    {
        public EventHandler<double>? updateProgress { get; set; }

        /// <summary>内部格式名 → 输出文件扩展名（aac/alac 主流封装为 m4a）。</summary>
        private static readonly Dictionary<string, string> FormatExtensionMap = new(StringComparer.OrdinalIgnoreCase)
        {
            ["wav"] = "wav",
            ["flac"] = "flac",
            ["mp3"] = "mp3",
            ["ogg"] = "ogg",
            ["opus"] = "opus",
            ["aac"] = "m4a",
            ["alac"] = "m4a",
        };

        /// <summary>格式对应的输出文件扩展名（未知格式原样返回）。</summary>
        public static string GetExtensionForFormat(string format)
            => FormatExtensionMap.TryGetValue(format, out var ext) ? ext : format;

        /// <summary>
        /// 转换（元数据随转换由 FFmpeg 内联写入，无 ATL 后置），不入库——
        /// 供 USB 导出等"目标不在音乐库"的场景。全程持有写入门。
        /// 失败返回 false，进度事件保证达 100。
        /// </summary>
        public async Task<bool> ConvertForExportAsync(Music music, string outputPath, string format, int bitRateKbps = 320)
        {
            using (AudioFileWriteGate.BeginWrite(outputPath))
            {
                try
                {
                    // 元数据获取（DB/封面缓存同步 IO）与转换整体移出调用方线程，
                    // 避免 UI 发起的转换被 USB 唤醒等慢 IO 卡住界面
                    return await Task.Run(async () =>
                    {
                        bool isDsd = music.Extension.Equals("dsf", StringComparison.OrdinalIgnoreCase)
                                  || music.Extension.Equals("dff", StringComparison.OrdinalIgnoreCase);
                        int dsdFreq = isDsd ? AppViewModel.DsdPcmFreq : 0;
                        int dsdGain = isDsd ? AppViewModel.DsdGain : 0;

                        // 元数据前置获取，随转换一次性写入；容器能力差异（歌词/封面哪些可写）
                        // 由转换器内部处理，元数据获取失败不阻断转换
                        ConversionMetadata? meta = await BuildConversionMetadataAsync(music);

                        _converter.Convert(music.Path, outputPath, format, dsdFreq, dsdGain, bitRateKbps, meta);
                        return true;
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"ConvertForExportAsync 导出转换失败: {ex.Message}");
                    OnProgressChanged(100);
                    return false;
                }
            }
        }

        /// <summary>转换前取齐歌词与封面，构造内联元数据。</summary>
        private async Task<ConversionMetadata?> BuildConversionMetadataAsync(Music music)
        {
            try
            {
                var (lyricsText, _, krcText, _) = await App.Services.GetRequiredService<MusicDatabaseService>().GetLyricsAsync(music.Id);
                string? lyrics = PickLyrics(lyricsText, krcText);
                byte[]? cover = await ToolUtils.GetRawImage(music);
                return new ConversionMetadata
                {
                    Title = music.Title,
                    Artist = music.Author,
                    Album = music.Album,
                    TrackNumber = music.TrackNumber,
                    DiscNumber = music.DiskNumber,
                    Year = music.Year,
                    CoverBytes = cover is { Length: > 0 } ? cover : null,
                    Lyrics = lyrics,
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"BuildConversionMetadataAsync 元数据获取失败（转换继续，无元数据）: {music.Path}: {ex.Message}");
                return null;
            }
        }

        /// <summary>歌词优先，其次 KRC；两者皆空返回 null。</summary>
        private static string? PickLyrics(string? lyricsText, string? krcText)
        {
            if (!string.IsNullOrWhiteSpace(lyricsText)) return lyricsText;
            if (!string.IsNullOrWhiteSpace(krcText)) return krcText;
            return null;
        }

        private readonly FFmpegAudioConverter _converter;
        private AppViewModel AppViewModel { get; }
        private ILogger<AudioConverterService> _logger;

        public AudioConverterService(AppViewModel appViewModel, ILogger<AudioConverterService> logger)
        {
            AppViewModel = appViewModel;
            _logger = logger;
            _converter = new FFmpegAudioConverter();
            _converter.progressEvent += (_, progress) => OnProgressChanged(progress);
        }

        public async Task<bool> ConvertAudioAsync(Music music, string type = "wav", int bitRateKbps = 320)
        {
            string format = type.ToLowerInvariant();
            if (music.Extension.Equals(format, StringComparison.OrdinalIgnoreCase))
                return true; // 同格式无需转换（多选批量时逐文件判重）
            string outputPath = GenerateOutputPath(music.Path, format);
            if (!await ConvertForExportAsync(music, outputPath, format, bitRateKbps))
                return false;
            // 转换产物主动入库（不再依赖 AutoScan 二次扫描发现），列表刷新由调用方统一触发
            await AddConvertedFileToLibraryAsync(outputPath);
            return true;
        }

        private async Task AddConvertedFileToLibraryAsync(string outputPath)
        {
            try
            {
                await App.Services.GetRequiredService<MusicDatabaseService>().AddConvertedFileAsync(outputPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"AddConvertedFileToLibraryAsync 转换产物入库失败: {outputPath}");
            }
        }

        private string GenerateOutputPath(string inputPath, string format)
        {
            string directory = Path.GetDirectoryName(inputPath);
            string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(inputPath);
            string extension = FormatExtensionMap.TryGetValue(format, out var ext) ? ext : format;
            return Path.Combine(directory, $"{fileNameWithoutExtension}_output.{extension.ToLower()}");
        }

        private void OnProgressChanged(double progress)
        {
            updateProgress?.Invoke(this, progress);
        }
    }
}
