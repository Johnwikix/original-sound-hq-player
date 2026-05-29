using AnimatedWin2dControls.Controls.AnimatedTextBlock.Enums;
using AnimatedWin2dControls.Controls.AnimatedTextBlock.Internals;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Text;
using System;
using System.Collections.Generic;
using System.Numerics;
using Windows.UI;

namespace AnimatedWin2dControls.Controls.AnimatedTextBlock.Effects;

public partial class TextMotionBlurEffect : ITextEffect
{
    public TimeSpan AnimationDuration { get; set; } = TimeSpan.FromMilliseconds(800);

    public TimeSpan DelayPerCluster { get; set; } = TimeSpan.FromMilliseconds(10);

    public void DrawText(string oldText,
        string newText,
        List<TextDiffResult> diffResults,
        CanvasTextLayout oldTextLayout,
        CanvasTextLayout newTextLayout,
        CanvasTextFormat textFormat,
        Color textColor,
        CanvasLinearGradientBrush gradientBrush,
        AnimatedTextBlockRedrawState state,
        CanvasDrawingSession drawingSession)
    {
        if (diffResults == null)
            return;

        var ds = drawingSession;

        if (state == AnimatedTextBlockRedrawState.Idle)
        {
            DrawIdle(ds,
                oldTextLayout,
                newTextLayout,
                textFormat,
                textColor,
                gradientBrush);

            return;
        }

        foreach (var diffResult in diffResults)
        {
            switch (diffResult.Type)
            {
                case AnimatedTextBlockDiffOperationType.Insert:
                    DrawInsert(ds,
                        diffResult.OldGlyphCluster,
                        diffResult.NewGlyphCluster,
                        oldTextLayout,
                        newTextLayout,
                        textFormat,
                        textColor,
                        gradientBrush);
                    break;
                case AnimatedTextBlockDiffOperationType.Remove:
                    DrawRemove(ds,
                        diffResult.OldGlyphCluster,
                        diffResult.NewGlyphCluster,
                        oldTextLayout,
                        newTextLayout,
                        textFormat,
                        textColor,
                        gradientBrush);
                    break;
                case AnimatedTextBlockDiffOperationType.Stay:
                case AnimatedTextBlockDiffOperationType.Move:
                    DrawMove(ds,
                        diffResult.OldGlyphCluster,
                        diffResult.NewGlyphCluster,
                        oldTextLayout,
                        newTextLayout,
                        textFormat,
                        textColor,
                        gradientBrush);
                    break;
                case AnimatedTextBlockDiffOperationType.Update:
                    DrawUpdate(ds,
                        diffResult.OldGlyphCluster,
                        diffResult.NewGlyphCluster,
                        oldTextLayout,
                        newTextLayout,
                        textFormat,
                        textColor,
                        gradientBrush);
                    break;
            }
        }
    }

    private void DrawIdle(CanvasDrawingSession ds,
        CanvasTextLayout oldTextLayout,
        CanvasTextLayout newTextLayout,
        CanvasTextFormat textFormat,
        Color textColor,
        CanvasLinearGradientBrush gradientBrush)
    {
        if (newTextLayout == null) return;
        try
        {
            ds.Transform = Matrix3x2.Identity;
            ds.DrawTextLayout(newTextLayout, 0, 0, textColor);
        }
        catch (Exception ex) when (ex is ObjectDisposedException || ex is ArgumentException) { }
    }

    private void DrawInsert(CanvasDrawingSession ds,
    GraphemeCluster oldCluster, GraphemeCluster newCluster,
    CanvasTextLayout oldTextLayout, CanvasTextLayout newTextLayout,
    CanvasTextFormat textFormat, Color textColor,
    CanvasLinearGradientBrush gradientBrush)
    {
        if (newCluster == null || newTextLayout == null) return;

        float t = 1.0f - Easing.UpdateProgress(newCluster.Progress, Easing.EasingFunction.CubicOut);
        if (t >= 1f) return;

        using var cl = new CanvasCommandList(ds);
        using (var clds = cl.CreateDrawingSession())
        {
            clds.Transform = Matrix3x2.CreateTranslation(0,
                (float)(newCluster.LayoutBounds.Height * t));
            clds.DrawText(
                newCluster.IsTrimmed ? newTextLayout.GenerateTrimmingSign() : newCluster.Characters,
                (float)newCluster.DrawBounds.X,
                (float)newCluster.DrawBounds.Y,
                textColor, textFormat);
        }

        using var fx = new DirectionalBlurEffect
        {
            Source = cl,
            Angle = DegreesToRadians(90),
            BlurAmount = (float)(t * newCluster.DrawBounds.Height * 0.5f),
            Optimization = EffectOptimization.Speed
        };

        using (ds.CreateLayer(1.0f - t))
            ds.DrawImage(fx);
    }

