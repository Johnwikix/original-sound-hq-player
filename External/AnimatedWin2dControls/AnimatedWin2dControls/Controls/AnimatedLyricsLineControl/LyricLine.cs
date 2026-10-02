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

        public string PronunciationText
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

        /// <summary>Gets or sets the display highlight boundary, independently of the actual lyric end.</summary>
        /// <remarks>The playback projection derives this value; it is not stored in the lyrics database.</remarks>
        public double? HighlightEndMs { get; set; }
    }
}
