using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.ObjectModel;

namespace WinUIMusicPlayer.Controls.Lyrics
{
    public partial class LyricLine : ObservableObject
    {
        //private string _text = string.Empty;
        //public string Text
        //{
        //    get => _text;
        //    set => SetProperty(ref _text, value);
        //}
        public ObservableCollection<LyricWord> Words { get => field; set => SetProperty(ref field, value); } = [];
        public string TransLateText
        {
            get => field;
            set => SetProperty(ref field, value);
        } = string.Empty;

        public bool IsCurrent
        {
            get => field;
            set => SetProperty(ref field, value);
        } = false;

        public TimeSpan Time { 
            get => field;
            set => SetProperty(ref field, value);
        } = TimeSpan.Zero;

        public TimeSpan LineAnimateDuration { 
            get => field; 
            set => SetProperty(ref field, value); 
        } = TimeSpan.Zero;
    }

    public class LyricWord
    {
        public string Word { get; set; } = string.Empty;
        public TimeSpan StartTime { get; set; }
        public TimeSpan Duration { get; set; }
    }
}
