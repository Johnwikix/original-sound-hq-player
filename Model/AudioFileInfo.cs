using System;

namespace WinUIMusicPlayer.Model
{
    public class AudioFileInfo
    {
        public string Title { get; set; } = "未知标题";
        public string Album { get; set; } = "未知艺术家";
        public string Artist { get; set; } = "未知专辑";
        public int SampleRate { get; set; } = 0;
        public int ChannelCount { get; set; } = 0;
        public int BitRate { get; set; } = 0;
        public int BitDepth { get; set; } = 0;
        public int Year { get; set; } = 0;
        public string Lyrics { get; set; } = string.Empty;
        public int TrackNumber { get; set; } = 0;
        public int DiskNumber { get; set; } = 0;
        public TimeSpan Duration { get; set; } = TimeSpan.Zero;
    }
}
