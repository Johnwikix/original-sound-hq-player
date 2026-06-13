using System;
using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.V2;
using Microsoft.Graphics.Canvas.Text;

namespace AnimatedWin2dControls.Messages;

public static class LyricsSettingsBus
{
    public readonly struct Settings
    {
        public readonly string FontFamilyName;
        public readonly CanvasHorizontalAlignment LyricsTextAlignment;
        public readonly bool IsDark;
        public readonly double ScrollSensitivity;
        public readonly double LyricsBlurAmount;
        public readonly double GlowAmount;
        public readonly double CharFloatAmount;
        public readonly double CharScaleAmount;
        public readonly double LongSyllableThreshold;
        public readonly bool IsFadeOutEnabled;
        public readonly bool IsOutOfSightEnabled;
        public readonly double UnplayedOpacity;
        public readonly double TranslatedOpacity;
        public readonly double StrokeWidth;
        public readonly EasingType ScrollEasingType;
        public readonly EaseMode ScrollEasingMode;
        public readonly double PlayingLineTopOffset;
        public readonly double TargetFrameRate;

        public Settings(
            string fontFamilyName,
            CanvasHorizontalAlignment lyricsTextAlignment,
            bool isDark,
            double scrollSensitivity,
            double lyricsBlurAmount,
            double glowAmount,
            double charFloatAmount,
            double charScaleAmount,
            double longSyllableThreshold,
            bool isFadeOutEnabled,
            bool isOutOfSightEnabled,
            double unplayedOpacity,
            double translatedOpacity,
            double strokeWidth,
            EasingType scrollEasingType,
            EaseMode scrollEasingMode,
            double playingLineTopOffset,
            double targetFrameRate)
        {
            FontFamilyName = fontFamilyName;
            LyricsTextAlignment = lyricsTextAlignment;
            IsDark = isDark;
            ScrollSensitivity = scrollSensitivity;
            LyricsBlurAmount = lyricsBlurAmount;
            GlowAmount = glowAmount;
            CharFloatAmount = charFloatAmount;
            CharScaleAmount = charScaleAmount;
            LongSyllableThreshold = longSyllableThreshold;
            IsFadeOutEnabled = isFadeOutEnabled;
            IsOutOfSightEnabled = isOutOfSightEnabled;
            UnplayedOpacity = unplayedOpacity;
            TranslatedOpacity = translatedOpacity;
            StrokeWidth = strokeWidth;
            ScrollEasingType = scrollEasingType;
            ScrollEasingMode = scrollEasingMode;
            PlayingLineTopOffset = playingLineTopOffset;
            TargetFrameRate = targetFrameRate;
        }
    }

    public static event Action<Settings>? SyncRequested;
    public static void Publish(in Settings value) => SyncRequested?.Invoke(value);
}
