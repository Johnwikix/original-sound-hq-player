using AnimatedWin2dControls.Controls.AnimatedTextBlock.Enums;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Text;
using System;
using System.Collections.Generic;
using System.Numerics;
using Windows.UI;

namespace AnimatedWin2dControls.Controls.AnimatedTextBlock.Effects;

public sealed class TextFadeEffect : ITextEffect
{
    // DelayPerCluster 对此 effect 无意义，设 Zero 让宿主 diff 路径尽快结束
    public TimeSpan AnimationDuration { get; set; } = TimeSpan.FromMilliseconds(1000);
    public TimeSpan DelayPerCluster { get; set; } = TimeSpan.Zero;

    internal float Progress { get; private set; } = 1f;
    internal bool IsFinished => Progress >= 1f;

    internal void Reset() => Progress = 0f;

    internal void Advance(TimeSpan elapsed)
    {
        float step = (float)(elapsed.TotalMilliseconds / AnimationDuration.TotalMilliseconds);
        Progress = MathF.Min(Progress + step, 1f);
    }

    public void DrawText(
        string oldText,
        string newText,
        List<TextDiffResult> diffResults,   // 此 effect 下始终为 null，忽略
        CanvasTextLayout oldTextLayout,
        CanvasTextLayout newTextLayout,
        CanvasTextFormat textFormat,
        Color textColor,
        CanvasLinearGradientBrush gradientBrush,
        AnimatedTextBlockRedrawState state,
        CanvasDrawingSession ds)
    {
        if (ds == null) return;

        ds.Transform = Matrix3x2.Identity;

        if (state == AnimatedTextBlockRedrawState.Idle || Progress >= 1f)
        {
            DrawLayout(ds, newTextLayout, textColor, gradientBrush, 1f);
            return;
        }

        float t = Ease(Progress);

        // 旧文字淡出
        if (t < 1f && oldTextLayout != null)
            DrawLayout(ds, oldTextLayout, textColor, gradientBrush, 1f - t);

        // 新文字淡入
        if (t > 0f && newTextLayout != null)
            DrawLayout(ds, newTextLayout, textColor, gradientBrush, t);
    }

    private static float Ease(float t)
    {
        // CubicOut: 1 - (1-t)^3
        float inv = 1f - t;
        return 1f - inv * inv * inv;
    }

    private static void DrawLayout(
        CanvasDrawingSession ds,
        CanvasTextLayout layout,
        Color textColor,
        CanvasLinearGradientBrush gradientBrush,
        float alpha)
    {
        if (layout == null || alpha <= 0f) return;
        try
        {
            using (ds.CreateLayer(alpha))
            {
                if (gradientBrush != null)
                    ds.DrawTextLayout(layout, Vector2.Zero, gradientBrush);
                else
                    ds.DrawTextLayout(layout, Vector2.Zero, textColor);
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException || ex is ArgumentException) { }
    }
}