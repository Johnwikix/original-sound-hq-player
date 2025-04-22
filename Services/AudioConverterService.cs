using System.Threading.Tasks;
using WinUIMusicPlayer.AudioConverters;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Services
{
    public class AudioConverterService
    {
        public static void ConvertAudio2Wav(Music music, string type = "wav")
        {
            string outputPath = AudioConverter.GenerateOutputPath(music.Path, type);
            switch (music.Extension.ToLower())
            {
                case "mp3":
                    Task.Run(() =>
                    {
                        AudioConverter.ConvertMp3(music.Path, outputPath, type);
                    });
                    break;
                case "flac":
                    Task.Run(() =>
                    {
                        AudioConverter.ConvertFlac(music.Path, outputPath, type);
                    });
                    break;
                case "aiff":
                    Task.Run(() =>
                    {
                        AudioConverter.ConvertAiff(music.Path, outputPath, type);
                    });
                    break;
                case "aif":
                    Task.Run(() =>
                    {
                        AudioConverter.ConvertAiff(music.Path, outputPath, type);
                    });
                    break;
                case "wav":
                    Task.Run(() =>
                    {
                        AudioConverter.ConvertWav(music.Path, outputPath, type);
                    });
                    break;
                case "ogg":
                    Task.Run(() =>
                    {
                        AudioConverter.ConvertOgg(music.Path, outputPath, type);
                    });
                    break;
                case "dsf":
                    Task.Run(() =>
                    {
                        AudioConverter.ConvertDSDToWav(music.Path, outputPath, type);
                    });
                    break;
                case "dff":
                    Task.Run(() =>
                    {
                        AudioConverter.ConvertDSDToWav(music.Path, outputPath, type);
                    });
                    break;
                default:
                    Task.Run(() =>
                    {
                        AudioConverter.ConvertAudio(music.Path, outputPath, type);
                    });
                    break;
            }
        }
    }
}
