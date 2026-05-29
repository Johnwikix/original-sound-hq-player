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

public partial class TextPivotEffect : ITextEffect
{
    public TimeSpan AnimationDuration { get; set; } = TimeSpan.FromMilliseconds(800);

    public TimeSpan DelayPerCluster { get; set; } = TimeSpan.FromMilliseconds(30);

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
            clds.DrawText(
                newCluster.IsTrimmed ? newTextLayout.GenerateTrimmingSign() : newCluster.Characters,
                (float)newCluster.DrawBounds.X,
                (float)newCluster.DrawBounds.Y,
                textColor, textFormat);
        }

        using var fx = new Transform3DEffect
        {
            Source = cl,
            TransformMatrix = Matrix4x4.CreateRotationY(t,
                new Vector3(
                    (float)(newCluster.LayoutBounds.X + newCluster.LayoutBounds.Width * 0.5),
                    (float)(newCluster.LayoutBounds.Y + newCluster.LayoutBounds.Height * 0.5),
                    0))
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
            clds.DrawText(
                oldCluster.IsTrimmed ? oldTextLayout.GenerateTrimmingSign() : oldCluster.Characters,
                (float)oldCluster.DrawBounds.X,
                (float)oldCluster.DrawBounds.Y,
                textColor, textFormat);
        }

        using var fx = new Transform3DEffect
        {
            Source = cl,
            TransformMatrix = Matrix4x4.CreateRotationY(p,
                new Vector3(
                    (float)(oldCluster.LayoutBounds.X + oldCluster.LayoutBounds.Width * 0.5),
                    (float)(oldCluster.LayoutBounds.Y + oldCluster.LayoutBounds.Height * 0.5),
                    0))
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
        float pivotProgress = 0f;

        var oX = oldCluster.DrawBounds.X;
        var oY = oldCluster.DrawBounds.Y;
        var dX = newCluster.DrawBounds.X - oX;
        var dY = newCluster.DrawBounds.Y - oY;

        if (dX != 0)
        {
            pivotProgress = Easing.UpdateProgress(oldCluster.Progress * 2.0f, Easing.EasingFunction.SinusoidalOut);
            pivotProgress = (float)Math.Clamp(pivotProgress, 0, 0.5);
        }

        // pivotProgress == 0 时跳过离屏路径，直接绘制
        if (pivotProgress == 0f)
        {
            ds.DrawText(
                oldCluster.IsTrimmed ? oldTextLayout.GenerateTrimmingSign() : oldCluster.Characters,
                (float)(oX + dX * p),
                (float)(oY + dY * p),
                textColor, textFormat);
            return;
        }

        using var cl = new CanvasCommandList(ds);
        using (var clds = cl.CreateDrawingSession())
        {
            clds.DrawText(
                newCluster.IsTrimmed ? newTextLayout.GenerateTrimmingSign() : newCluster.Characters,
                (float)(oX + dX * p),
                (float)(oY + dY * p),
                textColor, textFormat);
        }

        // 原来 CreateLayer(1.0f) 是空操作，直接 DrawImage 即可
        using var fx = new Transform3DEffect
        {
            Source = cl,
            TransformMatrix = Matrix4x4.CreateRotationY(pivotProgress,
                new Vector3(
                    (float)(newCluster.LayoutBounds.X + newCluster.LayoutBounds.Width * 0.5),
                    (float)(newCluster.LayoutBounds.Y + newCluster.LayoutBounds.Height * 0.5),
                    0))
        };
        ds.DrawImage(fx);
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

        // 旧字符：旋转淡出
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

            using var oldFx = new Transform3DEffect
            {
                Source = oCl,
                TransformMatrix = Matrix4x4.CreateRotationY(oldP,
                    new Vector3(
                        (float)(oldCluster.LayoutBounds.X + oldCluster.LayoutBounds.Width * 0.5),
                        (float)(oldCluster.LayoutBounds.Y + oldCluster.LayoutBounds.Height * 0.5),
                        0))
            };
            using (ds.CreateLayer(1.0f - oldP))
                ds.DrawImage(oldFx);
        }

        // 新字符：旋转淡入（从下方滑入）
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

            using var newFx = new Transform3DEffect
            {
                Source = nCl,
                TransformMatrix = Matrix4x4.CreateRotationY(newT,
                    new Vector3(
                        (float)(newCluster.LayoutBounds.X + newCluster.LayoutBounds.Width * 0.5),
                        (float)(newCluster.LayoutBounds.Y + newCluster.LayoutBounds.Height * 0.5),
                        0))
            };
            using (ds.CreateLayer(1.0f - newT))
                ds.DrawImage(newFx);
        }
    }
}
