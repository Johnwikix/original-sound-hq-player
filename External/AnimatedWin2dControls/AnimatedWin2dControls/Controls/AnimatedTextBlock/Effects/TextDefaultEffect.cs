using AnimatedWin2dControls.Controls.AnimatedTextBlock.Enums;
using AnimatedWin2dControls.Controls.AnimatedTextBlock.Internals;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Text;
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
    GraphemeCluster oldCluster, GraphemeCluster newCluster,
    CanvasTextLayout oldTextLayout, CanvasTextLayout newTextLayout,
    CanvasTextFormat textFormat, Color textColor,
    CanvasLinearGradientBrush gradientBrush)
    {
        if (newCluster == null || newTextLayout == null) return;

        float p = Easing.UpdateProgress(newCluster.Progress, Easing.EasingFunction.CubicOut);
        if (p <= 0f) return; // 完全透明时跳过绘制

        // 用调色 alpha 替代 CreateLayer（避免离屏 RT）
        var c = Color.FromArgb((byte)(textColor.A * p), textColor.R, textColor.G, textColor.B);

        ds.Transform = Matrix3x2.CreateScale(p,
            new Vector2(
                (float)(newCluster.LayoutBounds.X + newCluster.LayoutBounds.Width * 0.5),
                (float)newCluster.LayoutBounds.Bottom));

        ds.DrawText(
            newCluster.IsTrimmed ? newTextLayout.GenerateTrimmingSign() : newCluster.Characters,
            (float)newCluster.DrawBounds.X,
            (float)newCluster.DrawBounds.Y,
            c, textFormat);

        ds.Transform = Matrix3x2.Identity;
    }

    private void DrawRemove(CanvasDrawingSession ds,
        GraphemeCluster oldCluster, GraphemeCluster newCluster,
        CanvasTextLayout oldTextLayout, CanvasTextLayout newTextLayout,
        CanvasTextFormat textFormat, Color textColor,
        CanvasLinearGradientBrush gradientBrush)
    {
        if (oldCluster == null || oldTextLayout == null) return;

        float p = Easing.UpdateProgress(oldCluster.Progress, Easing.EasingFunction.CubicOut);
        float alpha = 1f - p;
        if (alpha <= 0f) return;

        var c = Color.FromArgb((byte)(textColor.A * alpha), textColor.R, textColor.G, textColor.B);

        ds.Transform = Matrix3x2.CreateScale(alpha,
            new Vector2(
                (float)(oldCluster.LayoutBounds.X + oldCluster.LayoutBounds.Width * 0.5),
                (float)oldCluster.LayoutBounds.Bottom));

        ds.DrawText(
            oldCluster.IsTrimmed ? oldTextLayout.GenerateTrimmingSign() : oldCluster.Characters,
            (float)oldCluster.DrawBounds.X,
            (float)oldCluster.DrawBounds.Y,
            c, textFormat);

        ds.Transform = Matrix3x2.Identity;
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
        if (p <= 0f)
        {
            // 还没开始动画，画在原位
            ds.DrawText(
                oldCluster.IsTrimmed ? oldTextLayout.GenerateTrimmingSign() : oldCluster.Characters,
                (float)oldCluster.DrawBounds.X,
                (float)oldCluster.DrawBounds.Y,
                textColor, textFormat);
            return;
        }

        var oX = oldCluster.DrawBounds.X;
        var oY = oldCluster.DrawBounds.Y;
        var dX = newCluster.DrawBounds.X - oX;
        var dY = newCluster.DrawBounds.Y - oY;

        ds.DrawText(
            oldCluster.IsTrimmed ? oldTextLayout.GenerateTrimmingSign() : oldCluster.Characters,
            (float)(oX + dX * p),
            (float)(oY + dY * p),
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
        float newP = Easing.UpdateProgress(newCluster.Progress, Easing.EasingFunction.CubicOut);

        // 旧字符：淡出 + 缩小，alpha = 1-oldP
        float oldAlpha = 1f - oldP;
        if (oldAlpha > 0f)
        {
            var oldColor = Color.FromArgb(
                (byte)(textColor.A * oldAlpha),
                textColor.R, textColor.G, textColor.B);

            ds.Transform = Matrix3x2.CreateScale(oldAlpha,
                new Vector2(
                    (float)(oldCluster.LayoutBounds.X + oldCluster.LayoutBounds.Width * 0.5),
                    (float)oldCluster.LayoutBounds.Bottom));

            ds.DrawText(
                oldCluster.IsTrimmed ? oldTextLayout.GenerateTrimmingSign() : oldCluster.Characters,
                (float)oldCluster.DrawBounds.X,
                (float)oldCluster.DrawBounds.Y,
                oldColor, textFormat);

            ds.Transform = Matrix3x2.Identity;
        }

        // 新字符：淡入 + 放大，alpha = newP
        if (newP > 0f)
        {
            var newColor = Color.FromArgb(
                (byte)(textColor.A * newP),
                textColor.R, textColor.G, textColor.B);

            ds.Transform = Matrix3x2.CreateScale(newP,
                new Vector2(
                    (float)(newCluster.LayoutBounds.X + newCluster.LayoutBounds.Width * 0.5),
                    (float)newCluster.LayoutBounds.Bottom));

            ds.DrawText(
                newCluster.IsTrimmed ? newTextLayout.GenerateTrimmingSign() : newCluster.Characters,
                (float)newCluster.DrawBounds.X,
                (float)newCluster.DrawBounds.Y,
                newColor, textFormat);

            ds.Transform = Matrix3x2.Identity;
        }
    }
}