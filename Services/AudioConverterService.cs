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
            if (music.Extension.ToLower() == "mp3")
            {
                Task.Run(() =>
                {
                    AudioConverter.ConvertMp3ToWav(music.Path, outputPath);
                });
            }
            else if (music.Extension.ToLower() == "flac")
            {
                Task.Run(() =>
                {
                    AudioConverter.ConvertFlacToWav(music.Path, outputPath);
                });
            }
            else if (music.Extension.ToLower() == "aiff" || music.Extension.ToLower() == "aif")
            {
                Task.Run(() =>
                {
                    AudioConverter.ConvertAiffToWav(music.Path, outputPath);
                });
            }
            else if (music.Extension.ToLower() == "wav")
            {
            }
            else if (music.Extension.ToLower() == "dsf" || music.Extension.ToLower() == "dff") {
                Task.Run(() =>
                {
                    AudioConverter.ConvertDSDToWav(music.Path, outputPath,music.SampleRate);
                });
            }
            else
            {
                Task.Run(() =>
                {
                    AudioConverter.ConvertAudioToWav(music.Path, outputPath);
                });
            }
        }
    }
}
