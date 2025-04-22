using System;
using System.Diagnostics;
using System.Threading.Tasks;
using WinUIMusicPlayer.AudioConverters;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Services
{
    public class AudioConverterService
    {
        public EventHandler<double> updateProgress;
        public void ConvertAudio2Wav(Music music, string type = "wav")
        {
            AudioConverter converter = new AudioConverter();
            converter.progressEvent += (sender, progress) =>
            {
                // 触发 Service 的事件，将进度传递给 Page
                OnProgressChanged(progress);
            };
            string outputPath = converter.GenerateOutputPath(music.Path, type);
            switch (music.Extension.ToLower())
            {
                case "mp3":
                    Task.Run(() =>
                    {
                        converter.ConvertMp3(music.Path, outputPath, type);
                    });
                    break;
                case "flac":
                    Task.Run(() =>
                    {
                        converter.ConvertFlac(music.Path, outputPath, type);
                    });
                    break;
                case "aiff":
                    Task.Run(() =>
                    {
                        converter.ConvertAiff(music.Path, outputPath, type);
                    });
                    break;
                case "aif":
                    Task.Run(() =>
                    {
                        converter.ConvertAiff(music.Path, outputPath, type);
                    });
                    break;
                case "wav":
                    Task.Run(() =>
                    {
                        converter.ConvertWav(music.Path, outputPath, type);
                    });
                    break;
                case "ogg":
                    Task.Run(() =>
                    {
                        converter.ConvertOgg(music.Path, outputPath, type);
                    });
                    break;
                case "dsf":
                    Task.Run(() =>
                    {
                        converter.ConvertDSDToWav(music.Path, outputPath, type);
                    });
                    break;
                case "dff":
                    Task.Run(() =>
                    {
                        converter.ConvertDSDToWav(music.Path, outputPath, type);
                    });
                    break;
                default:
                    Task.Run(() =>
                    {
                        converter.ConvertAudio(music.Path, outputPath, type);
                    });
                    break;
            }
        }

        private void OnProgressChanged(double progress)
        {
            updateProgress?.Invoke(this, progress);
        }
    }
}
