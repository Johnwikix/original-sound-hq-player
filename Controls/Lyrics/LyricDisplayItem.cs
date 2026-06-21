using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;

namespace WinUIMusicPlayer.Controls.Lyrics
{
    public sealed partial class LyricDisplayItem : ObservableObject
    {
        public LyricLine Source { get; }
        public int LineIndex { get; }
        public string MainText { get; }
        public string TranslationText { get; }
        public bool HasTranslation => !string.IsNullOrEmpty(TranslationText);

        [ObservableProperty]
        private bool _isCurrent;

        [ObservableProperty]
        private double _displayOpacity = 0.5;

        [ObservableProperty]
        private double _displayFontSize = 36.0;

        [ObservableProperty]
        private TextAlignment _displayTextAlignment = TextAlignment.Left;

        [ObservableProperty]
        private double _displayTranslationOpacity = 0.6;

        [ObservableProperty]
        private string _displayFontFamily = "Segoe UI";

        public double DisplayTranslationFontSize => DisplayFontSize * 0.75;

        public LyricDisplayItem(LyricLine source, int index, string mainText, string translationText)
        {
            Source = source;
            LineIndex = index;
            MainText = mainText;
            TranslationText = translationText;
        }

        partial void OnDisplayFontSizeChanged(double value)
        {
            OnPropertyChanged(nameof(DisplayTranslationFontSize));
        }
    }
}
