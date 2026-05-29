using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.V2;
using Microsoft.Graphics.Canvas.Text;

namespace AnimatedWin2dControls.Messages;

public sealed record LyricsSettingsSyncMessage(
    string FontFamilyName,
    CanvasHorizontalAlignment LyricsTextAlignment,
    bool IsDark,
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
