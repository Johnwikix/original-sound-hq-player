using System;
using System.Threading.Tasks;
using WinUIMusicPlayer.AudioConverters;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Services
{
    public class AudioConverterService
    {
        public EventHandler<double>? updateProgress;
        private AudioConverter converter;

        public AudioConverterService() {
            converter = new AudioConverter();
            converter.progressEvent += (sender, progress) =>
            {
                // 触发 Service 的事件，将进度传递给 Page
                OnProgressChanged(progress);
            };
        }
        public async Task ConvertAudio2Wav(Music music, string type = "wav")
        {
            try
            {
                string outputPath = converter.GenerateOutputPath(music.Path, type);
                switch (music.Extension.ToLower())
                {
                    case "mp3":
                        await Task.Run(() =>
                        {
                            converter.ConvertMp3(music.Path, outputPath, type);
                        });
                        break;
                    case "flac":
                        await Task.Run(() =>
                        {
                            converter.ConvertFlac(music.Path, outputPath, type);
                        });
                        break;
                    case "aiff":
                        await Task.Run(() =>
                        {
                            converter.ConvertAiff(music.Path, outputPath, type);
                        });
                        break;
                    case "aif":
                        await Task.Run(() =>
                        {
                            converter.ConvertAiff(music.Path, outputPath, type);
                        });
                        break;
                    case "wav":
                        await Task.Run(() =>
                        {
                            converter.ConvertWav(music.Path, outputPath, type);
                        });
                        break;
                    case "ogg":
                        await Task.Run(() =>
                        {
                            converter.ConvertOgg(music.Path, outputPath, type);
                        });
                        break;
                    case "dsf":
                        await Task.Run(() =>
                        {
                            converter.ConvertDSDToWav(music.Path, outputPath, type);
                        });
                        break;
                    case "dff":
                        await Task.Run(() =>
                        {
                            converter.ConvertDSDToWav(music.Path, outputPath, type);
                        });
                        break;
                    default:
                        await Task.Run(() =>
                        {
                            converter.ConvertAudio(music.Path, outputPath, type);
                        });
                        break;
                }
            }
            catch (Exception e) {
                OnProgressChanged(100);
            }
        }

        private void OnProgressChanged(double progress)
        {
            updateProgress?.Invoke(this, progress);
        }
    }
}
