using AnimatedWin2dControls.Controls.AnimatedTextBlock.Enums;

namespace WinUIMusicPlayer.Model
{
    public class EffectComboBoxItem
    {
        public string DisplayName { get; set; } = string.Empty;
        public AnimatedTextEffect Value { get; set; } = AnimatedTextEffect.TextDefaultEffect;
    }
}
