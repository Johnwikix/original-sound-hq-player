using CSCore;
using CSCore.Ffmpeg;
using CSCore.Streams;
using NAudio.Flac;
using NAudio.Lame;
using NAudio.MediaFoundation;
using NAudio.Vorbis;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using WinUIMusicPlayer.Reader;

namespace WinUIMusicPlayer.AudioConverters
{
    public class AudioConverter
    {
        public static void ConvertMp3(string mp3FilePath, string outputPath, string type = "wav")
        {
            using (Mp3FileReader mp3Reader = new Mp3FileReader(mp3FilePath)) {
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

                }
            }
        }

        public static void ConvertFlac(string flacFilePath, string outputPath, string type = "wav")
        {
            using (WaveStream flacReader = new FlacReader(flacFilePath)) {
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
            using (WaveStream audioReader = new AiffFileReader(filePath)) {
                if (type == "wav") {
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
                if (type == "mp3") {
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
                if (type == "wav") {
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
                if (type == "mp3") {
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
                if (type == "wma") {                    
                    using (var reader = new WaveFileReader(filePath))
                    {
                        MediaFoundationEncoder.EncodeToWma(reader, outputPath);
                    }
                }                
            }
        }

        public static void ConvertDSDToWav(string filePath, string outputPath,string type="wav")
        {
            using (IWaveSource waveSource = new FfmpegDecoder(filePath))
            {
                IWaveSource audio = waveSource.ChangeSampleRate(waveSource.WaveFormat.SampleRate/4);
                if (type == "wav") {
                    using (CSCore.Codecs.WAV.WaveWriter wavWriter = new CSCore.Codecs.WAV.WaveWriter(outputPath, audio.WaveFormat))
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
                    IWaveSource resampledSource = audio.ChangeSampleRate(44100);
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
            }
        }

        public static void ConvertOgg(string filePath, string outputPath, string type = "wav")
        {
            using (WaveStream audio = new VorbisWaveReader(filePath))
            {
                if (type == "wav") {
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
                if (type == "mp3") {
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
    }
}
