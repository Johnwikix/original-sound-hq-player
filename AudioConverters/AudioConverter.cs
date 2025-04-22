using CSCore;
using CSCore.Ffmpeg;
using CUETools.Codecs.FLAKE;
using NAudio.Flac;
using NAudio.Lame;
using NAudio.Vorbis;
using NAudio.Wave;
using System.IO;

namespace WinUIMusicPlayer.AudioConverters
{
    public class AudioConverter
    {
        public static void ConvertMp3(string mp3FilePath, string outputPath, string type = "wav")
        {


            using (Mp3FileReader mp3Reader = new Mp3FileReader(mp3FilePath))
            {
                using (WaveStream pcmStream = WaveFormatConversionStream.CreatePcmStream(mp3Reader))
                {
                    if (type == "wav")
                    {
                        using (WaveFileWriter wavWriter = new WaveFileWriter(outputPath, pcmStream.WaveFormat))
                        {
                            byte[] buffer = new byte[4096];
                            int bytesRead;
                            while ((bytesRead = pcmStream.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                wavWriter.Write(buffer, 0, bytesRead);
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
                        }
                        memoryStream.Position = 0;
                        AudioBuffer buff = WAVReader.ReadAllSamples(null, memoryStream);
                        FlakeWriter target;
                        target = new FlakeWriter(outputPath, null, new FlakeWriterSettings { PCM = buff.PCM, EncoderMode = "7" });
                        target.Settings.Padding = 1;
                        target.DoSeekTable = false;
                        target.FinalSampleCount = buff.Length;
                        target.Write(buff);
                        target.Close();
                    }
                }
            }

        }

        public static void ConvertWav(string inputFilePath, string outputPath, string type = "flac")
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
        }

        public static void ConvertFlac(string flacFilePath, string outputPath, string type = "wav")
        {
            using (WaveStream flacReader = new FlacReader(flacFilePath))
            {
                if (type == "wav")
                {
                    using (var wavWriter = new WaveFileWriter(outputPath, flacReader.WaveFormat))
                    {
                        byte[] buffer = new byte[4096];
                        int bytesRead;
                        while ((bytesRead = flacReader.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            wavWriter.Write(buffer, 0, bytesRead);
                        }
                    }
                }
                if (type == "mp3")
                {
                    using (LameMP3FileWriter mp3Writer = new LameMP3FileWriter(outputPath, flacReader.WaveFormat, LAMEPreset.INSANE))
                    {
                        byte[] buffer = new byte[4096];
                        int bytesRead;
                        while ((bytesRead = flacReader.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            // 将读取的 PCM 数据写入到 MP3 文件中
                            mp3Writer.Write(buffer, 0, bytesRead);
                        }
                    }
                }
            }
        }

        public static void ConvertAiff(string filePath, string outputPath, string type = "wav")
        {
            using (WaveStream audioReader = new AiffFileReader(filePath))
            {
                if (type == "wav")
                {
                    using (var wavWriter = new WaveFileWriter(outputPath, audioReader.WaveFormat))
                    {
                        byte[] buffer = new byte[4096];
                        int bytesRead;
                        while ((bytesRead = audioReader.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            wavWriter.Write(buffer, 0, bytesRead);
                        }
                    }
                }
                if (type == "mp3")
                {
                    using (LameMP3FileWriter mp3Writer = new LameMP3FileWriter(outputPath, audioReader.WaveFormat, LAMEPreset.INSANE))
                    {
                        byte[] buffer = new byte[4096];
                        int bytesRead;
                        while ((bytesRead = audioReader.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            // 将读取的 PCM 数据写入到 MP3 文件中
                            mp3Writer.Write(buffer, 0, bytesRead);
                        }
                    }
                }
            }

        }

        public static void ConvertAudio(string filePath, string outputPath, string type = "wav")
        {
            using (WaveStream audioReader = new MediaFoundationReader(filePath))
            {
                if (type == "wav")
                {
                    using (var wavWriter = new WaveFileWriter(outputPath, audioReader.WaveFormat))
                    {
                        byte[] buffer = new byte[4096];
                        int bytesRead;
                        while ((bytesRead = audioReader.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            wavWriter.Write(buffer, 0, bytesRead);
                        }
                    }
                }
                if (type == "mp3")
                {
                    using (LameMP3FileWriter mp3Writer = new LameMP3FileWriter(outputPath, audioReader.WaveFormat, LAMEPreset.INSANE))
                    {
                        byte[] buffer = new byte[4096];
                        int bytesRead;
                        while ((bytesRead = audioReader.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            // 将读取的 PCM 数据写入到 MP3 文件中
                            mp3Writer.Write(buffer, 0, bytesRead);
                        }
                    }
                }
                if (type == "wma")
                {
                    using (var reader = new WaveFileReader(filePath))
                    {
                        MediaFoundationEncoder.EncodeToWma(reader, outputPath);
                    }
                }
            }
        }

        public static void ConvertDSDToWav(string filePath, string outputPath, string type = "wav")
        {
            using (IWaveSource waveSource = new FfmpegDecoder(filePath))
            {

                if (type == "wav")
                {
                    IWaveSource audio = waveSource.ChangeSampleRate(waveSource.WaveFormat.SampleRate / 4);
                    using (CSCore.Codecs.WAV.WaveWriter wavWriter = new CSCore.Codecs.WAV.WaveWriter(outputPath, audio.WaveFormat))
                    {
                        // 确保缓冲区大小是块对齐的倍数
                        int bufferSize = audio.WaveFormat.BlockAlign * 1024; // 使用块对齐的倍数
                        byte[] buffer = new byte[bufferSize];
                        int bytesRead;

                        while ((bytesRead = audio.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            wavWriter.Write(buffer, 0, bytesRead);
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
                    using (LameMP3FileWriter mp3Writer = new LameMP3FileWriter(outputPath, wave, LAMEPreset.INSANE))
                    {
                        byte[] buffer = new byte[4096];
                        int bytesRead;
                        while ((bytesRead = convertedSource.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            mp3Writer.Write(buffer, 0, bytesRead);
                        }
                    }
                }
                if (type == "flac")
                {
                    IWaveSource resampledSource = waveSource.ChangeSampleRate(waveSource.WaveFormat.SampleRate / 4);
                    // 转换为24位格式
                    IWaveSource audio = resampledSource.ToSampleSource().ToWaveSource(24);
                    // 创建临时WAV文件
                    string tempWavFile = Path.GetTempFileName();
                    try
                    {
                        // 使用CSCore的WaveWriter创建标准WAV文件
                        using (CSCore.Codecs.WAV.WaveWriter wavWriter = new CSCore.Codecs.WAV.WaveWriter(tempWavFile, audio.WaveFormat))
                        {
                            int bufferSize = audio.WaveFormat.BlockAlign * 1024;
                            byte[] buffer = new byte[bufferSize];
                            int bytesRead;
                            while ((bytesRead = audio.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                wavWriter.Write(buffer, 0, bytesRead);
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
            }
        }

        public static void ConvertOgg(string filePath, string outputPath, string type = "wav")
        {
            using (WaveStream audio = new VorbisWaveReader(filePath))
            {
                if (type == "wav")
                {
                    using (WaveFileWriter wavWriter = new WaveFileWriter(outputPath, audio.WaveFormat))
                    {
                        byte[] buffer = new byte[4096];
                        int bytesRead;
                        while ((bytesRead = audio.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            wavWriter.Write(buffer, 0, bytesRead);
                        }
                    }
                }
                if (type == "mp3")
                {
                    using (LameMP3FileWriter mp3Writer = new LameMP3FileWriter(outputPath, audio.WaveFormat, LAMEPreset.INSANE))
                    {
                        byte[] buffer = new byte[4096];
                        int bytesRead;
                        while ((bytesRead = audio.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            // 将读取的 PCM 数据写入到 MP3 文件中
                            mp3Writer.Write(buffer, 0, bytesRead);
                        }
                    }
                }
            }
        }


        public static string GenerateOutputPath(string inputPath, string extension)
        {
            string directory = Path.GetDirectoryName(inputPath);
            string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(inputPath);
            return Path.Combine(directory, $"{fileNameWithoutExtension}_output.{extension.ToLower()}");
        }

        public static void WriteWavHeader(BinaryWriter writer, Stream memoryStream, WaveStream pcmStream)
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
    }
}
