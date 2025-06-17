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
        [ObservableProperty]
        private string _title;
        //public string Title
        //{
        //    get { return title; }
        //    set
        //    {
        //        if (title != value)
        //        {
        //            title = value;
        //            OnPropertyChanged();
        //        }
        //    }
        //}
        private BitmapImage _cover = null;
        [Ignore]
        public BitmapImage Cover
        {
            get => _cover;
            set => SetProperty(ref _cover, value);
        }
        [ObservableProperty]
        private string _author;

        //public string Author
        //{
        //    get { return author; }
        //    set
        //    {
        //        if (author != value)
        //        {
        //            author = value;
        //            OnPropertyChanged();
        //        }
        //    }
        //}
        public TimeSpan Duration { get; set; }
        [ObservableProperty]
        private string _album;

        //public string Album
        //{
        //    get { return album; }
        //    set
        //    {
        //        if (album != value)
        //        {
        //            album = value;
        //            OnPropertyChanged();
        //        }
        //    }
        //}
        public string FolderPath { get; set; }
        [ObservableProperty]
        public string _lastLevelFolderPath;

        //public string LastLevelFolderPath
        //{
        //    get { return lastLevelFolderPath; }
        //    set
        //    {
        //        if (lastLevelFolderPath != value)
        //        {
        //            lastLevelFolderPath = value;
        //            OnPropertyChanged();
        //        }
        //    }
        //}
        public string Extension { get; set; }
        public int Order { get; set; }
        public int BitDepth { get; set; }
        public int BitRate { get; set; }
        public int SampleRate { get; set; }
        [Ignore]
        public int PlayListOrder { get; set; }
        public int Channel { get; set; }
        public int Year { get; set; }
        [ObservableProperty]
        private bool _isFavorite = false;
        //public bool isFavorite
        //{
        //    get { return _isFavorite; }
        //    set
        //    {
        //        if (_isFavorite != value)
        //        {
        //            _isFavorite = value;
        //            OnPropertyChanged();
        //        }
        //    }
        //}
        [ObservableProperty]
        private int _trackNumber;

        //public int TrackNumber
        //{
        //    get { return _trackNumber; }
        //    set
        //    {
        //        if (_trackNumber != value)
        //        {
        //            _trackNumber = value;
        //            OnPropertyChanged();
        //        }
        //    }
        //}
        [ObservableProperty]
        private string _lyrics;

        //public string Lyrics
        //{
        //    get { return _lyrics; }
        //    set
        //    {
        //        if (_lyrics != value)
        //        {
        //            _lyrics = value;
        //            OnPropertyChanged();
        //        }
        //    }
        //}
        private int _isExistOnDevice = 0;
        [Ignore]
        public int IsExistOnDevice
        {
            get => _isExistOnDevice;
            set => SetProperty(ref _isExistOnDevice, value);
            //get { return _isExistOnDevice; }
            //set
            //{
            //    if (_isExistOnDevice != value)
            //    {
            //        _isExistOnDevice = value;
            //        OnPropertyChanged();
            //    }
            //}
        }

        //public override bool Equals(object obj)
        //{
        //    if (obj == null || GetType() != obj.GetType())
        //        return false;

        //    Music other = (Music)obj;
        //    return Id == other.Id;
        //}

        //public override int GetHashCode()
        //{
        //    return Id.GetHashCode();
        //}

        //public event PropertyChangedEventHandler PropertyChanged;

        //protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        //{
        //    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        //}
    }
}
