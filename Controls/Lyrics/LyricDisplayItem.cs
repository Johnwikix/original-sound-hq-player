using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;

namespace WinUIMusicPlayer.Controls.Lyrics
{
    public sealed class LyricDisplayItem : ObservableObject
    {
        public LyricLine Source { get; }
        public int LineIndex { get; }
        public string MainText { get; }
        public string TranslationText { get; }
        public bool HasTranslation => !string.IsNullOrEmpty(TranslationText);

        public bool IsCurrent { get => field; set => SetProperty(ref field, value); }
        public double DisplayOpacity { get => field; set => SetProperty(ref field, value); } = 0.5;
        public double DisplayFontSize
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    OnPropertyChanged(nameof(DisplayTranslationFontSize));
                }
            }
        } = 36.0;
        public TextAlignment DisplayTextAlignment { get => field; set => SetProperty(ref field, value); } = TextAlignment.Left;
        public double DisplayTranslationOpacity { get => field; set => SetProperty(ref field, value); } = 0.6;
        public string DisplayFontFamily { get => field; set => SetProperty(ref field, value); } = "Segoe UI";

        public double DisplayTranslationFontSize => DisplayFontSize * 0.75;

        public LyricDisplayItem(LyricLine source, int index, string mainText, string translationText)
        {
            Source = source;
            LineIndex = index;
            MainText = mainText;
            TranslationText = translationText;
        }
    }
}
