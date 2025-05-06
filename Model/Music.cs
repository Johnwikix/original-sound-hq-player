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
        private string title;
        public string Title
        {
            get { return title; }
            set
            {
                if (title != value)
                {
                    title = value;
                    OnPropertyChanged();
                }
            }
        }

        private BitmapImage cover = null;
        [Ignore]
        public BitmapImage Cover
        {
            get { return cover; }
            set
            {
                if (cover != value)
                {
                    cover = value;
                    OnPropertyChanged();
                }
            }
        }

        private string author;

        public string Author
        {
            get { return author; }
            set
            {
                if (author != value)
                {
                    author = value;
                    OnPropertyChanged();
                }
            }
        }
        public TimeSpan Duration { get; set; }
        private string album;

        public string Album
        {
            get { return album; }
            set
            {
                if (album != value)
                {
                    album = value;
                    OnPropertyChanged();
                }
            }
        }
        public string FolderPath { get; set; }
        public string lastLevelFolderPath;

        public string LastLevelFolderPath
        {
            get { return lastLevelFolderPath; }
            set
            {
                if (lastLevelFolderPath != value)
                {
                    lastLevelFolderPath = value;
                    OnPropertyChanged();
                }
            }
        }
        public string Extension { get; set; }
        public int Order { get; set; }
        public int BitDepth { get; set; }
        public int BitRate { get; set; }
        public int SampleRate { get; set; }
        [Ignore]
        public int PlayListOrder { get; set; }
        public int Channel { get; set; }
        public int Year { get; set; }
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

        private int _trackNumber { get; set; }

        public int TrackNumber
        {
            get { return _trackNumber; }
            set
            {
                if (_trackNumber != value)
                {
                    _trackNumber = value;
                    OnPropertyChanged();
                }
            }
        }

        private string _lyrics { get; set; }

        public string Lyrics
        {
            get { return _lyrics; }
            set
            {
                if (_lyrics != value)
                {
                    _lyrics = value;
                    OnPropertyChanged();
                }
            }
        }

        private bool _isExistOnDevice { get; set; } = false;
        public bool IsExistOnDevice
        {
            get { return _isExistOnDevice; }
            set
            {
                if (_isExistOnDevice != value)
                {
                    _isExistOnDevice = value;
                    OnPropertyChanged();
                }
            }
        }

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
