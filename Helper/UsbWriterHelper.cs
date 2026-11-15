using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Threading;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;

namespace WinUIMusicPlayer.Helper
{
    /// <summary>
    /// USB 传输的纯 IO 层：按目标设备目录结构（MUSIC\歌手\专辑）写入音频与歌词，
    /// 支持发送前转换（磁盘直接编码，MTP 经本地暂存，失败回退为原格式复制）。
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
        /// <paramref name="onFileCopied"/> 在每首音频实际复制成功、歌词发送之前等待执行，
        /// 用于逐曲发布状态；返回值分别记录音频成功与音频、歌词、状态失败。
        /// </summary>
        public async Task<UsbExportResult> WriteToUsb(IList<Music> musicList, UsbStorageDevice device, string? format = null,
            int bitRateKbps = 320, IProgress<double>? progress = null, Func<double>? nextFileBase = null, CancellationToken cancellationToken = default,
            Func<Music, string, Task>? onFileCopied = null)
        {
            var result = new UsbExportResult(musicList.Count);
            double basePercent = 0;
            double currentFilePercent = 0;
            // 转换服务的进度事件（0-100）映射为当前文件的内部进度参与聚合
            void OnConverterProgress(object? _, double p)
            {
                currentFilePercent = p;
                // MTP 转码完成后还要传输，保留最后 20% 给设备复制。
                progress?.Report(basePercent + currentFilePercent * (device.IsPortable ? 0.8 : 1) / musicList.Count);
            }
            _converterService.updateProgress += OnConverterProgress;
            try
            {
                foreach (var music in musicList)
                {
                    // 原生转换没有取消协议：等待本文件真正结束后停止下一项，保留已成功的台账。
                    if (cancellationToken.IsCancellationRequested) break;
                    currentFilePercent = 0;
                    progress?.Report(basePercent);
                    string? stagingPath = null;
                    bool audioCopied = false;
                    try
                    {
                        if (!File.Exists(music.Path))
                        {
                            _logger.LogWarning($"源文件不存在，跳过: {music.Path}");
                            result.AddFailure(music, UsbExportFailureStage.Audio, new FileNotFoundException(null, music.Path).Message);
                            continue;
                        }
                        string sanitizedAuthor = UsbExportPaths.GetFolderName(music.Author);
                        string sanitizedAlbum = UsbExportPaths.GetFolderName(music.Album);
                        using var portableWriter = device.IsPortable
                            ? await UsbPortableStorageWriter.OpenAsync(device, sanitizedAuthor, sanitizedAlbum, cancellationToken)
                            : null;
                        if (device.IsPortable)
                            stagingPath = Directory.CreateTempSubdirectory("OriginalSound-UsbExport-").FullName;
                        string targetBasePath = stagingPath ?? Path.Combine(device.Path, "MUSIC", sanitizedAuthor, sanitizedAlbum);
                        Directory.CreateDirectory(targetBasePath);

                        string sanitizedFileName = UsbExportPaths.GetFileName(Path.GetFileName(music.Path));
                        string convertedPath = Path.Combine(targetBasePath,
                            Path.ChangeExtension(sanitizedFileName, "." + AudioConverterService.GetExtensionForFormat(format ?? music.Extension)));
                        string originalPath = Path.Combine(targetBasePath, sanitizedFileName);

                        bool converted = false;
                        if (format is not null && !music.Extension.Equals(format, StringComparison.OrdinalIgnoreCase))
                        {
                            // MTP 编码到暂存目录；标签/封面仍由同一转换服务完成。
                            converted = await _converterService.ConvertForExportAsync(music, convertedPath, format, bitRateKbps);
                            if (!converted)
                                _logger.LogWarning($"转换发送失败，回退为原格式复制: {music.Path}");
                        }

                        string targetFilePath = converted ? convertedPath : originalPath;
                        if (!converted)
                        {
                            if (device.IsPortable)
                                targetFilePath = music.Path;
                            else
                                await CopyWithProgressAsync(music.Path, targetFilePath,
                                    f => progress?.Report(basePercent + f / musicList.Count));
                        }

                        if (portableWriter is not null)
                            await portableWriter.CopyAsync(targetFilePath);

                        _logger.LogInformation("已发送 {File} 到 {Device}", music.Path, device.Name);
                        string extension = Path.GetExtension(targetFilePath).TrimStart('.');
                        result.AddSuccess(music, extension);
                        audioCopied = true;
                        // 回调仅发布状态，不能重入 MTP 门；持久化由协调器在整批 I/O 结束后完成。
                        if (onFileCopied is not null)
                        {
                            try { await onFileCopied(music, extension); }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "发布 USB 发送状态失败: {File}", music.Path);
                                result.AddFailure(music, UsbExportFailureStage.Status, ex.Message);
                            }
                        }
                        string? lyricsError = await WriteLyricsFileAsync(music, targetBasePath, sanitizedFileName);
                        if (lyricsError is not null)
                            result.AddFailure(music, UsbExportFailureStage.Lyrics, lyricsError);
                        if (portableWriter is not null)
                        {
                            if (lyricsError is null)
                                foreach (string lyricsPath in Directory.EnumerateFiles(targetBasePath, "*.lrc"))
                                    await portableWriter.CopyAsync(lyricsPath);
                            progress?.Report(basePercent + 100.0 / musicList.Count);
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"将 {music.Path} 发送到 {device.Name} 时失败: {ex.Message}");
                        result.AddFailure(music, audioCopied ? UsbExportFailureStage.Lyrics : UsbExportFailureStage.Audio, ex.Message);
                        // MTP 会话已失效时停止整批，避免每首重复等待驱动超时。
                        if (device.IsPortable && IsDeviceUnavailable(ex)) break;
                    }
                    finally
                    {
                        if (stagingPath is not null)
                        {
                            try { Directory.Delete(stagingPath, recursive: true); }
                            catch (Exception ex) { _logger.LogWarning(ex, "清理 USB 导出临时文件失败: {Path}", stagingPath); }
                        }
                        basePercent = nextFileBase?.Invoke() ?? (basePercent + 100.0 / musicList.Count);
                    }
                }
            }
            finally
            {
                _converterService.updateProgress -= OnConverterProgress;
            }
            return result;
        }

        private static bool IsDeviceUnavailable(Exception error)
        {
            for (Exception? current = error; current is not null; current = current.InnerException)
                if (current is DirectoryNotFoundException || current.HResult == unchecked((int)0x802A0006)) return true;
            return false;
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

        private async Task<string?> WriteLyricsFileAsync(Music music, string targetBasePath, string sanitizedFileName)
        {
            try
            {
                var document = await _musicDatabaseService.GetLyricsDocumentAsync(music);
                string lyricsText = new WinUIMusicPlayer.Services.Lyrics.LyricsParser().ExportLrc(document);
                if (string.IsNullOrEmpty(lyricsText)) return null;
                string lrcFilePath = Path.Combine(targetBasePath, Path.ChangeExtension(sanitizedFileName, ".lrc"));
                await WinUIMusicPlayer.Services.Lyrics.LyricsExporter.SaveFilesAsync(lrcFilePath,
                    document with { Original = new(lyricsText, LyricsFormat.Lrc) }, CancellationToken.None);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"写入歌词文件失败: {music.Path}: {ex.Message}");
                return ex.Message;
            }
        }
    }
}
