using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.ObjectModel;
using System.Linq;

namespace WinUIMusicPlayer.Controls.Lyrics
{
    public partial class LyricLine : ObservableObject
    {
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

        public TimeSpan CurrentPlayingTime { get => field; set => SetProperty(ref field, value); } = TimeSpan.Zero;
        public string LyricText => string.Join(string.Empty, Words.Select(w => w.Word));
    }

    public class LyricWord
    {
        public string Word { get; set; } = string.Empty;
        public TimeSpan StartTime { get; set; }
        public TimeSpan Duration { get; set; }
    }
}
