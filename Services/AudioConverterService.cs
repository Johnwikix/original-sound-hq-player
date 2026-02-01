using ManagedBass.Dsd;
using System;
using System.IO;
using System.Threading.Tasks;
using WinUIMusicPlayer.AudioConverters;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Services
{
    public class AudioConverterService
    {
        public EventHandler<double>? updateProgress { get; set; }
        private BassAudioConverter bassAudioConverter;
        public AudioConverterService()
        {
            bassAudioConverter = new BassAudioConverter();
            bassAudioConverter.progressEvent += (sender, progress) =>
            {
                OnProgressChanged(progress);
            };
        }
        public async Task ConvertAudio2Wav(Music music, string type = "wav")
        {
            try
            {
                string outputPath = GenerateOutputPath(music.Path, type);
                if (music.Extension.Equals("dsf", StringComparison.OrdinalIgnoreCase) || music.Extension.Equals("dff", StringComparison.OrdinalIgnoreCase))
                {
                    BassDsd.DefaultFrequency = AppSettings.dsdPcmFreq;
                    BassDsd.DefaultGain = AppSettings.dsdGain;
                }
                switch (type)
                {
                    case "wav":
                        if (!music.Extension.Equals("wav", StringComparison.OrdinalIgnoreCase))
                        {
                            await Task.Run(() =>
                            {
                                bassAudioConverter.ConvertToWav(music.Path, outputPath);
                            });
                        }
                        break;
                    case "flac":
                        if (!music.Extension.Equals("flac", StringComparison.OrdinalIgnoreCase))
                        {
                            await Task.Run(() =>
                            {
                                bassAudioConverter.ConvertToFlac(music.Path, outputPath);
                            });
                        }
                        break;
                    case "mp3":
                        if (!music.Extension.Equals("mp3", StringComparison.OrdinalIgnoreCase))
                        {
                            await Task.Run(() =>
                            {
                                bassAudioConverter.ConvertToMp3(music.Path, outputPath);
                            });
                        }
                        break;
                    case "ogg":
                        if (!music.Extension.Equals("ogg", StringComparison.OrdinalIgnoreCase))
                        {
                            await Task.Run(() =>
                            {
                                bassAudioConverter.ConvertToOgg(music.Path, outputPath);
                            });
                        }
                        break;
                    case "opus":
                        if (!music.Extension.Equals("opus", StringComparison.OrdinalIgnoreCase))
                        {
                            await Task.Run(() =>
                            {
                                bassAudioConverter.ConvertToOpus(music.Path, outputPath);
                            });
                        }
                        break;
                }
            }
            catch (Exception)
            {
                OnProgressChanged(100);
            }
        }

        private string GenerateOutputPath(string inputPath, string extension)
        {
            string directory = Path.GetDirectoryName(inputPath);
            string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(inputPath);
            return Path.Combine(directory, $"{fileNameWithoutExtension}_output.{extension.ToLower()}");
        }

        private void OnProgressChanged(double progress)
        {
            updateProgress?.Invoke(this, progress);
        }
    }
}
