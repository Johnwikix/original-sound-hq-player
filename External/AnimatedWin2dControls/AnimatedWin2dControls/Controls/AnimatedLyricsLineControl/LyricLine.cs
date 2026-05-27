using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.Generic;

namespace AnimatedWin2dControls.Controls.AnimatedLyricsLineControl
{
    public partial class LyricLine : ObservableObject
    {
        public List<LyricWord> Words { get => field; set => SetProperty(ref field, value); } = [];
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

        public double StartMs
        {
            get => field;
            set => SetProperty(ref field, value);
        } = 0;

        public double EndMs
        {
            get => field;
            set => SetProperty(ref field, value);
        } = 0;
    }
}
