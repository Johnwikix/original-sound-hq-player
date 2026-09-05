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
    /// BASS 已从主程序移除；转换完成后沿用 ATL 写回元数据（标签/歌词/封面）。
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
            ["wma"] = "wma",
        };

        /// <summary>格式对应的输出文件扩展名（未知格式原样返回）。</summary>
        public static string GetExtensionForFormat(string format)
            => FormatExtensionMap.TryGetValue(format, out var ext) ? ext : format;

        /// <summary>
        /// 转换 + 写标签，但不入库——供 USB 导出等"目标不在音乐库"的场景。
        /// 全程持有写入门（转换 → 标签重写 → 调用方后续动作期间扫描方跳过该路径）。
        /// 失败返回 false，进度事件保证达 100。
        /// </summary>
        public async Task<bool> ConvertForExportAsync(Music music, string outputPath, string format, int bitRateKbps = 320)
        {
            using (AudioFileWriteGate.BeginWrite(outputPath))
            {
                try
                {
                    bool isDsd = music.Extension.Equals("dsf", StringComparison.OrdinalIgnoreCase)
                              || music.Extension.Equals("dff", StringComparison.OrdinalIgnoreCase);
                    int dsdFreq = isDsd ? AppViewModel.DsdPcmFreq : 0;
                    int dsdGain = isDsd ? AppViewModel.DsdGain : 0;

                    await Task.Run(() => _converter.Convert(music.Path, outputPath, format, dsdFreq, dsdGain, bitRateKbps));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"ConvertForExportAsync 导出转换失败: {ex.Message}");
                    OnProgressChanged(100);
                    return false;
                }
                await SaveMetaDataAsync(music, outputPath);
                return true;
            }
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

        private async Task SaveMetaDataAsync(Music music, string outputPath)
        {
            try
            {
                var (lyricsText, _, krcText, _) = await App.Services.GetRequiredService<MusicDatabaseService>().GetLyricsAsync(music.Id);
                byte[] pic = await ToolUtils.GetRawImage(music);
                ToolUtils.SaveMetaData(music, outputPath, pic, lyricsText, krcText);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"SaveMetaData 保存元数据失败: {ex.Message}");
            }
            finally
            {
                OnProgressChanged(100);
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
