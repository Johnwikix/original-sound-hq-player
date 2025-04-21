using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using WinUIMusicPlayer.AudioConverters;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Services
{
    public class AudioConverterService
    {
        public static void ConvertAudio2Wav(Music music) {
            string outputPath = AudioConverter.GenerateOutputPath(music.Path, "wav");
            if (music.Extension.ToLower() == "mp3") {
                Task.Run(() => {
                    AudioConverter.ConvertMp3ToWav(music.Path, outputPath);
                });               
            }
            if (music.Extension.ToLower() == "flac") {
                Task.Run(() => {
                    AudioConverter.ConvertFlacToWav(music.Path, outputPath);
                });                
            }
        }
    }
}
