using CSCore;
using CSCore.Ffmpeg;
using CUETools.Codecs.FLAKE;
using NAudio.Flac;
using NAudio.Lame;
using NAudio.Vorbis;
using NAudio.Wave;
using System;
using System.IO;
using System.Text;
using WinUIMusicPlayer.Reader;

namespace WinUIMusicPlayer.AudioConverters
{
    public class AudioConverter
    {
        public EventHandler<double>? progressEvent;
        public void ConvertMp3(string mp3FilePath, string outputPath, string type = "wav")
        {
            using (Mp3FileReader mp3Reader = new Mp3FileReader(mp3FilePath))
            {
                using (WaveStream pcmStream = WaveFormatConversionStream.CreatePcmStream(mp3Reader))
                {
                    long totalBytes = pcmStream.Length;
                    long bytesWritten = 0;
                    DateTime lastUpdate = DateTime.Now;
                    if (type == "wav")
                    {
                        using (WaveFileWriter wavWriter = new WaveFileWriter(outputPath, pcmStream.WaveFormat))
                        {
                            byte[] buffer = new byte[4096];
                            int bytesRead;
                            while ((bytesRead = pcmStream.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                wavWriter.Write(buffer, 0, bytesRead);
                                bytesWritten += bytesRead;
                                if ((DateTime.Now - lastUpdate).TotalSeconds >= 1)
                                {
                                    double progress = (double)bytesWritten / totalBytes * 100;
                                    Console.WriteLine($"当前写入进度: {progress:F2}%");
                                    lastUpdate = DateTime.Now;
                                    progressEvent?.Invoke(this, progress);
                                }
                            }
                        }
                    }
                    if (type == "flac")
                    {
                        var memoryStream = new MemoryStream();
                        var writer = new BinaryWriter(memoryStream);
                        WriteWavHeader(writer, memoryStream, pcmStream);
                        byte[] buffer = new byte[4096];
                        int bytesRead;
                        while ((bytesRead = pcmStream.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            writer.Write(buffer, 0, bytesRead);
                            bytesWritten += bytesRead;
                            if ((DateTime.Now - lastUpdate).TotalSeconds >= 1)
                            {
                                double progress = (double)bytesWritten / totalBytes * 100;
                                Console.WriteLine($"当前写入进度: {progress:F2}%");
                                lastUpdate = DateTime.Now;
                                progressEvent?.Invoke(this, progress);
                            }
                        }
                        memoryStream.Position = 0;
                        ConvertAudioToFlac(outputPath, memoryStream);
                    }
                }
            }
            SaveMetaData(mp3FilePath, outputPath);
            progressEvent?.Invoke(this, 100);
        }

        public void ConvertWav(string inputFilePath, string outputPath, string type = "flac")
        {
            if (type == "flac")
            {
                AudioBuffer buff = WAVReader.ReadAllSamples(inputFilePath, null);
                FlakeWriter target;
                target = new FlakeWriter(outputPath, null, new FlakeWriterSettings { PCM = buff.PCM, EncoderMode = "7" });
                target.Settings.Padding = 1;
                target.DoSeekTable = false;
                target.FinalSampleCount = buff.Length;
                target.Write(buff);
                target.Close();
            }
            if (type == "mp3")
            {
                using (WaveStream wavReader = new WaveFileReader(inputFilePath))
                {
                    if (type == "mp3")
                    {
                        var mp3FormatPCMStream = ResampleToMp3Format(wavReader, wavReader.WaveFormat.Channels);
                        long totalBytes = mp3FormatPCMStream.Length;
                        long bytesWritten = 0;
                        DateTime lastUpdate = DateTime.Now;
                        using (LameMP3FileWriter mp3Writer = new LameMP3FileWriter(outputPath, mp3FormatPCMStream.WaveFormat, LAMEPreset.INSANE))
                        {
                            byte[] buffer = new byte[4096];
                            int bytesRead;
                            while ((bytesRead = mp3FormatPCMStream.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                mp3Writer.Write(buffer, 0, bytesRead);
                                bytesWritten += bytesRead;
                                if ((DateTime.Now - lastUpdate).TotalSeconds >= 1)
                                {
                                    double progress = (double)bytesWritten / totalBytes * 100;
                                    lastUpdate = DateTime.Now;
                                    progressEvent?.Invoke(this, progress);
                                }
                            }
                        }
                    }
                }
            }
            SaveMetaData(inputFilePath, outputPath);
            progressEvent?.Invoke(this, 100);
        }

        public void ConvertFlac(string flacFilePath, string outputPath, string type = "wav")
        {
            try
            {
                using (WaveStream flacReader = new FlacReader(flacFilePath))
                {
                    ConvertFlacFFmpeg(flacReader, outputPath, type);
                }
            }
            catch (Exception ex)
            {
                using (WaveStream flacReader = new FFmpegAudioReader(flacFilePath))
                {
                    ConvertFlacFFmpeg(flacReader, outputPath, type);
                }
            }
            SaveMetaData(flacFilePath, outputPath);
            progressEvent?.Invoke(this, 100);
        }

        private void ConvertFlacFFmpeg(WaveStream flacReader, string outputPath, string type)
        {
            long totalBytes = flacReader.Length;
            long bytesWritten = 0;
            DateTime lastUpdate = DateTime.Now;
            if (type == "wav")
            {
                using (var wavWriter = new WaveFileWriter(outputPath, flacReader.WaveFormat))
                {
                    byte[] buffer = new byte[4096];
                    int bytesRead;
                    while ((bytesRead = flacReader.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        wavWriter.Write(buffer, 0, bytesRead);
                        bytesWritten += bytesRead;
                        if ((DateTime.Now - lastUpdate).TotalSeconds >= 1)
                        {
                            double progress = (double)bytesWritten / totalBytes * 100;
                            lastUpdate = DateTime.Now;
                            progressEvent?.Invoke(this, progress);
                        }
                    }
                }
            }
            if (type == "mp3")
            {
                var mp3FormatPCMStream = ResampleToMp3Format(flacReader, flacReader.WaveFormat.Channels);
                totalBytes = mp3FormatPCMStream.Length;
                using (LameMP3FileWriter mp3Writer = new LameMP3FileWriter(outputPath, mp3FormatPCMStream.WaveFormat, LAMEPreset.INSANE))
                {
                    byte[] buffer = new byte[4096];
                    int bytesRead;
                    while ((bytesRead = mp3FormatPCMStream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        mp3Writer.Write(buffer, 0, bytesRead);
                        bytesWritten += bytesRead;
                        if ((DateTime.Now - lastUpdate).TotalSeconds >= 1)
                        {
                            double progress = (double)bytesWritten / totalBytes * 100;
                            lastUpdate = DateTime.Now;
                            progressEvent?.Invoke(this, progress);
                        }
                    }
                }
            }
        }

        public void ConvertAiff(string filePath, string outputPath, string type = "wav")
        {
            using (WaveStream audioReader = new AiffFileReader(filePath))
            {
                long totalBytes = audioReader.Length;
                long bytesWritten = 0;
                DateTime lastUpdate = DateTime.Now;
                if (type == "wav")
                {
                    using (var wavWriter = new WaveFileWriter(outputPath, audioReader.WaveFormat))
                    {
                        byte[] buffer = new byte[4096];
                        int bytesRead;
                        while ((bytesRead = audioReader.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            wavWriter.Write(buffer, 0, bytesRead);
                            bytesWritten += bytesRead;
                            if ((DateTime.Now - lastUpdate).TotalSeconds >= 1)
                            {
                                double progress = (double)bytesWritten / totalBytes * 100;
                                lastUpdate = DateTime.Now;
                                progressEvent?.Invoke(this, progress);
                            }
                        }
                    }
                }
                if (type == "mp3")
                {
                    var mp3FormatPCMStream = ResampleToMp3Format(audioReader, audioReader.WaveFormat.Channels);
                    totalBytes = mp3FormatPCMStream.Length;
                    using (LameMP3FileWriter mp3Writer = new LameMP3FileWriter(outputPath, mp3FormatPCMStream.WaveFormat, LAMEPreset.INSANE))
                    {
                        byte[] buffer = new byte[4096];
                        int bytesRead;
                        while ((bytesRead = mp3FormatPCMStream.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            mp3Writer.Write(buffer, 0, bytesRead);
                            bytesWritten += bytesRead;
                            if ((DateTime.Now - lastUpdate).TotalSeconds >= 1)
                            {
                                double progress = (double)bytesWritten / totalBytes * 100;
                                lastUpdate = DateTime.Now;
                                progressEvent?.Invoke(this, progress);
                            }
                        }
                    }
                }
                if (type == "flac")
                {
                    var memoryStream = new MemoryStream();
                    var writer = new BinaryWriter(memoryStream);
                    WriteWavHeader(writer, memoryStream, audioReader);
                    byte[] buffer = new byte[4096];
                    int bytesRead;
                    while ((bytesRead = audioReader.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        writer.Write(buffer, 0, bytesRead);
                        bytesWritten += bytesRead;
                        if ((DateTime.Now - lastUpdate).TotalSeconds >= 1)
                        {
                            double progress = (double)bytesWritten / totalBytes * 100;
                            lastUpdate = DateTime.Now;
                            progressEvent?.Invoke(this, progress);
                        }
                    }
                    memoryStream.Position = 0;
                    ConvertAudioToFlac(outputPath, memoryStream);
                }
            }
            SaveMetaData(filePath, outputPath);
            progressEvent?.Invoke(this, 100);
        }

        public void ConvertDSDToWav(string filePath, string outputPath, string type = "wav")
        {
            using (IWaveSource waveSource = new FfmpegDecoder(filePath))
            {
                if (type == "wav")
                {
                    IWaveSource audio = waveSource.ChangeSampleRate(waveSource.WaveFormat.SampleRate / 4);
                    using (CSCore.Codecs.WAV.WaveWriter wavWriter = new CSCore.Codecs.WAV.WaveWriter(outputPath, audio.WaveFormat))
                    {
                        long totalBytes = audio.Length;
                        long bytesWritten = 0;
                        DateTime lastUpdate = DateTime.Now;
                        // 确保缓冲区大小是块对齐的倍数
                        int bufferSize = audio.WaveFormat.BlockAlign * 1024; // 使用块对齐的倍数
                        byte[] buffer = new byte[bufferSize];
                        int bytesRead;
                        while ((bytesRead = audio.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            wavWriter.Write(buffer, 0, bytesRead);
                            bytesWritten += bytesRead;
                            if ((DateTime.Now - lastUpdate).TotalSeconds >= 1)
                            {
                                double progress = (double)bytesWritten / totalBytes * 100;
                                lastUpdate = DateTime.Now;
                                progressEvent?.Invoke(this, progress);
                            }
                        }
                    }
                }
                if (type == "mp3")
                {

                    IWaveSource audio;
                    if (waveSource.WaveFormat.SampleRate != 44100)
                    {
                        // 使用更高质量的重采样算法
                        var resampler = new CSCore.DSP.DmoResampler(waveSource, 44100)
                        {
                            Quality = 60 // 设置高质量
                        };
                        ISampleSource sampleSource = resampler.ToSampleSource();
                        var normalizer = new AudioNormalizer(sampleSource);
                        sampleSource = normalizer;
                        var limiter = new SoftLimiter(sampleSource, -0.1f); // -0.1dB限制
                        sampleSource = limiter;
                        sampleSource = new DitheringProcessor(sampleSource, 16);
                        audio = sampleSource.ToWaveSource(16);
                    }
                    else
                    {
                        ISampleSource sampleSource = waveSource.ToSampleSource();
                        var normalizer = new AudioNormalizer(sampleSource);
                        sampleSource = normalizer;
                        var limiter = new SoftLimiter(sampleSource, -0.1f); // -0.1dB限制
                        sampleSource = limiter;
                        sampleSource = new DitheringProcessor(sampleSource, 16);
                        audio = sampleSource.ToWaveSource(16);
                    }
                    NAudio.Wave.WaveFormat wave = new NAudio.Wave.WaveFormat(audio.WaveFormat.SampleRate,
                                                                             audio.WaveFormat.BitsPerSample,
                                                                             audio.WaveFormat.Channels);
                    long totalBytes = audio.Length;
                    long bytesWritten = 0;
                    DateTime lastUpdate = DateTime.Now;
                    using (LameMP3FileWriter mp3Writer = new LameMP3FileWriter(outputPath, wave, LAMEPreset.INSANE))
                    {
                        byte[] buffer = new byte[4096];
                        int bytesRead;
                        while ((bytesRead = audio.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            mp3Writer.Write(buffer, 0, bytesRead);
                            bytesWritten += bytesRead;
                            if ((DateTime.Now - lastUpdate).TotalSeconds >= 1)
                            {
                                double progress = (double)bytesWritten / totalBytes * 100;
                                lastUpdate = DateTime.Now;
                                progressEvent?.Invoke(this, progress);
                            }
                        }
                    }
                }
                if (type == "flac")
                {
                    int sampleRate = 176400;
                    if (waveSource.WaveFormat.SampleRate / 4 <= sampleRate)
                    {
                        sampleRate = waveSource.WaveFormat.SampleRate / 4;
                    }
                    IWaveSource audio;
                    if (waveSource.WaveFormat.SampleRate != sampleRate)
                    {
                        // 使用更高质量的重采样算法
                        var resampler = new CSCore.DSP.DmoResampler(waveSource, sampleRate)
                        {
                            Quality = 60 // 设置高质量
                        };
                        ISampleSource sampleSource = resampler.ToSampleSource();
                        var normalizer = new AudioNormalizer(sampleSource);
                        sampleSource = normalizer;
                        var limiter = new SoftLimiter(sampleSource, -0.1f); // -0.1dB限制
                        sampleSource = limiter;
                        sampleSource = new DitheringProcessor(sampleSource, 24);
                        audio = sampleSource.ToWaveSource(24);
                    }
                    else
                    {
                        ISampleSource sampleSource = waveSource.ToSampleSource();
                        var normalizer = new AudioNormalizer(sampleSource);
                        sampleSource = normalizer;
                        var limiter = new SoftLimiter(sampleSource, -0.1f); // -0.1dB限制
                        sampleSource = limiter;
                        sampleSource = new DitheringProcessor(sampleSource, 24);
                        audio = sampleSource.ToWaveSource(24);
                    }
                    long totalBytes = audio.Length;
                    long bytesWritten = 0;
                    DateTime lastUpdate = DateTime.Now;
                    using (var memoryStream = new MemoryStream())
                    {
                        // 先写入占位符头部
                        WritePlaceholderWavHeader(memoryStream, audio.WaveFormat);
                        long dataStartPosition = memoryStream.Position;

                        // 写入音频数据
                        int bufferSize = audio.WaveFormat.BlockAlign * 4096; // 增大缓冲区
                        byte[] buffer = new byte[bufferSize];
                        int bytesRead;
                        long actualDataSize = 0;

                        while ((bytesRead = audio.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            memoryStream.Write(buffer, 0, bytesRead);
                            actualDataSize += bytesRead;
                            bytesWritten += bytesRead;

                            if ((DateTime.Now - lastUpdate).TotalSeconds >= 1)
                            {
                                double progress = (double)bytesWritten / Math.Max(totalBytes, 1) * 100;
                                lastUpdate = DateTime.Now;
                                if (progress < 99)
                                {
                                    progressEvent?.Invoke(this, progress);
                                }
                            }
                        }

                        // 更新WAV头部信息
                        UpdateWavHeaderInMemory(memoryStream, actualDataSize);

                        // 重置位置并读取
                        memoryStream.Position = 0;
                        AudioBuffer buff = WAVReader.ReadAllSamples(null, memoryStream);

                        FlakeWriter target = new FlakeWriter(outputPath, null, new FlakeWriterSettings
                        {
                            PCM = buff.PCM,
                            EncoderMode = "7"
                        });

                        target.Settings.Padding = 1;
                        target.DoSeekTable = false;
                        target.FinalSampleCount = buff.Length;
                        target.Write(buff);
                        target.Close();
                    }
                }
            }
            SaveMetaData(filePath, outputPath);
            progressEvent?.Invoke(this, 100);
        }

        public void FFmpegConverter(string filePath, string outputPath, string type = "wav", int bitDepth = 16)
        {
            using (IWaveSource waveSource = new FfmpegDecoder(filePath))
            {
                if (type == "wav")
                {
                    using (CSCore.Codecs.WAV.WaveWriter wavWriter = new CSCore.Codecs.WAV.WaveWriter(outputPath, waveSource.WaveFormat))
                    {
                        long totalBytes = waveSource.Length;
                        long bytesWritten = 0;
                        DateTime lastUpdate = DateTime.Now;
                        // 确保缓冲区大小是块对齐的倍数
                        int bufferSize = waveSource.WaveFormat.BlockAlign * 1024; // 使用块对齐的倍数
                        byte[] buffer = new byte[bufferSize];
                        int bytesRead;
                        while ((bytesRead = waveSource.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            wavWriter.Write(buffer, 0, bytesRead);
                            bytesWritten += bytesRead;
                            if ((DateTime.Now - lastUpdate).TotalSeconds >= 1)
                            {
                                double progress = (double)bytesWritten / totalBytes * 100;
                                lastUpdate = DateTime.Now;
                                progressEvent?.Invoke(this, progress);
                            }
                        }
                    }
                }
                if (type == "mp3")
                {
                    IWaveSource audio;
                    if (waveSource.WaveFormat.SampleRate != 44100)
                    {
                        // 使用更高质量的重采样算法
                        var resampler = new CSCore.DSP.DmoResampler(waveSource, 44100)
                        {
                            Quality = 60 // 设置高质量
                        };
                        ISampleSource sampleSource = resampler.ToSampleSource();
                        var normalizer = new AudioNormalizer(sampleSource);
                        sampleSource = normalizer;
                        var limiter = new SoftLimiter(sampleSource, -0.1f); // -0.1dB限制
                        sampleSource = limiter;
                        sampleSource = new DitheringProcessor(sampleSource, 16);
                        audio = sampleSource.ToWaveSource(16);
                    }
                    else
                    {
                        ISampleSource sampleSource = waveSource.ToSampleSource();
                        var normalizer = new AudioNormalizer(sampleSource);
                        sampleSource = normalizer;
                        var limiter = new SoftLimiter(sampleSource, -0.1f); // -0.1dB限制
                        sampleSource = limiter;
                        sampleSource = new DitheringProcessor(sampleSource, 16);
                        audio = sampleSource.ToWaveSource(16);
                    }
                    NAudio.Wave.WaveFormat wave = new NAudio.Wave.WaveFormat(audio.WaveFormat.SampleRate,
                                                                             audio.WaveFormat.BitsPerSample,
                                                                             audio.WaveFormat.Channels);
                    long totalBytes = audio.Length;
                    long bytesWritten = 0;
                    DateTime lastUpdate = DateTime.Now;
                    using (LameMP3FileWriter mp3Writer = new LameMP3FileWriter(outputPath, wave, LAMEPreset.INSANE))
                    {
                        byte[] buffer = new byte[4096];
                        int bytesRead;
                        while ((bytesRead = audio.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            mp3Writer.Write(buffer, 0, bytesRead);
                            bytesWritten += bytesRead;
                            if ((DateTime.Now - lastUpdate).TotalSeconds >= 1)
                            {
                                double progress = (double)bytesWritten / totalBytes * 100;
                                lastUpdate = DateTime.Now;
                                progressEvent?.Invoke(this, progress);
                            }
                        }
                    }
                }
                if (type == "flac")
                {
                    int sampleRate = 176400;
                    if (waveSource.WaveFormat.SampleRate <= sampleRate)
                    {
                        sampleRate = waveSource.WaveFormat.SampleRate;
                    }
                    IWaveSource audio;
                    if (waveSource.WaveFormat.SampleRate != sampleRate)
                    {
                        // 使用更高质量的重采样算法
                        var resampler = new CSCore.DSP.DmoResampler(waveSource, sampleRate)
                        {
                            Quality = 60 // 设置高质量
                        };
                        ISampleSource sampleSource = resampler.ToSampleSource();
                        var normalizer = new AudioNormalizer(sampleSource);
                        sampleSource = normalizer;
                        var limiter = new SoftLimiter(sampleSource, -0.1f); // -0.1dB限制
                        sampleSource = limiter;
                        sampleSource = new DitheringProcessor(sampleSource, bitDepth);
                        audio = sampleSource.ToWaveSource(bitDepth);
                    }
                    else
                    {
                        ISampleSource sampleSource = waveSource.ToSampleSource();
                        var normalizer = new AudioNormalizer(sampleSource);
                        sampleSource = normalizer;
                        var limiter = new SoftLimiter(sampleSource, -0.1f); // -0.1dB限制
                        sampleSource = limiter;
                        sampleSource = new DitheringProcessor(sampleSource, bitDepth);
                        audio = sampleSource.ToWaveSource(bitDepth);
                    }
                    long totalBytes = audio.Length;
                    long bytesWritten = 0;
                    DateTime lastUpdate = DateTime.Now;
                    using (var memoryStream = new MemoryStream())
                    {
                        // 先写入占位符头部
                        WritePlaceholderWavHeader(memoryStream, audio.WaveFormat);
                        long dataStartPosition = memoryStream.Position;
                        int bufferSize = audio.WaveFormat.BlockAlign * 1024; // 增大缓冲区
                        byte[] buffer = new byte[bufferSize];
                        int bytesRead;
                        long actualDataSize = 0;

                        while ((bytesRead = audio.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            memoryStream.Write(buffer, 0, bytesRead);
                            actualDataSize += bytesRead;
                            bytesWritten += bytesRead;

                            if ((DateTime.Now - lastUpdate).TotalSeconds >= 1)
                            {
                                double progress = (double)bytesWritten / Math.Max(totalBytes, 1) * 100;
                                lastUpdate = DateTime.Now;
                                if (progress < 99)
                                {
                                    progressEvent?.Invoke(this, progress);
                                }
                            }
                        }
                        UpdateWavHeaderInMemory(memoryStream, actualDataSize);
                        memoryStream.Position = 0;
                        AudioBuffer buff = WAVReader.ReadAllSamples(null, memoryStream);
                        FlakeWriter target = new FlakeWriter(outputPath, null, new FlakeWriterSettings
                        {
                            PCM = buff.PCM,
                            EncoderMode = "7"
                        });
                        target.Settings.Padding = 1;
                        target.DoSeekTable = false;
                        target.FinalSampleCount = buff.Length;
                        target.Write(buff);
                        target.Close();
                    }
                }
            }
            SaveMetaData(filePath, outputPath);
            progressEvent?.Invoke(this, 100);
        }

        public void ConvertOgg(string filePath, string outputPath, string type = "wav")
        {
            using (WaveStream audio = new VorbisWaveReader(filePath))
            {
                long totalBytes = audio.Length;
                long bytesWritten = 0;
                DateTime lastUpdate = DateTime.Now;
                if (type == "wav")
                {
                    using (WaveFileWriter wavWriter = new WaveFileWriter(outputPath, audio.WaveFormat))
                    {
                        byte[] buffer = new byte[4096];
                        int bytesRead;
                        while ((bytesRead = audio.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            wavWriter.Write(buffer, 0, bytesRead);
                            bytesWritten += bytesRead;
                            if ((DateTime.Now - lastUpdate).TotalSeconds >= 1)
                            {
                                double progress = (double)bytesWritten / totalBytes * 100;
                                lastUpdate = DateTime.Now;
                                progressEvent?.Invoke(this, progress);
                            }
                        }
                    }
                }
                if (type == "mp3")
                {
                    var mp3FormatPCMStream = ResampleToMp3Format(audio, audio.WaveFormat.Channels);
                    totalBytes = mp3FormatPCMStream.Length;
                    using (LameMP3FileWriter mp3Writer = new LameMP3FileWriter(outputPath, mp3FormatPCMStream.WaveFormat, LAMEPreset.INSANE))
                    {
                        byte[] buffer = new byte[4096];
                        int bytesRead;
                        while ((bytesRead = mp3FormatPCMStream.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            mp3Writer.Write(buffer, 0, bytesRead);
                            bytesWritten += bytesRead;
                            if ((DateTime.Now - lastUpdate).TotalSeconds >= 1)
                            {
                                double progress = (double)bytesWritten / totalBytes * 100;
                                lastUpdate = DateTime.Now;
                                progressEvent?.Invoke(this, progress);
                            }
                        }
                    }
                }
                if (type == "flac")
                {
                    var memoryStream = new MemoryStream();
                    var writer = new BinaryWriter(memoryStream);
                    WriteWavHeader(writer, memoryStream, audio);
                    byte[] buffer = new byte[4096];
                    int bytesRead;
                    while ((bytesRead = audio.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        writer.Write(buffer, 0, bytesRead);
                        bytesWritten += bytesRead;
                        if ((DateTime.Now - lastUpdate).TotalSeconds >= 1)
                        {
                            double progress = (double)bytesWritten / totalBytes * 100;
                            lastUpdate = DateTime.Now;
                            progressEvent?.Invoke(this, progress);
                        }
                    }
                    memoryStream.Position = 0;
                    ConvertAudioToFlac(outputPath, memoryStream);
                }
            }
            SaveMetaData(filePath, outputPath);
            progressEvent?.Invoke(this, 100);
        }


        public string GenerateOutputPath(string inputPath, string extension)
        {
            string directory = Path.GetDirectoryName(inputPath);
            string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(inputPath);
            return Path.Combine(directory, $"{fileNameWithoutExtension}_output.{extension.ToLower()}");
        }

        public void WriteWavHeader(BinaryWriter writer, Stream memoryStream, WaveStream pcmStream)
        {
            // 写入 WAV 文件头
            writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            writer.Write((int)(pcmStream.Length + 36)); // 文件总长度 - 8
            writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16); // fmt 块大小
            writer.Write((short)1); // 音频格式（PCM 为 1）
            writer.Write((short)pcmStream.WaveFormat.Channels); // 声道数
            writer.Write(pcmStream.WaveFormat.SampleRate); // 采样率
            writer.Write(pcmStream.WaveFormat.AverageBytesPerSecond); // 每秒平均字节数
            writer.Write((short)pcmStream.WaveFormat.BlockAlign); // 块对齐
            writer.Write((short)pcmStream.WaveFormat.BitsPerSample); // 位深度
            writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            writer.Write((int)pcmStream.Length); // 数据块大小
        }

        private static void WritePlaceholderWavHeader(Stream stream, CSCore.WaveFormat format)
        {
            using (BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write(Encoding.UTF8.GetBytes("RIFF"));
                writer.Write((int)0); // 占位符
                writer.Write(Encoding.UTF8.GetBytes("WAVE"));
                writer.Write(Encoding.UTF8.GetBytes("fmt "));
                writer.Write(16);
                writer.Write((ushort)format.WaveFormatTag);
                writer.Write((ushort)format.Channels);
                writer.Write(format.SampleRate);
                writer.Write(format.BytesPerSecond);
                writer.Write((ushort)format.BlockAlign);
                writer.Write((ushort)format.BitsPerSample);
                writer.Write(Encoding.UTF8.GetBytes("data"));
                writer.Write((int)0); // 占位符
            }
        }

        // 辅助方法：更新内存中的WAV头部
        private static void UpdateWavHeaderInMemory(MemoryStream stream, long actualDataSize)
        {
            long currentPosition = stream.Position;

            // 更新RIFF块大小 (位置4)
            stream.Position = 4;
            stream.Write(BitConverter.GetBytes((int)(actualDataSize + 36)), 0, 4);

            // 更新data块大小 (位置40)
            stream.Position = 40;
            stream.Write(BitConverter.GetBytes((int)actualDataSize), 0, 4);

            stream.Position = currentPosition;
        }


        public void ConvertAudioToFlac(string outputPath, MemoryStream memoryStream)
        {
            AudioBuffer buff = WAVReader.ReadAllSamples(null, memoryStream);
            FlakeWriter target;
            target = new FlakeWriter(outputPath, null, new FlakeWriterSettings { PCM = buff.PCM, EncoderMode = "7" });
            target.Settings.Padding = 1;
            target.DoSeekTable = false;
            target.FinalSampleCount = buff.Length;
            target.Write(buff);
            target.Close();
        }

        public static WaveStream ResampleToMp3Format(WaveStream inputStream, int channels)
        {
            var targetFormat = new NAudio.Wave.WaveFormat(44100, 16, channels);
            return new ResamplerDmoStream(inputStream, targetFormat);
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
