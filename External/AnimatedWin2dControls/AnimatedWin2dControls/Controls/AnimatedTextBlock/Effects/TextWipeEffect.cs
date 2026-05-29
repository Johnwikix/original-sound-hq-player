using AnimatedWin2dControls.Controls.AnimatedTextBlock.Enums;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Text;
using System;
using System.Collections.Generic;
using System.Numerics;
using Windows.UI;

namespace AnimatedWin2dControls.Controls.AnimatedTextBlock.Effects;

public enum EasingMode
{
    Linear,
    CubicIn,
    CubicOut,
    CubicInOut,
    QuartOut,
    SineInOut,
    BackOut // 带有一点回弹效果
}

public sealed class TextWipeEffect : ITextEffect
{
    public TimeSpan AnimationDuration { get; set; } = TimeSpan.FromMilliseconds(1000);
    public TimeSpan DelayPerCluster { get; set; } = TimeSpan.Zero;

    /// <summary>羽化带宽度（像素）。</summary>
    public float FeatherWidth { get; set; } = 24f;

    /// <summary>当前选用的缓动函数</summary>
    public EasingMode Easing { get; set; } = EasingMode.SineInOut;

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
        List<TextDiffResult> diffResults,
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
            DrawLayout(ds, newTextLayout, textColor, gradientBrush);
            return;
        }

        float W = (float)(oldTextLayout?.RequestedSize.Width ?? newTextLayout?.RequestedSize.Width ?? 0);
        float H = (float)(oldTextLayout?.RequestedSize.Height ?? newTextLayout?.RequestedSize.Height ?? 0);

        if (W <= 0 || H <= 0)
        {
            DrawLayout(ds, newTextLayout, textColor, gradientBrush);
            return;
        }

        // 应用选中的 Ease 算法
        float easedProgress = ApplyEasing(Progress, Easing);

        float scanX = W * easedProgress;
        float half = FeatherWidth * 0.5f;
        float featherL = MathF.Max(scanX - half, 0f);
        float featherR = MathF.Min(scanX + half, W);

        var device = ds.Device;

        if (oldTextLayout != null)
        {
            using var oldMask = MakeMask(device, H, featherL, 0f, featherR, 1f, W);
            using (ds.CreateLayer(oldMask))
                DrawLayout(ds, oldTextLayout, textColor, gradientBrush);
        }

        if (newTextLayout != null)
        {
            using var newMask = MakeMask(device, H, featherL, 1f, featherR, 0f, W);
            using (ds.CreateLayer(newMask))
                DrawLayout(ds, newTextLayout, textColor, gradientBrush);
        }
    }

    private static float ApplyEasing(float t, EasingMode mode)
    {
        return mode switch
        {
            EasingMode.Linear => t,
            EasingMode.CubicIn => t * t * t,
            EasingMode.CubicOut => 1f - MathF.Pow(1f - t, 3),
            EasingMode.CubicInOut => t < 0.5f ? 4f * t * t * t : 1f - MathF.Pow(-2f * t + 2f, 3) / 2f,
            EasingMode.QuartOut => 1f - MathF.Pow(1f - t, 4),
            EasingMode.SineInOut => -(MathF.Cos(MathF.PI * t) - 1f) / 2f,
            EasingMode.BackOut => 1f + 2.70158f * MathF.Pow(t - 1f, 3) + 1.70158f * MathF.Pow(t - 1f, 2),
            _ => t
        };
    }

    private static CanvasLinearGradientBrush MakeMask(
        ICanvasResourceCreator device,
        float h,
        float featherL, float alphaL,
        float featherR, float alphaR,
        float W)
    {
        float pL = featherL / W;
        float pR = featherR / W;

        var stops = new CanvasGradientStop[]
        {
            new() { Position = 0f,  Color = ToMaskColor(alphaL) },
            new() { Position = MathF.Max(0, pL),  Color = ToMaskColor(alphaL) },
            new() { Position = MathF.Min(1, pR),  Color = ToMaskColor(alphaR) },
            new() { Position = 1f,  Color = ToMaskColor(alphaR) },
        };

        return new CanvasLinearGradientBrush(device, stops)
        {
            StartPoint = new Vector2(0f, h * 0.5f),
            EndPoint = new Vector2(W, h * 0.5f),
        };
    }

    private static Color ToMaskColor(float alpha)
        => Color.FromArgb((byte)(Math.Clamp(alpha, 0, 1) * 255), 255, 255, 255);

    private static void DrawLayout(
        CanvasDrawingSession ds,
        CanvasTextLayout layout,
        Color textColor,
        CanvasLinearGradientBrush gradientBrush)
    {
        if (layout == null) return;
        try
        {
            if (gradientBrush != null)
                ds.DrawTextLayout(layout, Vector2.Zero, gradientBrush);
            else
                ds.DrawTextLayout(layout, Vector2.Zero, textColor);
        }
        catch { /* 忽略释放异常 */ }
    }
}
