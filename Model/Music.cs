using Microsoft.UI.Xaml.Media.Imaging;
using SQLite;
using System;

namespace WinUIMusicPlayer.Model
{
    public class Music
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }
        public string Path { get; set; }
        public string Title { get; set; }
        public BitmapImage Cover { get; set; } = null;
        public string Author { get; set; }
        public TimeSpan Duration { get; set; }
        public string Album { get; set; }
        public string FolderPath { get; set; }
        public string LastLevelFolderPath { get; set; }
        public string Extension { get; set; }
        public int BitDepth { get; set; }
        public int BitRate { get; set; }
        public int SampleRate { get; set; }
        public int Channel { get; set; }

        public override bool Equals(object obj)
        {
            if (obj == null || GetType() != obj.GetType())
                return false;

            Music other = (Music)obj;
            return Id == other.Id;
        }

        public override int GetHashCode()
        {
            return Id.GetHashCode();
        }
    }
}
