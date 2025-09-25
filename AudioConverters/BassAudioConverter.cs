using ManagedBass;
using ManagedBass.Enc;
using ManagedBass.Fx;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using WinUIMusicPlayer.Manager;

namespace WinUIMusicPlayer.AudioConverters
{
    public class BassAudioConverter
    {
        public EventHandler<double>? progressEvent;
        public BassAudioConverter()
        {
            BassManager.Initialize();
            var version = BassEnc.Version;
            Debug.WriteLine($"BassEnc: {version}");
        }       

        /// <summary>
        /// 将任意音频文件转换为WAV格式
        /// </summary>
        /// <param name="inputPath">输入音频文件路径</param>
        /// <param name="outputPath">输出WAV文件路径</param>
        /// <param name="targetSampleRate">目标采样率（0表示使用原始采样率）</param>
        /// <param name="targetChannels">目标声道数（0表示使用原始声道数）</param>
        public void ConvertToWav(string inputPath, string outputPath)
        {
            int stream = 0;
            try
            {
                // 创建音频流
                stream = Bass.CreateStream(inputPath, 0, 0, BassFlags.Decode | BassFlags.Float);
                if (stream == 0)
                {
                    throw new Exception($"无法打开音频文件: {Bass.LastError}");
                }
                // 获取原始音频信息
                var channelInfo = Bass.ChannelGetInfo(stream);
                int originalSampleRate = (int)channelInfo.Frequency;
                int originalChannels = channelInfo.Channels;
                // 创建WaveFormat对象
                var waveFormat = WaveFormat.CreateIeeeFloat(originalSampleRate, originalChannels);
                // 开始转换
                using (var fileStream = new FileStream(outputPath, FileMode.Create))
                using (var waveWriter = new WaveFileWriter(fileStream, waveFormat))
                {
                    ConvertStreamToWav(stream, waveWriter);
                }
            }
            finally
            {
                if (stream != 0 ) Bass.StreamFree(stream);
                progressEvent?.Invoke(this, 100);
            }
        }

        /// <summary>
        /// 从内存中的音频数据转换为WAV
        /// </summary>
        /// <param name="audioData">音频数据</param>
        /// <param name="outputPath">输出WAV文件路径</param>
        public void ConvertFromMemoryToWav(byte[] audioData, string outputPath)
        {
            int stream = 0;

            try
            {
                // 从内存创建音频流
                stream = Bass.CreateStream(audioData, 0, audioData.Length, BassFlags.Decode | BassFlags.Float);
                if (stream == 0)
                {
                    throw new Exception($"无法从内存创建音频流: {Bass.LastError}");
                }

                // 获取音频信息
                var channelInfo = Bass.ChannelGetInfo(stream);
                var waveFormat = WaveFormat.CreateIeeeFloat((int)channelInfo.Frequency, channelInfo.Channels);

                // 转换
                using (var fileStream = new FileStream(outputPath, FileMode.Create))
                using (var waveWriter = new WaveFileWriter(fileStream, waveFormat))
                {
                    ConvertStreamToWav(stream, waveWriter);
                }

                Console.WriteLine($"从内存转换完成: {outputPath}");
            }
            finally
            {
                if (stream != 0) Bass.StreamFree(stream);
            }
        }

        /// <summary>
        /// 将Bass音频流转换写入WaveFileWriter
        /// </summary>
        private void ConvertStreamToWav(int stream, WaveFileWriter waveWriter)
        {
            const int bufferSize = 4096; // 4KB缓冲区
            float[] buffer = new float[bufferSize / 4]; // float是4字节
            int bytesRead;

            // 获取流的总长度用于进度显示
            long totalLength = Bass.ChannelGetLength(stream);
            long processedBytes = 0;

            Console.WriteLine("开始转换音频数据...");

            while ((bytesRead = Bass.ChannelGetData(stream, buffer, bufferSize)) > 0)
            {
                // 写入浮点数据
                if (!waveWriter.Write(buffer, bytesRead))
                {
                    throw new Exception("写入WAV数据失败");
                }

                processedBytes += bytesRead;

                // 显示进度
                if (totalLength > 0)
                {
                    double progress = (double)processedBytes / totalLength * 100;
                    Console.Write($"\r进度: {progress:F1}%");
                }
            }

            Console.WriteLine("\n音频数据转换完成");
        }

        /// <summary>
        /// 批量转换音频文件
        /// </summary>
        /// <param name="inputFolder">输入文件夹</param>
        /// <param name="outputFolder">输出文件夹</param>
        /// <param name="searchPattern">文件搜索模式</param>
        public void BatchConvert(string inputFolder, string outputFolder, string searchPattern = "*.*")
        {
            if (!Directory.Exists(outputFolder))
            {
                Directory.CreateDirectory(outputFolder);
            }

            string[] supportedExtensions = { ".mp3", ".flac", ".ogg", ".wav", ".aac", ".wma", ".m4a" };
            var files = Directory.GetFiles(inputFolder, searchPattern, SearchOption.TopDirectoryOnly);

            int converted = 0;
            foreach (string file in files)
            {
                string extension = Path.GetExtension(file).ToLower();
                if (Array.Exists(supportedExtensions, ext => ext == extension))
                {
                    string outputFile = Path.Combine(outputFolder,
                        Path.GetFileNameWithoutExtension(file) + ".wav");

                    try
                    {
                        Console.WriteLine($"转换: {Path.GetFileName(file)}");
                        ConvertToWav(file, outputFile);
                        converted++;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"转换失败 {file}: {ex.Message}");
                    }
                }
            }

            Console.WriteLine($"批量转换完成，共转换 {converted} 个文件");
        }

        /// <summary>
        /// 获取音频文件信息
        /// </summary>
        /// <param name="filePath">音频文件路径</param>
        public void GetAudioInfo(string filePath)
        {
            int stream = Bass.CreateStream(filePath, 0, 0, BassFlags.Decode);
            if (stream == 0)
            {
                Console.WriteLine($"无法打开文件: {Bass.LastError}");
                return;
            }

            try
            {
                var info = Bass.ChannelGetInfo(stream);
                long length = Bass.ChannelGetLength(stream);
                double duration = Bass.ChannelBytes2Seconds(stream, length);

                Console.WriteLine($"音频文件信息: {Path.GetFileName(filePath)}");
                Console.WriteLine($"  采样率: {info.Frequency}Hz");
                Console.WriteLine($"  声道数: {info.Channels}");
                Console.WriteLine($"  时长: {TimeSpan.FromSeconds(duration):mm\\:ss}");
                Console.WriteLine($"  比特率: {(length * 8 / duration / 1000):F0} kbps");
            }
            finally
            {
                Bass.StreamFree(stream);
            }
        }
    }
}
