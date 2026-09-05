using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.Helper
{
    /// <summary>
    /// USB 传输的纯 IO 层：按目标设备目录结构（MUSIC\歌手\专辑）写入音频与歌词，
    /// 支持发送前转换（直接编码到设备路径，失败回退为原格式复制）。
    /// 不做任何 UI 或数据层操作——进度提示由 AppViewModel 负责，入库由扫描/播放流程负责。
    /// </summary>
    public class UsbWriterHelper
    {
        private readonly AudioConverterService _converterService;
        private readonly MusicDatabaseService _musicDatabaseService;
        private readonly ILogger<UsbWriterHelper> _logger = App.GetLogger<UsbWriterHelper>();

        public UsbWriterHelper(AudioConverterService converterService, MusicDatabaseService musicDatabaseService)
        {
            _converterService = converterService;
            _musicDatabaseService = musicDatabaseService;
        }

        /// <summary>
        /// 把一批音乐写入 USB 设备。<paramref name="format"/> 为 null 时原格式复制；
        /// 否则先转换为该格式（含标签/封面写入），失败时回退复制原文件，保证用户仍拿到音乐。
        /// </summary>
        public async Task WriteToUsb(IEnumerable<Music> musicList, UsbStorageDevice device, string? format = null, int bitRateKbps = 320)
        {
            char[] invalidChars = Path.GetInvalidFileNameChars();
            foreach (var music in musicList)
            {
                try
                {
                    string sanitizedAuthor = ToolUtils.SanitizeFileName(music.Author, invalidChars);
                    string sanitizedAlbum = ToolUtils.SanitizeFileName(music.Album, invalidChars);
                    string targetBasePath = Path.Combine(device.Path, "MUSIC", sanitizedAuthor, sanitizedAlbum);
                    Directory.CreateDirectory(targetBasePath);

                    string sanitizedFileName = ToolUtils.SanitizeFileName(Path.GetFileName(music.Path), invalidChars);
                    if (!File.Exists(music.Path))
                    {
                        _logger.LogWarning($"源文件不存在，跳过: {music.Path}");
                        continue;
                    }

                    string convertedPath = Path.Combine(targetBasePath,
                        Path.ChangeExtension(sanitizedFileName, "." + AudioConverterService.GetExtensionForFormat(format ?? music.Extension)));
                    string originalPath = Path.Combine(targetBasePath, sanitizedFileName);

                    bool converted = false;
                    if (format is not null && !music.Extension.Equals(format, StringComparison.OrdinalIgnoreCase))
                    {
                        // 直接编码到设备路径，转换 + 标签/封面在转换服务内完成
                        converted = await _converterService.ConvertForExportAsync(music, convertedPath, format, bitRateKbps);
                        if (!converted)
                            _logger.LogWarning($"转换发送失败，回退为原格式复制: {music.Path}");
                    }

                    string targetFilePath = converted ? convertedPath : originalPath;
                    if (!converted)
                    {
                        await Task.Run(() => File.Copy(music.Path, targetFilePath, true));
                    }

                    _logger.LogInformation($"已写入 {targetFilePath}");
                    await WriteLyricsFileAsync(music, targetBasePath, sanitizedFileName);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"将 {music.Path} 发送到 {device.Name} 时失败: {ex.Message}");
                }
            }
        }

        private async Task WriteLyricsFileAsync(Music music, string targetBasePath, string sanitizedFileName)
        {
            try
            {
                var (lyricsText, _, _, _) = await _musicDatabaseService.GetLyricsAsync(music.Id);
                if (string.IsNullOrEmpty(lyricsText)) return;
                string lrcFilePath = Path.Combine(targetBasePath, Path.ChangeExtension(sanitizedFileName, ".lrc"));
                await Task.Run(() => File.WriteAllText(lrcFilePath, ToolUtils.ConvertLyrics(lyricsText)));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"写入歌词文件失败: {music.Path}: {ex.Message}");
            }
        }
    }
}
