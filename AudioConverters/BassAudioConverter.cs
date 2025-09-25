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
            //var version = BassEnc.Version;
            //var mp3Version = BassEnc_Mp3.Version;
            //Debug.WriteLine($"BassEnc: {version},{mp3Version}");
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
                else if ((originalResolution == 0 || originalResolution ==16) && !(Path.GetExtension(inputPath) == ".dsf" || Path.GetExtension(inputPath) == ".dff")) {
                    flags = EncodeFlags.ConvertFloatTo16BitInt | EncodeFlags.PCM;
                }
                var encoder = BassEnc.EncodeStart(stream, outputPath, flags, null);
                long length = Bass.ChannelGetLength(stream);
                long current = 0;
                while (true)
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
                if (stream != 0 ) Bass.StreamFree(stream);
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
                var encoder = BassEnc_Mp3.Start(stream, "lame -b 320", EncodeFlags.Default, outputPath);
                long length = Bass.ChannelGetLength(stream);
                long current = 0;
                while (true)
                {
                    var buffer = new byte[16384];
                    current += 16384;
                    var c = Bass.ChannelGetData(stream, buffer, buffer.Length);
                    if (current % 1048576 == 0) {
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
                while (true)
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

        ///// <summary>
        ///// 从内存中的音频数据转换为WAV
        ///// </summary>
        ///// <param name="audioData">音频数据</param>
        ///// <param name="outputPath">输出WAV文件路径</param>
        //public void ConvertFromMemoryToWav(byte[] audioData, string outputPath)
        //{
        //    int stream = 0;

        //    try
        //    {
        //        // 从内存创建音频流
        //        stream = Bass.CreateStream(audioData, 0, audioData.Length, BassFlags.Decode | BassFlags.Float);
        //        if (stream == 0)
        //        {
        //            throw new Exception($"无法从内存创建音频流: {Bass.LastError}");
        //        }

        //        // 获取音频信息
        //        var channelInfo = Bass.ChannelGetInfo(stream);
        //        var waveFormat = WaveFormat.CreateIeeeFloat((int)channelInfo.Frequency, channelInfo.Channels);

        //        // 转换
        //        using (var fileStream = new FileStream(outputPath, FileMode.Create))
        //        using (var waveWriter = new WaveFileWriter(fileStream, waveFormat))
        //        {
        //            ConvertStreamToWav(stream, waveWriter);
        //        }

        //        Console.WriteLine($"从内存转换完成: {outputPath}");
        //    }
        //    finally
        //    {
        //        if (stream != 0) Bass.StreamFree(stream);
        //    }
        //}

        ///// <summary>
        ///// 将Bass音频流转换写入WaveFileWriter
        ///// </summary>
        //private void ConvertStreamToWav(int stream, WaveFileWriter waveWriter)
        //{
        //    const int bufferSize = 4096; // 4KB缓冲区
        //    float[] buffer = new float[bufferSize / 4]; // float是4字节
        //    int bytesRead;

        //    // 获取流的总长度用于进度显示
        //    long totalLength = Bass.ChannelGetLength(stream);
        //    long processedBytes = 0;

        //    Console.WriteLine("开始转换音频数据...");

        //    while ((bytesRead = Bass.ChannelGetData(stream, buffer, bufferSize)) > 0)
        //    {
        //        // 写入浮点数据
        //        if (!waveWriter.Write(buffer, bytesRead))
        //        {
        //            throw new Exception("写入WAV数据失败");
        //        }

        //        processedBytes += bytesRead;

        //        // 显示进度
        //        if (totalLength > 0)
        //        {
        //            double progress = (double)processedBytes / totalLength * 100;
        //            Console.Write($"\r进度: {progress:F1}%");
        //        }
        //    }

        //    Console.WriteLine("\n音频数据转换完成");
        //}

        ///// <summary>
        ///// 批量转换音频文件
        ///// </summary>
        ///// <param name="inputFolder">输入文件夹</param>
        ///// <param name="outputFolder">输出文件夹</param>
        ///// <param name="searchPattern">文件搜索模式</param>
        //public void BatchConvert(string inputFolder, string outputFolder, string searchPattern = "*.*")
        //{
        //    if (!Directory.Exists(outputFolder))
        //    {
        //        Directory.CreateDirectory(outputFolder);
        //    }

        //    string[] supportedExtensions = { ".mp3", ".flac", ".ogg", ".wav", ".aac", ".wma", ".m4a" };
        //    var files = Directory.GetFiles(inputFolder, searchPattern, SearchOption.TopDirectoryOnly);

        //    int converted = 0;
        //    foreach (string file in files)
        //    {
        //        string extension = Path.GetExtension(file).ToLower();
        //        if (Array.Exists(supportedExtensions, ext => ext == extension))
        //        {
        //            string outputFile = Path.Combine(outputFolder,
        //                Path.GetFileNameWithoutExtension(file) + ".wav");

        //            try
        //            {
        //                Console.WriteLine($"转换: {Path.GetFileName(file)}");
        //                ConvertToWav(file, outputFile);
        //                converted++;
        //            }
        //            catch (Exception ex)
        //            {
        //                Console.WriteLine($"转换失败 {file}: {ex.Message}");
        //            }
        //        }
        //    }

        //    Console.WriteLine($"批量转换完成，共转换 {converted} 个文件");
        //}

        ///// <summary>
        ///// 获取音频文件信息
        ///// </summary>
        ///// <param name="filePath">音频文件路径</param>
        //public void GetAudioInfo(string filePath)
        //{
        //    int stream = Bass.CreateStream(filePath, 0, 0, BassFlags.Decode);
        //    if (stream == 0)
        //    {
        //        Console.WriteLine($"无法打开文件: {Bass.LastError}");
        //        return;
        //    }

        //    try
        //    {
        //        var info = Bass.ChannelGetInfo(stream);
        //        long length = Bass.ChannelGetLength(stream);
        //        double duration = Bass.ChannelBytes2Seconds(stream, length);

        //        Console.WriteLine($"音频文件信息: {Path.GetFileName(filePath)}");
        //        Console.WriteLine($"  采样率: {info.Frequency}Hz");
        //        Console.WriteLine($"  声道数: {info.Channels}");
        //        Console.WriteLine($"  时长: {TimeSpan.FromSeconds(duration):mm\\:ss}");
        //        Console.WriteLine($"  比特率: {(length * 8 / duration / 1000):F0} kbps");
        //    }
        //    finally
        //    {
        //        Bass.StreamFree(stream);
        //    }
        //}

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
