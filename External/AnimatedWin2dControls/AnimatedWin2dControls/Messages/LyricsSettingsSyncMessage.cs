using Microsoft.Graphics.Canvas.Text;
using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.V2;

namespace AnimatedWin2dControls.Messages;

public sealed record LyricsSettingsSyncMessage(
    double LyricsFontSize,
    string FontFamilyName,
    CanvasHorizontalAlignment LyricsTextAlignment,
    bool IsDark,
    double OffsetMs,
    double ScrollSensitivity,
    double LyricsBlurAmount,
    double GlowAmount,
    double CharFloatAmount,
    double CharScaleAmount,
    double LongSyllableThreshold,
    bool IsFadeOutEnabled,
    bool IsOutOfSightEnabled,
    double UnplayedOpacity,
    double TranslatedOpacity,
    double StrokeWidth,
    EasingType ScrollEasingType,
    EaseMode ScrollEasingMode,
    double PlayingLineTopOffset,
    double TargetFrameRate);
