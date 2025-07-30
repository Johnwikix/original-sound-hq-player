using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media.Imaging;
using SQLite;
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations.Schema;
using System.Runtime.CompilerServices;

namespace WinUIMusicPlayer.Model
{
    public partial class Music : ObservableObject
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }
        public string Path { get; set; }
        
        private string _title;
        public string Title
        {
            get => _title;
            set => SetProperty(ref _title, value);
        }

        private BitmapImage _cover = null;
        [Ignore]
        public BitmapImage Cover
        {
            get => _cover;
            set => SetProperty(ref _cover, value);
        }

        private string _author;
        public string Author
        {
            get => _author;
            set => SetProperty(ref _author, value);
        }


        public TimeSpan Duration { get; set; }
        private string _album;
        public string Album
        {
            get => _album;
            set => SetProperty(ref _album, value);
        }

        public string FolderPath { get; set; }
        public string _lastLevelFolderPath;
        public string LastLevelFolderPath
        {
            get => _lastLevelFolderPath;
            set => SetProperty(ref _lastLevelFolderPath, value);
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
        public bool IsFavorite
        {
            get => _isFavorite;
            set => SetProperty(ref _isFavorite, value);
        }

        private int _trackNumber;
        public int TrackNumber
        {
            get => _trackNumber;
            set => SetProperty(ref _trackNumber, value);
        }

        private int _diskNumber;
        public int DiskNumber {
            get => _diskNumber;
            set => SetProperty(ref _diskNumber, value);
        }

        private string _lyrics;
        public string Lyrics
        {
            get => _lyrics;
            set => SetProperty(ref _lyrics, value);
        }

        private int _playCount = 0;
        public int PlayCount
        {
            get => _playCount;
            set => SetProperty(ref _playCount, value);
        }

        private int _isExistOnDevice = 0;
        [Ignore]
        public int IsExistOnDevice
        {
            get => _isExistOnDevice;
            set => SetProperty(ref _isExistOnDevice, value);
        }

        private DateTime _createTime;
        public DateTime CreateTime
        {
            get => _createTime;
            set => SetProperty(ref _createTime, value);
        }
        private DateTime _updateTime;
        public DateTime UpdateTime
        {
            get => _updateTime;
            set => SetProperty(ref _updateTime, value);
        }

    }
}
