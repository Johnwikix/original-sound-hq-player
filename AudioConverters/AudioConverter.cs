using CSCore;
using CSCore.Ffmpeg;
using CUETools.Codecs.FLAKE;
using Microsoft.VisualBasic.Devices;
using NAudio.Flac;
using NAudio.Lame;
using NAudio.Vorbis;
using NAudio.Wave;
using System;
using System.Diagnostics;
using System.IO;

namespace WinUIMusicPlayer.AudioConverters
{
    public class AudioConverter
    {
        public EventHandler<double> progressEvent;
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
                                    progressEvent?.Invoke(this,progress);
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
                    SaveMetaData(mp3FilePath, outputPath);
                    progressEvent?.Invoke(this, 100);
                }
            }

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
            if (type == "mp3") {
                using (WaveStream wavReader = new WaveFileReader(inputFilePath))
                {
                    long totalBytes = wavReader.Length;
                    long bytesWritten = 0;
                    DateTime lastUpdate = DateTime.Now;
                    if (type == "mp3")
                    {
                        var mp3FormatPCMStream = ResampleToMp3Format(wavReader, wavReader.WaveFormat.Channels);
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
            using (WaveStream flacReader = new FlacReader(flacFilePath))
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
            SaveMetaData(flacFilePath, outputPath);
            progressEvent?.Invoke(this, 100);
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
                SaveMetaData(filePath, outputPath);
                progressEvent?.Invoke(this, 100);
            }

        }

        public void ConvertAudio(string filePath, string outputPath, string type = "wav")
        {
            using (WaveStream audioReader = new MediaFoundationReader(filePath))
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
                if (type == "wma")
                {
                    using (var reader = new WaveFileReader(filePath))
                    {
                        MediaFoundationEncoder.EncodeToWma(reader, outputPath);
                    }
                }
                SaveMetaData(filePath, outputPath);
                progressEvent?.Invoke(this, 100);
            }
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

                    IWaveSource resampledSource = waveSource.ChangeSampleRate(44100);
                    IWaveSource convertedSource = resampledSource.ToSampleSource().ToWaveSource(16);
                    NAudio.Wave.WaveFormat wave = new NAudio.Wave.WaveFormat(convertedSource.WaveFormat.SampleRate,
                                                                             convertedSource.WaveFormat.BitsPerSample,
                                                                             convertedSource.WaveFormat.Channels);
                    long totalBytes = convertedSource.Length;
                    long bytesWritten = 0;
                    DateTime lastUpdate = DateTime.Now;
                    using (LameMP3FileWriter mp3Writer = new LameMP3FileWriter(outputPath, wave, LAMEPreset.INSANE))
                    {
                        byte[] buffer = new byte[4096];
                        int bytesRead;
                        while ((bytesRead = convertedSource.Read(buffer, 0, buffer.Length)) > 0)
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
                    if (waveSource.WaveFormat.SampleRate / 4 <= sampleRate) {
                        sampleRate = waveSource.WaveFormat.SampleRate;
                    }
                    IWaveSource resampledSource = waveSource.ChangeSampleRate(sampleRate);
                    IWaveSource audio = resampledSource.ToSampleSource().ToWaveSource(24);
                    long totalBytes = audio.Length;
                    long bytesWritten = 0;
                    DateTime lastUpdate = DateTime.Now;
                    string tempFileName = $"temp_{Guid.NewGuid()}.wav";
                    string tempWavFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"temp",tempFileName);
                    string directory = Path.GetDirectoryName(tempWavFile);
                    if (!Directory.Exists(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }
                    try
                    {
                        // 使用CSCore的WaveWriter创建临时WAV文件
                        using (CSCore.Codecs.WAV.WaveWriter wavWriter = new CSCore.Codecs.WAV.WaveWriter(tempWavFile, audio.WaveFormat))
                        {
                            int bufferSize = audio.WaveFormat.BlockAlign * 1024;
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
                                    if (progress < 99) {
                                        progressEvent?.Invoke(this, progress);
                                    }                                    
                                }
                            }
                        }
                        AudioBuffer buff = WAVReader.ReadAllSamples(tempWavFile, null);
                        FlakeWriter target;
                        target = new FlakeWriter(outputPath, null, new FlakeWriterSettings { PCM = buff.PCM, EncoderMode = "7" });
                        target.Settings.Padding = 1;
                        target.DoSeekTable = false;
                        target.FinalSampleCount = buff.Length;
                        target.Write(buff);
                        target.Close();
                    }
                    finally
                    {
                        File.Delete(tempWavFile);
                    }
                }
                SaveMetaData(filePath, outputPath);
                progressEvent?.Invoke(this, 100);
            }
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
                SaveMetaData(filePath, outputPath);
                progressEvent?.Invoke(this, 100);
            }
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

        public static WaveStream ResampleToMp3Format(WaveStream inputStream,int channels)
        {
            var targetFormat = new NAudio.Wave.WaveFormat(44100, 16, channels);
            return new ResamplerDmoStream(inputStream, targetFormat);
        }

        private void SaveMetaData(string inputFile,string outputPath) {
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
