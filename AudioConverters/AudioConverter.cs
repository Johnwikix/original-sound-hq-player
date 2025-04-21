using CSCore;
using CSCore.Ffmpeg;
using NAudio.Flac;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using WinUIMusicPlayer.Reader;

namespace WinUIMusicPlayer.AudioConverters
{
    public class AudioConverter
    {
        public static void ConvertMp3ToWav(string mp3FilePath, string wavFilePath)
        {
            using (Mp3FileReader mp3Reader = new Mp3FileReader(mp3FilePath))
            using (WaveStream pcmStream = WaveFormatConversionStream.CreatePcmStream(mp3Reader))
            using (WaveFileWriter wavWriter = new WaveFileWriter(wavFilePath, pcmStream.WaveFormat))
            {
                byte[] buffer = new byte[4096];
                int bytesRead;
                while ((bytesRead = pcmStream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    wavWriter.Write(buffer, 0, bytesRead);
                }
            }
        }

        public static void ConvertFlacToWav(string flacFilePath, string wavFilePath)
        {
            using (WaveStream flacReader = new FlacReader(flacFilePath))
            using (var wavWriter = new WaveFileWriter(wavFilePath, flacReader.WaveFormat))
            {
                byte[] buffer = new byte[4096];
                int bytesRead;
                while ((bytesRead = flacReader.Read(buffer, 0, buffer.Length)) > 0)
                {
                    wavWriter.Write(buffer, 0, bytesRead);
                }
            }
        }

        public static void ConvertAiffToWav(string filePath, string wavFilePath)
        {
            using (WaveStream audioReader = new AiffFileReader(filePath))
            using (var wavWriter = new WaveFileWriter(wavFilePath, audioReader.WaveFormat))
            {
                byte[] buffer = new byte[4096];
                int bytesRead;
                while ((bytesRead = audioReader.Read(buffer, 0, buffer.Length)) > 0)
                {
                    wavWriter.Write(buffer, 0, bytesRead);
                }
            }
        }

        public static void ConvertAudioToWav(string filePath, string wavFilePath)
        {            
            using (WaveStream audioReader = new MediaFoundationReader(filePath))
            {                
                using (var wavWriter = new WaveFileWriter(wavFilePath, audioReader.WaveFormat))
                {
                    byte[] buffer = new byte[4096];
                    int bytesRead;
                    while ((bytesRead = audioReader.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        wavWriter.Write(buffer, 0, bytesRead);
                    }
                }
            }            
        }

        public static void ConvertDSDToWav(string filePath, string wavFilePath,int sampleRate)
        {
            if (sampleRate == 0) {
                sampleRate = 5644800;
            }
            using (IWaveSource audio = (new FfmpegDecoder(filePath)).ChangeSampleRate(sampleRate/16))
            {
                using (CSCore.Codecs.WAV.WaveWriter wavWriter = new CSCore.Codecs.WAV.WaveWriter(wavFilePath, audio.WaveFormat))
                {
                    byte[] buffer = new byte[4096];
                    int bytesRead;
                    while ((bytesRead = audio.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        wavWriter.Write(buffer, 0, bytesRead);
                    }
                }
            }
        }


        public static string GenerateOutputPath(string inputPath,string extension)
        {
            string directory = Path.GetDirectoryName(inputPath);
            string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(inputPath);
            return Path.Combine(directory, $"{ fileNameWithoutExtension}_output.{extension.ToLower()}");
        }
    }
}