    private void DrawRemove(CanvasDrawingSession ds,
        GraphemeCluster oldCluster, GraphemeCluster newCluster,
        CanvasTextLayout oldTextLayout, CanvasTextLayout newTextLayout,
        CanvasTextFormat textFormat, Color textColor,
        CanvasLinearGradientBrush gradientBrush)
    {
        if (oldCluster == null || oldTextLayout == null) return;

        float p = Easing.UpdateProgress(oldCluster.Progress, Easing.EasingFunction.CubicOut);
        if (p >= 1f) return;

        using var cl = new CanvasCommandList(ds);
        using (var clds = cl.CreateDrawingSession())
        {
            clds.Transform = Matrix3x2.CreateTranslation(0,
                (float)(-oldCluster.LayoutBounds.Height * 0.5 * p));
            clds.DrawText(
                oldCluster.IsTrimmed ? oldTextLayout.GenerateTrimmingSign() : oldCluster.Characters,
                (float)oldCluster.DrawBounds.X,
                (float)oldCluster.DrawBounds.Y,
                textColor, textFormat);
        }

        using var fx = new DirectionalBlurEffect
        {
            Source = cl,
            Angle = DegreesToRadians(90),
            BlurAmount = (float)(p * oldCluster.DrawBounds.Height * 0.5f),
            Optimization = EffectOptimization.Speed
        };

        using (ds.CreateLayer(1.0f - p))
            ds.DrawImage(fx);
    }

    private void DrawMove(CanvasDrawingSession ds,
        GraphemeCluster oldCluster, GraphemeCluster newCluster,
        CanvasTextLayout oldTextLayout, CanvasTextLayout newTextLayout,
        CanvasTextFormat textFormat, Color textColor,
        CanvasLinearGradientBrush gradientBrush)
    {
        if (oldCluster == null || newCluster == null) return;
        if (oldTextLayout == null || newTextLayout == null) return;

        float p = Easing.UpdateProgress(oldCluster.Progress, Easing.EasingFunction.CubicOut);

        ds.DrawText(
            oldCluster.IsTrimmed ? oldTextLayout.GenerateTrimmingSign() : oldCluster.Characters,
            (float)(oldCluster.DrawBounds.X + (newCluster.DrawBounds.X - oldCluster.DrawBounds.X) * p),
            (float)(oldCluster.DrawBounds.Y + (newCluster.DrawBounds.Y - oldCluster.DrawBounds.Y) * p),
            textColor, textFormat);
    }

    private void DrawUpdate(CanvasDrawingSession ds,
        GraphemeCluster oldCluster, GraphemeCluster newCluster,
        CanvasTextLayout oldTextLayout, CanvasTextLayout newTextLayout,
        CanvasTextFormat textFormat, Color textColor,
        CanvasLinearGradientBrush gradientBrush)
    {
        if (oldCluster == null || newCluster == null) return;
        if (oldTextLayout == null || newTextLayout == null) return;

        float oldP = Easing.UpdateProgress(oldCluster.Progress, Easing.EasingFunction.CubicOut);
        float newT = 1.0f - Easing.UpdateProgress(newCluster.Progress, Easing.EasingFunction.CubicOut);

        // 旧字符：向上位移 + 运动模糊淡出
        if (oldP < 1f)
        {
            using var oCl = new CanvasCommandList(ds);
            using (var clds = oCl.CreateDrawingSession())
            {
                clds.Transform = Matrix3x2.CreateTranslation(0,
                    (float)(-oldCluster.LayoutBounds.Height * 0.5 * oldP));
                clds.DrawText(
                    oldCluster.IsTrimmed ? oldTextLayout.GenerateTrimmingSign() : oldCluster.Characters,
                    (float)oldCluster.DrawBounds.X,
                    (float)oldCluster.DrawBounds.Y,
                    textColor, textFormat);
            }

            using var oldFx = new DirectionalBlurEffect
            {
                Source = oCl,
                Angle = DegreesToRadians(90),
                BlurAmount = (float)(oldP * oldCluster.DrawBounds.Height * 0.5f),
                Optimization = EffectOptimization.Speed
            };

            using (ds.CreateLayer(1.0f - oldP))
                ds.DrawImage(oldFx);
        }

        // 新字符：从下方位移进入 + 运动模糊淡入
        if (newT < 1f)
        {
            using var nCl = new CanvasCommandList(ds);
            using (var clds = nCl.CreateDrawingSession())
            {
                clds.Transform = Matrix3x2.CreateTranslation(0,
                    (float)(newCluster.LayoutBounds.Height * newT));
                clds.DrawText(
                    newCluster.IsTrimmed ? newTextLayout.GenerateTrimmingSign() : newCluster.Characters,
                    (float)newCluster.DrawBounds.X,
                    (float)newCluster.DrawBounds.Y,
                    textColor, textFormat);
            }

            using var newFx = new DirectionalBlurEffect
            {
                Source = nCl,
                Angle = DegreesToRadians(90),
                BlurAmount = (float)(newT * newCluster.DrawBounds.Height * 0.5f),
                Optimization = EffectOptimization.Speed
            };

            using (ds.CreateLayer(1.0f - newT))
                ds.DrawImage(newFx);
        }
    }

    private static float DegreesToRadians(float degrees)
    {
        float radians = ((MathF.PI / 180) * degrees);
        return (radians);
    }
}
