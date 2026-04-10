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

public partial class TextElasticEffect : ITextEffect
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

        for (int i = 0; i < diffResults.Count; i++)
        {
            var diffResult = diffResults[i];

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

        float opacity = Easing.UpdateProgress(newCluster.Progress, Easing.EasingFunction.CubicOut);
        if (opacity <= 0f) return;

        float bounce = Easing.UpdateProgress(1.0f - newCluster.Progress, Easing.EasingFunction.ElasticIn);

        var c = Color.FromArgb((byte)(textColor.A * Math.Clamp(opacity, 0f, 1f)),
            textColor.R, textColor.G, textColor.B);

        ds.Transform = Matrix3x2.CreateTranslation(0,
            (float)newCluster.LayoutBounds.Height * 0.5f * bounce);

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

        float opacity = Easing.UpdateProgress(1.0f - oldCluster.Progress, Easing.EasingFunction.CubicIn);
        if (opacity <= 0f) return;

        float bounce = Easing.UpdateProgress(oldCluster.Progress, Easing.EasingFunction.ElasticOut);

        var c = Color.FromArgb((byte)(textColor.A * Math.Clamp(opacity, 0f, 1f)),
            textColor.R, textColor.G, textColor.B);

        ds.Transform = Matrix3x2.CreateTranslation(0,
            (float)oldCluster.LayoutBounds.Height * 0.5f * bounce);

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

        float p = Easing.UpdateProgress(oldCluster.Progress, Easing.EasingFunction.ElasticOut);

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

        // 旧字符：向上弹出淡出
        float oldOpacity = Easing.UpdateProgress(1.0f - oldCluster.Progress, Easing.EasingFunction.CubicIn);
        if (oldOpacity > 0f)
        {
            float oldBounce = Easing.UpdateProgress(oldCluster.Progress, Easing.EasingFunction.ElasticOut);

            var oldC = Color.FromArgb((byte)(textColor.A * Math.Clamp(oldOpacity, 0f, 1f)),
                textColor.R, textColor.G, textColor.B);

            ds.Transform = Matrix3x2.CreateTranslation(0,
                (float)oldCluster.LayoutBounds.Height * 0.5f * oldBounce);

            ds.DrawText(
                oldCluster.IsTrimmed ? oldTextLayout.GenerateTrimmingSign() : oldCluster.Characters,
                (float)oldCluster.DrawBounds.X,
                (float)oldCluster.DrawBounds.Y,
                oldC, textFormat);

            ds.Transform = Matrix3x2.Identity;
        }

        // 新字符：从下方弹入淡入
        float newOpacity = Easing.UpdateProgress(newCluster.Progress, Easing.EasingFunction.CubicOut);
        if (newOpacity > 0f)
        {
            float newBounce = Easing.UpdateProgress(1.0f - newCluster.Progress, Easing.EasingFunction.ElasticIn);

            var newC = Color.FromArgb((byte)(textColor.A * Math.Clamp(newOpacity, 0f, 1f)),
                textColor.R, textColor.G, textColor.B);

            ds.Transform = Matrix3x2.CreateTranslation(0,
                (float)newCluster.LayoutBounds.Height * newBounce);

            ds.DrawText(
                newCluster.IsTrimmed ? newTextLayout.GenerateTrimmingSign() : newCluster.Characters,
                (float)newCluster.DrawBounds.X,
                (float)newCluster.DrawBounds.Y,
                newC, textFormat);

            ds.Transform = Matrix3x2.Identity;
        }
    }
}
