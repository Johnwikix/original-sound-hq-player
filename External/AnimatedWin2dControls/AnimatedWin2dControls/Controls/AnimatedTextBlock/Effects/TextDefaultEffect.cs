using AnimatedWin2dControls.Controls.AnimatedTextBlock;
using AnimatedWin2dControls.Controls.AnimatedTextBlock.Enums;
using AnimatedWin2dControls.Controls.AnimatedTextBlock.Internals;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using System;
using System.Collections.Generic;
using System.Numerics;
using Windows.UI;

namespace AnimatedWin2dControls.Controls.AnimatedTextBlock.Effects;

public partial class TextDefaultEffect : ITextEffect
{
    public TimeSpan AnimationDuration { get; set; } = TimeSpan.FromMilliseconds(800);

    public TimeSpan DelayPerCluster { get; set; } = TimeSpan.FromMilliseconds(10);

    //public void Update(string oldText,
    //    string newText,
    //    List<TextDiffResult> diffResults,
    //    CanvasTextLayout oldTextLayout,
    //    CanvasTextLayout newTextLayout,
    //    AnimatedTextBlockRedrawState state,
    //    ICanvasAnimatedControl canvas,
    //    CanvasAnimatedUpdateEventArgs args)
    //{
    //    // CanvasControl 模式下 Update 不再被调用，逻辑已移入 AnimatedTextBlock.OnRendering
    //    // 保留此方法以满足接口约定
    //}

    public void DrawText(string oldText,
        string newText,
        List<TextDiffResult> diffResults,
        CanvasTextLayout oldTextLayout,
        CanvasTextLayout newTextLayout,
        CanvasTextFormat textFormat,
        Color textColor,
        CanvasLinearGradientBrush gradientBrush,
        AnimatedTextBlockRedrawState state,
        CanvasDrawingSession drawingSession)   // CanvasControl 模式下传入 null，不要使用
    {
        if (diffResults == null || newTextLayout == null)
            return;

        var ds = drawingSession;

        if (state == AnimatedTextBlockRedrawState.Idle)
        {
            DrawIdle(ds, oldTextLayout, newTextLayout, textFormat, textColor, gradientBrush);
            return;
        }

        for (int i = 0; i < diffResults.Count; i++)
        {
            var diffResult = diffResults[i];

            switch (diffResult.Type)
            {
                case AnimatedTextBlockDiffOperationType.Insert:
                    DrawInsert(ds,
                        diffResult.OldGlyphCluster,
                        diffResult.NewGlyphCluster,
                        oldTextLayout, newTextLayout,
                        textFormat, textColor, gradientBrush);
                    break;

                case AnimatedTextBlockDiffOperationType.Remove:
                    DrawRemove(ds,
                        diffResult.OldGlyphCluster,
                        diffResult.NewGlyphCluster,
                        oldTextLayout, newTextLayout,
                        textFormat, textColor, gradientBrush);
                    break;

                case AnimatedTextBlockDiffOperationType.Stay:
                case AnimatedTextBlockDiffOperationType.Move:
                    DrawMove(ds,
                        diffResult.OldGlyphCluster,
                        diffResult.NewGlyphCluster,
                        oldTextLayout, newTextLayout,
                        textFormat, textColor, gradientBrush);
                    break;

                case AnimatedTextBlockDiffOperationType.Update:
                    DrawUpdate(ds,
                        diffResult.OldGlyphCluster,
                        diffResult.NewGlyphCluster,
                        oldTextLayout, newTextLayout,
                        textFormat, textColor, gradientBrush);
                    break;
            }
        }
    }

