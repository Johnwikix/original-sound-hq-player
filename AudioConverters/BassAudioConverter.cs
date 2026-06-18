using ManagedBass;
using ManagedBass.Enc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Buffers;
using System.IO;
using System.Threading.Tasks;
using WinUIMusicPlayer.Manager;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.AudioConverters
{
    public class BassAudioConverter
    {
        private static ILogger<BassAudioConverter> _logger = App.GetLogger<BassAudioConverter>();

        public EventHandler<double>? progressEvent;
        public BassAudioConverter()
        {
            BassManager.Initialize();
        }

        public Task ConvertToWav(Music music, string outputPath)
        {
            return ConvertCore(music, outputPath, BassFlags.Decode | BassFlags.Float | BassFlags.AsyncFile, (stream, outPath) =>
            {
                EncodeFlags flags = EncodeFlags.PCM;
                var originalResolution = Bass.ChannelGetInfo(stream).OriginalResolution;
                if (originalResolution == 24)
                {
                    flags = EncodeFlags.ConvertFloatTo24Bit | EncodeFlags.PCM;
                }
                else if ((originalResolution == 0 || originalResolution == 16) &&
                         !(Path.GetExtension(music.Path) == ".dsf" || Path.GetExtension(music.Path) == ".dff"))
                {
                    flags = EncodeFlags.ConvertFloatTo16BitInt | EncodeFlags.PCM;
                }
                return BassEnc.EncodeStart(stream, outPath, flags, null);
            });
        }

        public Task ConvertToMp3(Music music, string outputPath)
        {
            return ConvertCore(music, outputPath, BassFlags.Decode | BassFlags.AsyncFile,
                (stream, outPath) => BassEnc_Mp3.Start(stream, " -b 320", EncodeFlags.Default, outPath));
        }

        public Task ConvertToFlac(Music music, string outputPath)
        {
            return ConvertCore(music, outputPath, BassFlags.Decode | BassFlags.Float | BassFlags.AsyncFile, (stream, outPath) =>
            {
                EncodeFlags flags = EncodeFlags.Default;
                var originalResolution = Bass.ChannelGetInfo(stream).OriginalResolution;
                if (originalResolution >= 24 ||
                    Path.GetExtension(music.Path) == ".dsf" || Path.GetExtension(music.Path) == ".dff")
                {
                    flags = EncodeFlags.ConvertFloatTo24Bit;
                }
                return BassEnc_Flac.Start(stream, " --best", flags, outPath);
            });
        }

        public Task ConvertToOgg(Music music, string outputPath)
        {
            return ConvertCore(music, outputPath, BassFlags.Decode | BassFlags.AsyncFile,
                (stream, outPath) => BassEnc_Ogg.Start(stream, " -b 320", EncodeFlags.Default, outPath));
        }

        public Task ConvertToOpus(Music music, string outputPath)
        {
            return ConvertCore(music, outputPath, BassFlags.Decode | BassFlags.AsyncFile,
                (stream, outPath) => BassEnc_Opus.Start(stream, " --bitrate 320", EncodeFlags.Default, outPath));
        }

        private async Task ConvertCore(Music music, string outputPath, BassFlags streamFlags, Func<int, string, int> startEncoder)
        {
            int stream = 0;
            try
            {
                stream = Bass.CreateStream(music.Path, 0, 0, streamFlags);
                if (stream == 0)
                    throw new Exception($"无法打开音频文件: {Bass.LastError}");

                startEncoder(stream, outputPath);
                PumpStream(stream);
                BassEnc.EncodeStop(stream);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"转换失败: {ex.Message}");
            }
            finally
            {
                if (stream != 0) Bass.StreamFree(stream);
                await SaveMetaDataAsync(music, outputPath);
                progressEvent?.Invoke(this, 100);
            }
        }

        private void PumpStream(int stream)
        {
            long length = Bass.ChannelGetLength(stream);
            long current = 0;
            var buffer = ArrayPool<byte>.Shared.Rent(16384);
            try
            {
                for (int i = 0; i <= 1024 * 1024; i++)
                {
                    current += 16384;
                    var c = Bass.ChannelGetData(stream, buffer, 16384);
                    if (current % 1048576 == 0)
                    {
                        progressEvent?.Invoke(this, (double)(current * 100) / length);
                    }
                    if (c <= 0) break;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
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
        }
    }
}
