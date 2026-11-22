using AnimatedWin2dControls.Enums;
using System;
using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.Advance;
using Microsoft.Graphics.Canvas.Text;
using Windows.UI;

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
        public readonly bool IsCustomColorEnabled;
        public readonly Color LyricsCustomColor;
        public readonly int FontWeight;
        public readonly bool ShowTranslation;
        public readonly bool ShowPronunciation;
        public readonly bool IsLyricsEdgeFeatheringEffectEnabled;
        public readonly bool IsFanLyricsEnabled;
        public readonly int FanLyricsAngle;
        public readonly bool Is3DLyricsEnabled;
        public readonly bool IsAuto3DLyricsEnabled;
        public readonly int Lyrics3DXAngle;
        public readonly int Lyrics3DYAngle;
        public readonly int Lyrics3DZAngle;
        public readonly int Lyrics3DDepth;
        public readonly WordByWordEffectMode WordByWordEffectMode;

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
            double targetFrameRate,
            bool isCustomColorEnabled,
            Color lyricsCustomColor,
            int fontWeight = 700,
            bool showTranslation = true,
            bool showPronunciation = true,
            bool isLyricsEdgeFeatheringEffectEnabled = true,
            bool isFanLyricsEnabled = false,
            int fanLyricsAngle = 30,
            bool is3DLyricsEnabled = false,
            bool isAuto3DLyricsEnabled = false,
            int lyrics3DXAngle = 0,
            int lyrics3DYAngle = 0,
            int lyrics3DZAngle = 0,
            int lyrics3DDepth = 800,
            WordByWordEffectMode wordByWordEffectMode = WordByWordEffectMode.Auto)
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
            IsCustomColorEnabled = isCustomColorEnabled;
            LyricsCustomColor = lyricsCustomColor;
            FontWeight = fontWeight;
            ShowTranslation = showTranslation;
            ShowPronunciation = showPronunciation;
            IsLyricsEdgeFeatheringEffectEnabled = isLyricsEdgeFeatheringEffectEnabled;
            IsFanLyricsEnabled = isFanLyricsEnabled;
            FanLyricsAngle = fanLyricsAngle;
            Is3DLyricsEnabled = is3DLyricsEnabled;
            IsAuto3DLyricsEnabled = isAuto3DLyricsEnabled;
            Lyrics3DXAngle = lyrics3DXAngle;
            Lyrics3DYAngle = lyrics3DYAngle;
            Lyrics3DZAngle = lyrics3DZAngle;
            Lyrics3DDepth = lyrics3DDepth;
            WordByWordEffectMode = wordByWordEffectMode;
        }
    }

    public static event Action<Settings>? SyncRequested;
    public static void Publish(in Settings value) => SyncRequested?.Invoke(value);
}
