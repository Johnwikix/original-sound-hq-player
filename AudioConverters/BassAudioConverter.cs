using ManagedBass;
using ManagedBass.Enc;
using System;
using System.Diagnostics;
using System.IO;

using WinUIMusicPlayer.Manager;

namespace WinUIMusicPlayer.AudioConverters
{
    public class BassAudioConverter
    {
        public EventHandler<double>? progressEvent;
        public BassAudioConverter()
        {
            BassManager.Initialize();
        }

        /// <summary>
        /// 将任意音频文件转换为WAV格式
        /// </summary>
        public void ConvertToWav(string inputPath, string outputPath)
        {
            int stream = 0;
            try
            {
                // 创建音频流
                stream = Bass.CreateStream(inputPath, 0, 0, BassFlags.Decode | BassFlags.Float | BassFlags.AsyncFile);
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
                else if ((originalResolution == 0 || originalResolution == 16) && !(Path.GetExtension(inputPath) == ".dsf" || Path.GetExtension(inputPath) == ".dff"))
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
                SaveMetaData(inputPath, outputPath);
                progressEvent?.Invoke(this, 100);
            }
        }

        public void ConvertToMp3(string inputPath, string outputPath)
        {
            int stream = 0;
            try
            {
                // 创建音频流
                stream = Bass.CreateStream(inputPath, 0, 0, BassFlags.Decode | BassFlags.AsyncFile);
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
                Debug.WriteLine($"发生错误：{ex.Message}");
            }
            finally
            {
                if (stream != 0)
                {
                    Bass.StreamFree(stream);
                }
                SaveMetaData(inputPath, outputPath);
                progressEvent?.Invoke(this, 100);
            }
        }

        public void ConvertToFlac(string inputPath, string outputPath)
        {
            int stream = 0;
            try
            {
                // 创建音频流
                stream = Bass.CreateStream(inputPath, 0, 0, BassFlags.Decode | BassFlags.Float | BassFlags.AsyncFile);
                if (stream == 0)
                {
                    throw new Exception($"无法打开音频文件: {Bass.LastError}");
                }
                EncodeFlags flags = EncodeFlags.Default;
                var originalResolution = Bass.ChannelGetInfo(stream).OriginalResolution;
                if (originalResolution >= 24 || Path.GetExtension(inputPath) == ".dsf" || Path.GetExtension(inputPath) == ".dff")
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
                Debug.WriteLine($"发生错误：{ex.Message}");
            }
            finally
            {
                if (stream != 0)
                {
                    Bass.StreamFree(stream);
                }
                SaveMetaData(inputPath, outputPath);
                progressEvent?.Invoke(this, 100);
            }
        }

        public void ConvertToOgg(string inputPath, string outputPath)
        {
            int stream = 0;
            try
            {
                // 创建音频流
                stream = Bass.CreateStream(inputPath, 0, 0, BassFlags.Decode | BassFlags.AsyncFile);
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
                Debug.WriteLine($"发生错误：{ex.Message}");
            }
            finally
            {
                if (stream != 0)
                {
                    Bass.StreamFree(stream);
                }
                SaveMetaData(inputPath, outputPath);
                progressEvent?.Invoke(this, 100);
            }
        }

        public void ConvertToOpus(string inputPath, string outputPath)
        {
            int stream = 0;
            try
            {
                // 创建音频流
                stream = Bass.CreateStream(inputPath, 0, 0, BassFlags.Decode | BassFlags.AsyncFile);
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
                Debug.WriteLine($"发生错误：{ex.Message}");
            }
            finally
            {
                if (stream != 0)
                {
                    Bass.StreamFree(stream);
                }
                SaveMetaData(inputPath, outputPath);
                progressEvent?.Invoke(this, 100);
            }
        }

        private void SaveMetaData(string inputFile, string outputPath)
        {
            try
            {
                using (var originalFile = TagLib.File.Create(inputFile))
                {
                    using (var newFile = TagLib.File.Create(outputPath))
                    {
                        newFile.Tag.Title = originalFile.Tag.Title;
                        newFile.Tag.Performers = originalFile.Tag.Performers;
                        newFile.Tag.Album = originalFile.Tag.Album;
                        newFile.Tag.Year = originalFile.Tag.Year;
                        newFile.Tag.Track = originalFile.Tag.Track;
                        if (originalFile.Tag.Pictures.Length > 0)
                        {
                            var picture = originalFile.Tag.Pictures[0];
                            newFile.Tag.Pictures = new[] { picture };
                        }

                        newFile.Save();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"写入元信息和封面时出错: {ex.Message}");
            }
        }
    }
}
