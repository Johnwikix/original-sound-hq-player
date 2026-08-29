using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using System;
using System.Collections.Generic;
using System.Numerics;
using Windows.Foundation;

namespace AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.Advance
{
    public class LyricsLayoutManager
    {
        public static void MeasureAndArrange(
            ICanvasResourceCreator resourceCreator,
            IList<RenderLyricsLine>? lines,
            int originalFontSize,
            int translatedFontSize,
            string fontFamily,
            CanvasHorizontalAlignment horizontalAlignment,
            double lyricsWidth,
            double lyricsHeight,
            int strokeWidth,
            int fontWeight = 700)
        {
            if (lines == null || resourceCreator == null) return;

            const float horizontalPadding = 20f;
            double currentX = horizontalPadding;
            double currentY = 0;
            double layoutWidth = lyricsWidth - horizontalPadding * 2;

            var shareFormat = new CanvasTextFormat
            {
                FontFamily = fontFamily,
                FontWeight = new Windows.UI.Text.FontWeight((ushort)fontWeight),
                VerticalAlignment = CanvasVerticalAlignment.Top,
                WordWrapping = CanvasWordWrapping.WholeWord,
            };

            foreach (var line in lines)
            {
                if (line == null) continue;

                double actualWidth = 0;

                line.RecreateTextLayout(
                    resourceCreator,
                    originalFontSize, translatedFontSize,
                    fontFamily,
                    layoutWidth, lyricsHeight,
                    horizontalAlignment,
                    shareFormat);

                if (strokeWidth > 0)
                    line.RecreateTextGeometry();
                line.DisposeCaches();

                line.TopLeftPosition = new Vector2((float)currentX, (float)currentY);

                if (line.PrimaryTextLayout != null)
                {
                    line.PrimaryPosition = new Vector2((float)currentX, (float)currentY);
                    currentY += line.PrimaryTextLayout.LayoutBounds.Height;
                    actualWidth = Math.Max(actualWidth, line.PrimaryTextLayout.LayoutBounds.Width);
                }

                if (line.SecondaryTextLayout != null)
                {
                    currentY += line.PrimaryTextLayout?.LayoutBounds.Height * 0.1 ?? 3;
                    line.SecondaryPosition = new Vector2((float)currentX, (float)currentY);
                    currentY += line.SecondaryTextLayout.LayoutBounds.Height;
                    actualWidth = Math.Max(actualWidth, line.SecondaryTextLayout.LayoutBounds.Width);
                }

                line.BottomRightPosition = new Vector2((float)currentX + (float)actualWidth, (float)currentY);

                line.TopLeftPosition = horizontalAlignment switch
                {
                    CanvasHorizontalAlignment.Left => line.TopLeftPosition,
                    CanvasHorizontalAlignment.Center => line.TopLeftPosition.AddX((float)((layoutWidth - actualWidth) / 2)),
                    CanvasHorizontalAlignment.Right => line.TopLeftPosition.AddX((float)(layoutWidth - actualWidth)),
                    _ => line.TopLeftPosition
                };

                line.BottomRightPosition = horizontalAlignment switch
                {
                    CanvasHorizontalAlignment.Left => line.BottomRightPosition,
                    CanvasHorizontalAlignment.Center => line.BottomRightPosition.AddX((float)((layoutWidth - actualWidth) / 2)),
                    CanvasHorizontalAlignment.Right => line.BottomRightPosition.AddX((float)(layoutWidth - actualWidth)),
                    _ => line.BottomRightPosition
                };

                double centerY = (line.TopLeftPosition.Y + line.BottomRightPosition.Y) / 2;
                line.CenterPosition = horizontalAlignment switch
                {
                    CanvasHorizontalAlignment.Left => new Vector2(0, (float)centerY),
                    CanvasHorizontalAlignment.Center => new Vector2((float)(lyricsWidth / 2), (float)centerY),
                    CanvasHorizontalAlignment.Right => new Vector2((float)(lyricsWidth), (float)centerY),
                    _ => new Vector2(0, (float)centerY),
                };

                currentY += originalFontSize * 0.75;

                line.RecreateRenderChars(strokeWidth);
            }
            shareFormat.Dispose();
        }

        public static double? CalculateTargetScrollOffset(IList<RenderLyricsLine>? lines, int playingLineIndex)
        {
            if (lines == null || lines.Count == 0) return null;
            if (playingLineIndex < 0 || playingLineIndex >= lines.Count) return null;
            var currentLine = lines[playingLineIndex];
            if (currentLine?.PrimaryTextLayout == null) return null;
            return -currentLine.CenterPosition.Y;
        }

        public static (int Start, int End) CalculateVisibleRange(
            IList<RenderLyricsLine>? lines,
            double currentScrollOffset,
            double lyricsY,
            double lyricsHeight,
            double canvasHeight,
            double playingLineTopOffsetFactor)
        {
            if (lines == null || lines.Count == 0) return (-1, -1);

            double offset = currentScrollOffset + lyricsY + lyricsHeight * playingLineTopOffsetFactor;

            int start = FindFirstVisibleLine(lines, offset);
            int end = FindLastVisibleLine(lines, offset, canvasHeight);

            if (start != -1 && end == -1)
                end = lines.Count - 1;

            return (start, end);
        }

        public static int FindMouseHoverLineIndex(
            IList<RenderLyricsLine>? lines,
            bool isMouseInLyricsArea,
            Point mousePosition,
            double currentScrollOffset,
            double lyricsHeight,
            double playingLineTopOffsetFactor)
        {
            if (!isMouseInLyricsArea || lines == null || lines.Count == 0) return -1;

            double yOffset = currentScrollOffset + lyricsHeight * playingLineTopOffsetFactor;

            int left = 0, right = lines.Count - 1, result = -1;
            while (left <= right)
            {
                int mid = (left + right) / 2;
                var line = lines[mid];
                if (line.PrimaryTextLayout == null) break;
                double lineBottomY = yOffset + line.BottomRightPosition.Y;
                if (lineBottomY >= mousePosition.Y)
                {
                    result = mid;
                    right = mid - 1;
                }
                else { left = mid + 1; }
            }

            if (result != -1)
            {
                var line = lines[result];
                double lineLeftX = line.TopLeftPosition.X;
                double lineRightX = line.BottomRightPosition.X;
                double lineTopY = yOffset + line.TopLeftPosition.Y;
                if (mousePosition.X < lineLeftX || mousePosition.X > lineRightX || mousePosition.Y < lineTopY)
                    result = -1;
            }

            return result;
        }

        private static int FindFirstVisibleLine(IList<RenderLyricsLine> lines, double offset)
        {
            int left = 0, right = lines.Count - 1, result = -1;
            while (left <= right)
            {
                int mid = (left + right) / 2;
                var line = lines[mid];
                if (line.PrimaryTextLayout == null) break;
                double value = offset + line.BottomRightPosition.Y;
                if (value >= 0) { result = mid; right = mid - 1; }
                else { left = mid + 1; }
            }
            return result;
        }

        private static int FindLastVisibleLine(IList<RenderLyricsLine> lines, double offset, double canvasHeight)
        {
            int left = 0, right = lines.Count - 1, result = -1;
            while (left <= right)
            {
                int mid = (left + right) / 2;
                var line = lines[mid];
                if (line.PrimaryTextLayout == null) break;
                double value = offset + line.BottomRightPosition.Y;
                if (value >= canvasHeight) { result = mid; right = mid - 1; }
                else { left = mid + 1; }
            }
            return result;
        }
    }
}
