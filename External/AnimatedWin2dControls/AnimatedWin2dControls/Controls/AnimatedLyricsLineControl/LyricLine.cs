using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl;
using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.ObjectModel;
using System.Linq;

namespace AnimatedWin2dControls.Controls.AnimatedLyricsLineControl
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
    }
}
