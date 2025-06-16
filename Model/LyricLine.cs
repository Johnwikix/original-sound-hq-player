using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WinUIMusicPlayer.Model
{
    public partial class LyricLine : ObservableObject
    {
        [ObservableProperty]
        private string _text;
        [ObservableProperty]
        private bool _isCurrent;

        //public string Text
        //{
        //    get => _text;
        //    set
        //    {
        //        if (_text != value)
        //        {
        //            _text = value;
        //            OnPropertyChanged();
        //        }
        //    }
        //}

        public TimeSpan Time { get; set; }

        //public bool IsCurrent
        //{
        //    get => _isCurrent;
        //    set
        //    {
        //        if (_isCurrent != value)
        //        {
        //            _isCurrent = value;
        //            OnPropertyChanged();
        //        }
        //    }
        //}

        //public event PropertyChangedEventHandler PropertyChanged;

        //protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        //{
        //    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        //}
    }
}
