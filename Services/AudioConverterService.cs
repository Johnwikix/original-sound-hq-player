using BassPlayerIpc.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using WinUIMusicPlayer.AudioConverters;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.Services
{
    /// <summary>
    /// 音频转换服务：解码/编码由进程内的 FFmpeg DLL（FFmpegAudioConverter）完成，
    /// 标签、歌词和封面随编码写入。CUE 的 WAV 产物使用 ID3v2 保留 Unicode 标签和封面。
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
        /// 供 USB 等导出场景生成独立文件，全程持有写入门。
        /// 失败返回 false，进度事件保证达 100。
        /// </summary>
        public async Task<bool> ConvertForExportAsync(Music music, string outputPath, string format, int bitRateKbps = 320,
            bool overwrite = true, Action<double>? reportProgress = null)
        {
            reportProgress ??= OnProgressChanged;
            if (music.IsRemote) { reportProgress(100); return false; }
            var range = music.IsCueTrack ? new AudioSampleRange(music.StartSample, music.EndSample, music.SampleRate) : (AudioSampleRange?)null;
            var input = new AudioInput(music.Path, range);
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

                        var converter = new FFmpegAudioConverter();
                        converter.progressEvent += (_, progress) => reportProgress(progress);
                        if (range is null)
                        {
                            converter.Convert(input, outputPath, format, dsdFreq, dsdGain, bitRateKbps, meta);
                            return true;
                        }

                        if (string.Equals(Path.GetFullPath(input.Path), Path.GetFullPath(outputPath), StringComparison.OrdinalIgnoreCase))
                            throw new IOException("转换目标与 CUE 音源相同。");
                        string staged = Path.Combine(Path.GetDirectoryName(outputPath) ?? "", $".{Guid.NewGuid():N}.tmp");
                        try
                        {
                            converter.Convert(input, staged, format, bitRateKbps: bitRateKbps, metadata: meta);
                            if (format == "wav" && meta is not null) WriteWaveTags(staged, meta);
                            File.Move(staged, outputPath, overwrite);
                            return true;
                        }
                        finally { DeleteTemporaryFile(staged); }
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"ConvertForExportAsync 导出转换失败: {ex.Message}");
                    reportProgress(100);
                    return false;
                }
            }
        }

        /// <summary>转换前取齐歌词与封面，构造内联元数据。</summary>
        private async Task<ConversionMetadata?> BuildConversionMetadataAsync(Music music)
        {
            string? lyrics = null;
            try
            {
                var document = await App.Services.GetRequiredService<MusicDatabaseService>().GetLyricsDocumentAsync(music);
                lyrics = new WinUIMusicPlayer.Services.Lyrics.LyricsParser().ExportLrc(document);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "转换前读取歌词失败: {Path}", music.Path);
                if (!music.IsCueTrack) return null;
            }

            byte[]? cover = null;
            try { cover = await Task.Run(() => ToolUtils.GetRawImage(music)).ConfigureAwait(false); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "转换前读取封面失败: {Path}", music.Path);
                if (!music.IsCueTrack) return null;
            }
            return new ConversionMetadata
            {
                Title = music.Title,
                Artist = music.Author,
                Album = music.Album,
                TrackNumber = music.IsCueTrack && music.TrackNumber <= 0 ? music.CueTrackNo : music.TrackNumber,
                DiscNumber = music.DiskNumber,
                Year = music.Year,
                CoverBytes = cover is { Length: > 0 } ? cover : null,
                Lyrics = lyrics,
            };
        }

        private static void WriteWaveTags(string path, ConversionMetadata meta)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var track = new ATL.Track(stream, ".wav")
            {
                Title = meta.Title,
                Artist = meta.Artist,
                Album = meta.Album,
                TrackNumber = meta.TrackNumber,
                DiscNumber = meta.DiscNumber,
                Year = meta.Year,
            };
            if (meta.CoverBytes is { Length: > 0 } cover)
                track.EmbeddedPictures.Add(ATL.PictureInfo.fromBinaryData(cover, ATL.PictureInfo.PIC_TYPE.Front));
            if (!track.Save(ATL.AudioData.MetaDataIOFactory.TagType.ID3V2)) throw new IOException("无法写入 CUE 音轨的 WAV 标签。");
        }

        private AppViewModel AppViewModel { get; }
        private readonly ILogger<AudioConverterService> _logger;

        public AudioConverterService(AppViewModel appViewModel, ILogger<AudioConverterService> logger)
        {
            AppViewModel = appViewModel;
            _logger = logger;
        }

        public async Task<bool> ConvertAudioAsync(Music music, string type = "wav", int bitRateKbps = 320, Action? onCommitted = null)
        {
            if (music.IsRemote) { OnProgressChanged(100); return false; }
            string format = type.ToLowerInvariant();
            if (!music.IsCueTrack && music.Extension.Equals(format, StringComparison.OrdinalIgnoreCase))
                return true;
            string outputPath = GenerateOutputPath(music, format);
            // FFmpeg selects its muxer from format. A non-audio suffix keeps unfinished data out of the library.
            string temporaryPath = Path.Combine(Path.GetDirectoryName(outputPath) ?? "", $".{Guid.NewGuid():N}.tmp");
            try
            {
                if (!await ConvertForExportAsync(music, temporaryPath, format, bitRateKbps).ConfigureAwait(false))
                    return false;
                await App.Services.GetRequiredService<MusicDatabaseService>()
                    .InstallConvertedFileAsync(temporaryPath, outputPath, onCommitted, overwrite: !music.IsCueTrack).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "转换产物入库失败: {Path}", outputPath);
                return false;
            }
            finally { DeleteTemporaryFile(temporaryPath); }
        }

        private void DeleteTemporaryFile(string path)
        {
            try { File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "清理转换临时文件失败: {Path}", path);
            }
        }

        private static string GenerateOutputPath(Music music, string format)
        {
            string directory = Path.GetDirectoryName(music.Path) ?? "";
            string extension = GetExtensionForFormat(format);
            if (!music.IsCueTrack)
                return Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(music.Path)}_output.{extension}");

            string stem = UsbExportPaths.GetCueTrackFileStem(music);
            string path = Path.Combine(directory, $"{stem}.{extension}");
            for (int suffix = 2; File.Exists(path) || Directory.Exists(path); suffix++)
                path = Path.Combine(directory, FormattableString.Invariant($"{stem} ({suffix}).{extension}"));
            return path;
        }

        private void OnProgressChanged(double progress)
        {
            updateProgress?.Invoke(this, progress);
        }
    }
}
