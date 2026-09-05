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
    /// 不做任何 UI 或数据层操作——进度经 <see cref="WriteToUsb"/> 的聚合回调上报，
    /// 入库由扫描/发送流程负责。
    /// </summary>
    public class UsbWriterHelper
    {
        private const int CopyBufferSize = 1024 * 1024; // 1MB 流式复制缓冲
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
        /// <paramref name="progress"/> 收到整批聚合百分比（0-100）；
        /// <paramref name="nextFileBase"/> 在每个文件完成后调用，返回该文件的完成基准百分比
        /// （= 已完成文件数/总数），用于跨批次的进度重置与聚合。
        /// </summary>
        public async Task WriteToUsb(IList<Music> musicList, UsbStorageDevice device, string? format = null,
            int bitRateKbps = 320, IProgress<double>? progress = null, Func<double>? nextFileBase = null)
        {
            char[] invalidChars = Path.GetInvalidFileNameChars();
            double basePercent = 0;
            double currentFilePercent = 0;
            // 转换服务的进度事件（0-100）映射为当前文件的内部进度参与聚合
            void OnConverterProgress(object? _, double p)
            {
                currentFilePercent = p;
                progress?.Report(basePercent + currentFilePercent / musicList.Count);
            }
            _converterService.updateProgress += OnConverterProgress;
            try
            {
                foreach (var music in musicList)
                {
                    currentFilePercent = 0;
                    progress?.Report(basePercent);
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
                            await CopyWithProgressAsync(music.Path, targetFilePath,
                                f => progress?.Report(basePercent + f / musicList.Count));
                        }

                        _logger.LogInformation($"已写入 {targetFilePath}");
                        await WriteLyricsFileAsync(music, targetBasePath, sanitizedFileName);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"将 {music.Path} 发送到 {device.Name} 时失败: {ex.Message}");
                    }
                    finally
                    {
                        basePercent = nextFileBase?.Invoke() ?? (basePercent + 100.0 / musicList.Count);
                    }
                }
            }
            finally
            {
                _converterService.updateProgress -= OnConverterProgress;
            }
        }

        /// <summary>流式复制并按字节进度回调（0-100）。</summary>
        private static async Task CopyWithProgressAsync(string source, string destination, Action<double> fileProgress)
        {
            await Task.Run(async () =>
            {
                long total = new FileInfo(source).Length;
                using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, FileOptions.SequentialScan);
                using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferSize);
                var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(CopyBufferSize);
                try
                {
                    long copied = 0;
                    int read;
                    while ((read = await input.ReadAsync(buffer.AsMemory(0, CopyBufferSize))) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, read));
                        copied += read;
                        fileProgress(total > 0 ? copied * 100.0 / total : 100);
                    }
                    fileProgress(100);
                }
                finally
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
                }
            });
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
