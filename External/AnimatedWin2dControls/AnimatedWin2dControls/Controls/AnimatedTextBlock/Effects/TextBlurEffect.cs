using AnimatedWin2dControls.Controls.AnimatedTextBlock;
using AnimatedWin2dControls.Controls.AnimatedTextBlock.Enums;
using AnimatedWin2dControls.Controls.AnimatedTextBlock.Internals;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using System;
using System.Collections.Generic;
using System.Numerics;
using Windows.UI;

namespace AnimatedWin2dControls.Controls.AnimatedTextBlock.Effects;

public partial class TextBlurEffect : ITextEffect
{
    public TimeSpan AnimationDuration { get; set; } = TimeSpan.FromMilliseconds(800);

    public TimeSpan DelayPerCluster { get; set; } = TimeSpan.FromMilliseconds(20);

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

    // TextBlurEffect

    private void DrawInsert(CanvasDrawingSession ds,
        GraphemeCluster oldCluster, GraphemeCluster newCluster,
        CanvasTextLayout oldTextLayout, CanvasTextLayout newTextLayout,
        CanvasTextFormat textFormat, Color textColor,
        CanvasLinearGradientBrush gradientBrush)
    {
        if (newCluster == null || newTextLayout == null) return;

        float blurT = 1.0f - Easing.UpdateProgress(newCluster.Progress, Easing.EasingFunction.CubicOut);
        if (blurT >= 1f) return; // 完全透明，跳过

        float opacity = 1.0f - blurT;

        using var cl = new CanvasCommandList(ds);
        using (var clds = cl.CreateDrawingSession())
        {
            clds.DrawText(
                newCluster.IsTrimmed ? newTextLayout.GenerateTrimmingSign() : newCluster.Characters,
                (float)newCluster.DrawBounds.X,
                (float)newCluster.DrawBounds.Y,
                textColor, textFormat);
        }

        using var blurEffect = new GaussianBlurEffect
        {
            Source = cl,
            BlurAmount = (float)(blurT * newCluster.DrawBounds.Height * 0.5f),
            Optimization = EffectOptimization.Speed
        };

        using (ds.CreateLayer(opacity))
            ds.DrawImage(blurEffect);
    }

    private void DrawRemove(CanvasDrawingSession ds,
        GraphemeCluster oldCluster, GraphemeCluster newCluster,
        CanvasTextLayout oldTextLayout, CanvasTextLayout newTextLayout,
        CanvasTextFormat textFormat, Color textColor,
        CanvasLinearGradientBrush gradientBrush)
    {
        if (oldCluster == null || oldTextLayout == null) return;

        float p = Easing.UpdateProgress(oldCluster.Progress, Easing.EasingFunction.CubicOut);
        if (p >= 1f) return; // 完全透明，跳过

        float opacity = 1.0f - p;

        using var cl = new CanvasCommandList(ds);
        using (var clds = cl.CreateDrawingSession())
        {
            clds.DrawText(
                oldCluster.IsTrimmed ? oldTextLayout.GenerateTrimmingSign() : oldCluster.Characters,
                (float)oldCluster.DrawBounds.X,
                (float)oldCluster.DrawBounds.Y,
                textColor, textFormat);
        }

        using var blurEffect = new GaussianBlurEffect
        {
            Source = cl,
            BlurAmount = (float)(p * oldCluster.DrawBounds.Height * 0.5f),
            Optimization = EffectOptimization.Speed
        };

        using (ds.CreateLayer(opacity))
            ds.DrawImage(blurEffect);
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
        float blurT = 1.0f - Easing.UpdateProgress(newCluster.Progress, Easing.EasingFunction.CubicOut);

        // 旧字符：模糊淡出
        if (oldP < 1f)
        {
            using var oCl = new CanvasCommandList(ds);
            using (var clds = oCl.CreateDrawingSession())
            {
                clds.DrawText(
                    oldCluster.IsTrimmed ? oldTextLayout.GenerateTrimmingSign() : oldCluster.Characters,
                    (float)oldCluster.DrawBounds.X,
                    (float)oldCluster.DrawBounds.Y,
                    textColor, textFormat);
            }

            using var oldBlur = new GaussianBlurEffect
            {
                Source = oCl,
                BlurAmount = (float)(oldP * oldCluster.DrawBounds.Height * 0.5f),
                Optimization = EffectOptimization.Speed
            };

            using (ds.CreateLayer(1.0f - oldP))
                ds.DrawImage(oldBlur);
        }

        // 新字符：模糊淡入
        if (blurT < 1f)
        {
            using var nCl = new CanvasCommandList(ds);
            using (var clds = nCl.CreateDrawingSession())
            {
                clds.Transform = Matrix3x2.CreateTranslation(0,
                    (float)(newCluster.LayoutBounds.Height * blurT));

                clds.DrawText(
                    newCluster.IsTrimmed ? newTextLayout.GenerateTrimmingSign() : newCluster.Characters,
                    (float)newCluster.DrawBounds.X,
                    (float)newCluster.DrawBounds.Y,
                    textColor, textFormat);
            }

            using var newBlur = new GaussianBlurEffect
            {
                Source = nCl,
                BlurAmount = (float)(blurT * newCluster.DrawBounds.Height * 0.5f),
                Optimization = EffectOptimization.Speed
            };

            using (ds.CreateLayer(1.0f - blurT))
                ds.DrawImage(newBlur);
        }
    }

    private void DrawMove(CanvasDrawingSession ds,
        GraphemeCluster oldCluster,
        GraphemeCluster newCluster,
        CanvasTextLayout oldTextLayout,
        CanvasTextLayout newTextLayout,
        CanvasTextFormat textFormat,
        Color textColor,
        CanvasLinearGradientBrush gradientBrush)
    {
        if (oldCluster == null || newCluster == null)
        {
            return;
        }

        float oldProgress = Easing.UpdateProgress(oldCluster.Progress, Easing.EasingFunction.CubicOut);

        var oX = oldCluster.DrawBounds.X;
        var oY = oldCluster.DrawBounds.Y;
        var nX = newCluster.DrawBounds.X;
        var nY = newCluster.DrawBounds.Y;

        var dX = nX - oX;
        var dY = nY - oY;

        ds.DrawText(
            oldCluster.IsTrimmed
                ? oldTextLayout.GenerateTrimmingSign()
                : oldCluster.Characters,
            (float)(oX + dX * oldProgress),
            (float)(oY + dY * oldProgress),
            textColor,
            textFormat);
    }    

    private static float DegreesToRadians(float degrees)
    {
        float radians = ((MathF.PI / 180) * degrees);
        return (radians);
    }
}
