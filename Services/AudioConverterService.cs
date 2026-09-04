using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
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
    /// </summary>
    public class AudioConverterService
    {
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

        public async Task ConvertAudio2Wav(Music music, string type = "wav")
        {
            try
            {
                string outputPath = GenerateOutputPath(music.Path, type);
                bool isDsd = music.Extension.Equals("dsf", StringComparison.OrdinalIgnoreCase)
                          || music.Extension.Equals("dff", StringComparison.OrdinalIgnoreCase);
                int dsdFreq = isDsd ? AppViewModel.DsdPcmFreq : 0;
                int dsdGain = isDsd ? AppViewModel.DsdGain : 0;

                if (!music.Extension.Equals(type, StringComparison.OrdinalIgnoreCase))
                {
                    await Task.Run(() => _converter.Convert(music.Path, outputPath, type.ToLowerInvariant(), dsdFreq, dsdGain));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"ConvertAudio2Wav 音频转换失败: {ex.Message}");
                OnProgressChanged(100);
                return;
            }
            await SaveMetaDataAsync(music, GenerateOutputPath(music.Path, type));
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

        private string GenerateOutputPath(string inputPath, string extension)
        {
            string directory = Path.GetDirectoryName(inputPath);
            string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(inputPath);
            return Path.Combine(directory, $"{fileNameWithoutExtension}_output.{extension.ToLower()}");
        }

        private void OnProgressChanged(double progress)
        {
            updateProgress?.Invoke(this, progress);
        }
    }
}
