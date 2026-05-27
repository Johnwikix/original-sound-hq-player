using ManagedBass;
using ManagedBass.Enc;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using WinUIMusicPlayer.Manager;
using WinUIMusicPlayer.Model;
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

        /// <summary>
        /// 将任意音频文件转换为WAV格式
        /// </summary>
        public void ConvertToWav(Music music, string outputPath)
        {
            int stream = 0;
            try
            {
                // 创建音频流
                stream = Bass.CreateStream(music.Path, 0, 0, BassFlags.Decode | BassFlags.Float | BassFlags.AsyncFile);
                if (stream == 0)
                {
                    throw new Exception($"无法打开音频文件: {Bass.LastError}");
                }
                EncodeFlags flags = EncodeFlags.PCM;
                var originalResolution = Bass.ChannelGetInfo(stream).OriginalResolution;
                if (originalResolution == 24)
                {
                    flags = EncodeFlags.ConvertFloatTo24Bit | EncodeFlags.PCM;
                }
                else if ((originalResolution == 0 || originalResolution == 16) && !(Path.GetExtension(music.Path) == ".dsf" || Path.GetExtension(music.Path) == ".dff"))
                {
                    flags = EncodeFlags.ConvertFloatTo16BitInt | EncodeFlags.PCM;
                }
                var encoder = BassEnc.EncodeStart(stream, outputPath, flags, null);
                long length = Bass.ChannelGetLength(stream);
                long current = 0;
                //16g限制
                for (int i = 0; i <= 1024 * 1024; i++)
                {
                    var buffer = new byte[16384];
                    current += 16384;
                    var c = Bass.ChannelGetData(stream, buffer, buffer.Length);
                    if (current % 1048576 == 0)
                    {
                        progressEvent?.Invoke(this, (double)(current * 100) / length);
                    }
                    if (c <= 0) break;
                }
                BassEnc.EncodeStop(stream);
            }
            finally
            {
                if (stream != 0) Bass.StreamFree(stream);
                SaveMetaData(music, outputPath);
                progressEvent?.Invoke(this, 100);
            }
        }

        public void ConvertToMp3(Music music, string outputPath)
        {
            int stream = 0;
            try
            {
                // 创建音频流
                stream = Bass.CreateStream(music.Path, 0, 0, BassFlags.Decode | BassFlags.AsyncFile);
                if (stream == 0)
                {
                    throw new Exception($"无法打开音频文件: {Bass.LastError}");
                }
                var encoder = BassEnc_Mp3.Start(stream, " -b 320", EncodeFlags.Default, outputPath);
                long length = Bass.ChannelGetLength(stream);
                long current = 0;
                //16g限制
                for (int i = 0; i <= 1024 * 1024; i++)
                {
                    var buffer = new byte[16384];
                    current += 16384;
                    var c = Bass.ChannelGetData(stream, buffer, buffer.Length);
                    if (current % 1048576 == 0)
                    {
                        progressEvent?.Invoke(this, (double)(current * 100) / length);
                    }
                    if (c <= 0) break;
                }
                BassEnc.EncodeStop(stream);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"ConvertToMp3 转换失败: {ex.Message}");
            }
            finally
            {
                if (stream != 0)
                {
                    Bass.StreamFree(stream);
                }
                SaveMetaData(music, outputPath);
                progressEvent?.Invoke(this, 100);
            }
        }

        public void ConvertToFlac(Music music, string outputPath)
        {
            int stream = 0;
            try
            {
                // 创建音频流
                stream = Bass.CreateStream(music.Path, 0, 0, BassFlags.Decode | BassFlags.Float | BassFlags.AsyncFile);
                if (stream == 0)
                {
                    throw new Exception($"无法打开音频文件: {Bass.LastError}");
                }
                EncodeFlags flags = EncodeFlags.Default;
                var originalResolution = Bass.ChannelGetInfo(stream).OriginalResolution;
                if (originalResolution >= 24 || Path.GetExtension(music.Path) == ".dsf" || Path.GetExtension(music.Path) == ".dff")
                {
                    flags = EncodeFlags.ConvertFloatTo24Bit;
                }
                var encoder = BassEnc_Flac.Start(stream, " --best", flags, outputPath);
                long length = Bass.ChannelGetLength(stream);
                long current = 0;
                //16g限制
                for (int i = 0; i <= 1024 * 1024; i++)
                {
                    var buffer = new byte[16384];
                    current += 16384;
                    var c = Bass.ChannelGetData(stream, buffer, buffer.Length);
                    if (current % 1048576 == 0)
                    {
                        progressEvent?.Invoke(this, (double)(current * 100) / length);
                    }
                    if (c <= 0) break;
                }
                BassEnc.EncodeStop(stream);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"ConvertToFlac 转换失败: {ex.Message}");
            }
            finally
            {
                if (stream != 0)
                {
                    Bass.StreamFree(stream);
                }
                SaveMetaData(music, outputPath);
                progressEvent?.Invoke(this, 100);
            }
        }

        public void ConvertToOgg(Music music, string outputPath)
        {
            int stream = 0;
            try
            {
                // 创建音频流
                stream = Bass.CreateStream(music.Path, 0, 0, BassFlags.Decode | BassFlags.AsyncFile);
                if (stream == 0)
                {
                    throw new Exception($"无法打开音频文件: {Bass.LastError}");
                }
                var encoder = BassEnc_Ogg.Start(stream, " -b 320", EncodeFlags.Default, outputPath);
                long length = Bass.ChannelGetLength(stream);
                long current = 0;
                //16g限制
                for (int i = 0; i <= 1024 * 1024; i++)
                {
                    var buffer = new byte[16384];
                    current += 16384;
                    var c = Bass.ChannelGetData(stream, buffer, buffer.Length);
                    if (current % 1048576 == 0)
                    {
                        progressEvent?.Invoke(this, (double)(current * 100) / length);
                    }
                    if (c <= 0) break;
                }
                BassEnc.EncodeStop(stream);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"ConvertToOgg 转换失败: {ex.Message}");
            }
            finally
            {
                if (stream != 0)
                {
                    Bass.StreamFree(stream);
                }
                SaveMetaData(music, outputPath);
                progressEvent?.Invoke(this, 100);
            }
        }

        public void ConvertToOpus(Music music, string outputPath)
        {
            int stream = 0;
            try
            {
                // 创建音频流
                stream = Bass.CreateStream(music.Path, 0, 0, BassFlags.Decode | BassFlags.AsyncFile);
                if (stream == 0)
                {
                    throw new Exception($"无法打开音频文件: {Bass.LastError}");
                }
                var encoder = BassEnc_Opus.Start(stream, " --bitrate 320", EncodeFlags.Default, outputPath);
                long length = Bass.ChannelGetLength(stream);
                long current = 0;
                //16g限制
                for (int i = 0; i <= 1024 * 1024; i++)
                {
                    var buffer = new byte[16384];
                    current += 16384;
                    var c = Bass.ChannelGetData(stream, buffer, buffer.Length);
                    if (current % 1048576 == 0)
                    {
                        progressEvent?.Invoke(this, (double)(current * 100) / length);
                    }
                    if (c <= 0) break;
                }
                BassEnc.EncodeStop(stream);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"ConvertToOpus 转换失败: {ex.Message}");
            }
            finally
            {
                if (stream != 0)
                {
                    Bass.StreamFree(stream);
                }
                SaveMetaData(music, outputPath);
                progressEvent?.Invoke(this, 100);
            }
        }

        private void SaveMetaData(Music music, string outputPath)
        {
            try
            {
                byte[] pic = ToolUtils.GetRawImage(music).Result;
                ToolUtils.SaveMetaData(music,outputPath,pic).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"SaveMetaData 保存元数据失败: {ex.Message}");
            }
        }
    }
}