    // ── 各操作类型的绘制方法 ──────────────────────────────────────────────

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
        GraphemeCluster oldCluster,
        GraphemeCluster newCluster,
        CanvasTextLayout oldTextLayout,
        CanvasTextLayout newTextLayout,
        CanvasTextFormat textFormat,
        Color textColor,
        CanvasLinearGradientBrush gradientBrush)
    {
        if (newCluster == null || newTextLayout == null) return;

        float newProgress = Easing.UpdateProgress(newCluster.Progress, Easing.EasingFunction.CubicOut);

        using (ds.CreateLayer(newProgress))
        {
            ds.Transform = Matrix3x2.CreateScale(newProgress,
                new Vector2(
                    (float)(newCluster.LayoutBounds.X + newCluster.LayoutBounds.Width * 0.5),
                    (float)newCluster.LayoutBounds.Bottom));

            ds.DrawText(
                newCluster.IsTrimmed ? newTextLayout.GenerateTrimmingSign() : newCluster.Characters,
                (float)newCluster.DrawBounds.X,
                (float)newCluster.DrawBounds.Y,
                textColor,
                textFormat);

            ds.Transform = Matrix3x2.Identity;
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
        if (oldCluster == null || newCluster == null) return;
        if (oldTextLayout == null || newTextLayout == null) return;

        float progress = Easing.UpdateProgress(oldCluster.Progress, Easing.EasingFunction.CubicOut);

        var oX = oldCluster.DrawBounds.X;
        var oY = oldCluster.DrawBounds.Y;
        var dX = newCluster.DrawBounds.X - oX;
        var dY = newCluster.DrawBounds.Y - oY;

        ds.DrawText(
            oldCluster.IsTrimmed ? oldTextLayout.GenerateTrimmingSign() : oldCluster.Characters,
            (float)(oX + dX * progress),
            (float)(oY + dY * progress),
            textColor,
            textFormat);
    }

    private void DrawUpdate(CanvasDrawingSession ds,
        GraphemeCluster oldCluster,
        GraphemeCluster newCluster,
        CanvasTextLayout oldTextLayout,
        CanvasTextLayout newTextLayout,
        CanvasTextFormat textFormat,
        Color textColor,
        CanvasLinearGradientBrush gradientBrush)
    {
        if (oldCluster == null || newCluster == null) return;
        if (oldTextLayout == null || newTextLayout == null) return;

        float oldProgress = Easing.UpdateProgress(oldCluster.Progress, Easing.EasingFunction.CubicOut);
        float newProgress = Easing.UpdateProgress(newCluster.Progress, Easing.EasingFunction.CubicOut);

        // 旧字符淡出缩小
        using (ds.CreateLayer(1.0f - oldProgress))
        {
            ds.Transform = Matrix3x2.CreateScale(1.0f - oldProgress,
                new Vector2(
                    (float)(oldCluster.LayoutBounds.X + oldCluster.LayoutBounds.Width * 0.5),
                    (float)oldCluster.LayoutBounds.Bottom));

            ds.DrawText(
                oldCluster.IsTrimmed ? oldTextLayout.GenerateTrimmingSign() : oldCluster.Characters,
                (float)oldCluster.DrawBounds.X,
                (float)oldCluster.DrawBounds.Y,
                textColor,
                textFormat);

            ds.Transform = Matrix3x2.Identity;
        }

        // 新字符淡入放大
        using (ds.CreateLayer(newProgress))
        {
            ds.Transform = Matrix3x2.CreateScale(newProgress,
                new Vector2(
                    (float)(newCluster.LayoutBounds.X + newCluster.LayoutBounds.Width * 0.5),
                    (float)(newCluster.LayoutBounds.Bottom)));

            ds.DrawText(
                newCluster.IsTrimmed ? newTextLayout.GenerateTrimmingSign() : newCluster.Characters,
                (float)newCluster.DrawBounds.X,
                (float)newCluster.DrawBounds.Y,
                textColor,
                textFormat);

            ds.Transform = Matrix3x2.Identity;
        }
    }

    private void DrawRemove(CanvasDrawingSession ds,
        GraphemeCluster oldCluster,
        GraphemeCluster newCluster,
        CanvasTextLayout oldTextLayout,
        CanvasTextLayout newTextLayout,
        CanvasTextFormat textFormat,
        Color textColor,
        CanvasLinearGradientBrush gradientBrush)
    {
        if (oldCluster == null || oldTextLayout == null) return;

        float oldProgress = Easing.UpdateProgress(oldCluster.Progress, Easing.EasingFunction.CubicOut);

        using (ds.CreateLayer(1.0f - oldProgress))
        {
            ds.Transform = Matrix3x2.CreateScale(1.0f - oldProgress,
                new Vector2(
                    (float)(oldCluster.LayoutBounds.X + oldCluster.LayoutBounds.Width * 0.5),
                    (float)oldCluster.LayoutBounds.Bottom));

            ds.DrawText(
                oldCluster.IsTrimmed ? oldTextLayout.GenerateTrimmingSign() : oldCluster.Characters,
                (float)oldCluster.DrawBounds.X,
                (float)oldCluster.DrawBounds.Y,
                textColor,
                textFormat);

            ds.Transform = Matrix3x2.Identity;
        }
    }
}