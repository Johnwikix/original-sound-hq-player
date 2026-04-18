using AnimatedWin2dControls.Controls.AnimatedTextBlock.Enums;
using System;
using System.Collections.Generic;
using System.Text;

namespace WinUIMusicPlayer.Model
{
    public class EffectComboBoxItem
    {
        public string DisplayName { get; set; } = string.Empty;
        public AnimatedTextEffect Value { get; set; } = AnimatedTextEffect.TextDefaultEffect;
    }
}
