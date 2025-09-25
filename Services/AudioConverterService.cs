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
        public EventHandler<double>? updateProgress;
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
                if (music.Extension.Equals("dsf", StringComparison.OrdinalIgnoreCase) || music.Extension.Equals("dff", StringComparison.OrdinalIgnoreCase)) {
                    BassDsd.DefaultFrequency = AppSettings.dsdPcmFreq;
                    BassDsd.DefaultGain = AppSettings.dsdGain;
                }
                switch (type) {
                    case "wav":
                        if (!music.Extension.Equals("wav", StringComparison.OrdinalIgnoreCase)) {
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
                }
                //switch (music.Extension.ToLower())
                //{
                //    case "mp3":
                //        await Task.Run(() =>
                //        {
                //            converter.ConvertMp3(music.Path, outputPath, type);
                //        });
                //        break;
                //    case "flac":
                //        await Task.Run(() =>
                //        {
                //            converter.ConvertFlac(music.Path, outputPath, type);
                //        });
                //        break;
                //    case "aiff":
                //        await Task.Run(() =>
                //        {
                //            converter.ConvertAiff(music.Path, outputPath, type);
                //        });
                //        break;
                //    case "aif":
                //        await Task.Run(() =>
                //        {
                //            converter.ConvertAiff(music.Path, outputPath, type);
                //        });
                //        break;
                //    case "wav":
                //        await Task.Run(() =>
                //        {
                //            converter.ConvertWav(music.Path, outputPath, type);
                //        });
                //        break;
                //    case "ogg":
                //        await Task.Run(() =>
                //        {
                //            converter.ConvertOgg(music.Path, outputPath, type);
                //        });
                //        break;
                //    case "dsf":
                //        await Task.Run(() =>
                //        {
                //            converter.ConvertDSDToWav(music.Path, outputPath, type);
                //        });
                //        break;
                //    case "dff":
                //        await Task.Run(() =>
                //        {
                //            converter.ConvertDSDToWav(music.Path, outputPath, type);
                //        });
                //        break;
                //    default:
                //        await Task.Run(() =>
                //        {
                //            converter.FFmpegConverter(music.Path, outputPath, type, music.BitDepth <= 16 ? 16 : music.BitDepth);
                //        });
                //        break;
                //}
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
