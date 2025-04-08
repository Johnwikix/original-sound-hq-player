using Microsoft.UI.Xaml.Media.Imaging;
using SQLite;
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WinUIMusicPlayer.Model
{
    public class Music : INotifyPropertyChanged
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }
        public string Path { get; set; }
        public string Title { get; set; }
        [Ignore]
        public BitmapImage Cover { get; set; } = null;
        public string Author { get; set; }
        public TimeSpan Duration { get; set; }
        public string Album { get; set; }
        public string FolderPath { get; set; }
        public string LastLevelFolderPath { get; set; }
        public string Extension { get; set; }
        public int Order { get; set; }
        public int BitDepth { get; set; }
        public int BitRate { get; set; }
        public int SampleRate { get; set; }
        public int Channel { get; set; }
        private bool _isFavorite = false;
        public bool isFavorite
        {
            get { return _isFavorite; }
            set
            {
                if (_isFavorite != value)
                {
                    _isFavorite = value;
                    OnPropertyChanged();
                }
            }
        }

        public int TrackNumber { get; set; }
        public string Lyrics { get; set; }
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

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
