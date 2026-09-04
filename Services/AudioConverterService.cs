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
        /// <summary>
        /// 本应用正在转换/写标签的输出文件集合：此期间 AutoScan 的 AddNewMusicAsync
        /// 跳过这些路径（ATL 以 FileShare.Read 打开，与标签写入的 ReadWrite 句柄互斥，
        /// 大文件 m4a/wma 头部重写可达数秒，靠重试等写者不可靠），入库由转换流程主动完成。
        /// </summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _activeOutputs =
            new(StringComparer.OrdinalIgnoreCase);

        public static bool IsActiveOutput(string path) => _activeOutputs.ContainsKey(path);
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

        public EventHandler<double>? updateProgress { get; set; }
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

        public async Task<bool> ConvertAudio2Wav(Music music, string type = "wav", int bitRateKbps = 320)
        {
            string format = type.ToLowerInvariant();
            string outputPath = GenerateOutputPath(music.Path, format);
            _activeOutputs[outputPath] = 0; // 转换 + 写标签 + 入库全程对 AutoScan 屏蔽
            try
            {
                try
                {
                    bool isDsd = music.Extension.Equals("dsf", StringComparison.OrdinalIgnoreCase)
                              || music.Extension.Equals("dff", StringComparison.OrdinalIgnoreCase);
                    int dsdFreq = isDsd ? AppViewModel.DsdPcmFreq : 0;
                    int dsdGain = isDsd ? AppViewModel.DsdGain : 0;

                    if (!music.Extension.Equals(format, StringComparison.OrdinalIgnoreCase))
                    {
                        await Task.Run(() => _converter.Convert(music.Path, outputPath, format, dsdFreq, dsdGain, bitRateKbps));
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"ConvertAudio2Wav 音频转换失败: {ex.Message}");
                    OnProgressChanged(100);
                    return false;
                }
                await SaveMetaDataAsync(music, outputPath);
                // 转换产物主动入库（不再依赖 AutoScan 二次扫描发现），列表刷新由调用方统一触发
                await AddConvertedFileToLibraryAsync(outputPath);
                return true;
            }
            finally
            {
                _activeOutputs.TryRemove(outputPath, out _);
            }
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
