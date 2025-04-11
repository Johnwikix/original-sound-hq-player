using System;

namespace WinUIMusicPlayer.Model
{
    public class AudioFileInfo
    {
        public int SampleRate { get; set; } = 0;
        public int ChannelCount { get; set; } = 0;
        public int BitRate { get; set; } = 0;
        public int BitDepth { get; set; } = 0;
        public TimeSpan Duration { get; set; }
    }
}
