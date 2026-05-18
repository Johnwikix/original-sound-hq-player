using System;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI;
using Windows.Foundation;
using Windows.UI;

namespace AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.V2
{
    public class LyricsLineRendererV2
    {
        public bool IsPlaying { get; set; }
        public double CurrentProgressMs { get; set; }
        public RenderLyricsLine? Line { get; set; }

        public Color PlayedFillColor { get; set; } = Colors.White;
        public Color UnplayedFillColor { get; set; } = Colors.Black;

        public bool IsGlowEnabled { get; set; }
        public bool IsScaleEnabled { get; set; }
        public bool IsFloatEnabled { get; set; }

        public void Draw(ICanvasResourceCreator resourceCreator, CanvasDrawingSession ds)
        {
            DrawSecondaryText(ds);
            DrawPrimaryText(resourceCreator, ds);
        }

        private static readonly float CropHorizonPadding = 10f;
        private static readonly float CropVerticalPadding = 5f;

        private void DrawSecondaryText(CanvasDrawingSession ds)
        {
            if (Line?.SecondaryTextLayout == null) return;

            var opacity = Line.SecondaryOpacityTransition.Value;
            var blur = Line.BlurAmountTransition.Value;
            if (double.IsNaN(opacity) || opacity <= 0) return;

            var bounds = Line.SecondaryTextLayout.LayoutBounds;
            var srcRect = new Rect(
                bounds.X + Line.SecondaryPosition.X - CropHorizonPadding,
                bounds.Y + Line.SecondaryPosition.Y - CropVerticalPadding,
                bounds.Width + CropHorizonPadding * 2, bounds.Height + CropVerticalPadding * 2);

            if (Line.CachedCropEffect is {} crop && Line.CachedBlurEffect is {} blurFx && Line.CachedOpacityEffect is {} opacityFx)
            {
                crop.SourceRectangle = srcRect;
                blurFx.BlurAmount = (float)blur;
                opacityFx.Opacity = (float)opacity;
                ds.DrawImage(opacityFx, srcRect, srcRect);
            }
        }

        private void DrawPrimaryText(ICanvasResourceCreator resourceCreator, CanvasDrawingSession ds)
        {
            if (Line?.PrimaryTextLayout == null || Line.PrimaryTextRegions == null) return;
            if (Line.UnplayedComposite == null) return;

            var bounds = Line.PrimaryTextLayout.LayoutBounds;
            var srcRect = new Rect(
                bounds.X + Line.PrimaryPosition.X - CropHorizonPadding,
                bounds.Y + Line.PrimaryPosition.Y - CropHorizonPadding,
                bounds.Width + CropHorizonPadding * 2, bounds.Height + CropHorizonPadding * 2);

            if (!IsPlaying)
            {
                var opacity = Math.Max(Line.PlayedPrimaryOpacityTransition.Value,
                    Line.UnplayedPrimaryOpacityTransition.Value);
                if (double.IsNaN(opacity) || opacity <= 0) return;

                var blur = Line.BlurAmountTransition.Value;
                if (Line.CachedCropEffect is {} crop && Line.CachedBlurEffect is {} blurFx && Line.CachedOpacityEffect is {} opacityFx)
                {
                    crop.SourceRectangle = srcRect;
                    blurFx.BlurAmount = (float)blur;
                    opacityFx.Opacity = (float)opacity;
                    ds.DrawImage(opacityFx, srcRect, srcRect);
                }
                return;
            }

            if (!Line.IsPrimaryHasRealSyllableInfo)
            {
                DrawFullLineRegion(resourceCreator, ds);
            }
            else
            {
                for (int i = 0; i < Line.PrimaryTextRegions.Length; i++)
                {
                    DrawSubLineRegion(resourceCreator, ds, i);
                }
            }
        }

        private void DrawFullLineRegion(ICanvasResourceCreator resourceCreator, CanvasDrawingSession ds)
        {
            if (Line?.PrimaryTextRegions == null || Line.RenderLyricsRegions == null) return;

            var bounds = Line.PrimaryTextLayout!.LayoutBounds;
            Rect fullRect = new(
                bounds.X + Line.PrimaryPosition.X,
                bounds.Y + Line.PrimaryPosition.Y,
                bounds.Width, bounds.Height);

            var playedOpacity = Line.PlayedPrimaryOpacityTransition.Value;
            var unplayedOpacity = Line.UnplayedPrimaryOpacityTransition.Value;

            float progress = Math.Clamp((float)Line.GetPlayProgress(CurrentProgressMs), 0f, 1f);

            var region = Line.RenderLyricsRegions[0];
            var stops = region.FillStops;
            stops[0].Position = 0; stops[0].Color = PlayedFillColor.WithAlpha((byte)(255 * playedOpacity));
            stops[1].Position = progress; stops[1].Color = PlayedFillColor.WithAlpha((byte)(255 * playedOpacity));
            stops[2].Position = progress + 0.05f; stops[2].Color = UnplayedFillColor.WithAlpha((byte)(255 * unplayedOpacity));
            stops[3].Position = 1; stops[3].Color = UnplayedFillColor.WithAlpha((byte)(255 * unplayedOpacity));

            float targetW = (float)(fullRect.X + fullRect.Width + 1);
            float targetH = (float)(fullRect.Y + fullRect.Height + 1);

            if (region.PrevFillLayer == null || region.CachedTargetWidth != targetW || region.CachedTargetHeight != targetH)
            {
                region.PrevFillLayer?.Dispose();
                region.PrevFillLayer = new CanvasRenderTarget((Microsoft.Graphics.Canvas.ICanvasResourceCreatorWithDpi)resourceCreator, targetW, targetH);
                region.CachedTargetWidth = targetW;
                region.CachedTargetHeight = targetH;
            }

            using var brush = new CanvasLinearGradientBrush(resourceCreator, stops)
            {
                StartPoint = new Vector2((float)fullRect.X, (float)fullRect.Y),
                EndPoint = new Vector2((float)(fullRect.X + fullRect.Width), (float)fullRect.Y)
            };
            using (var gds = region.PrevFillLayer.CreateDrawingSession())
            {
                gds.Clear(Microsoft.UI.Colors.Transparent);
                gds.FillRectangle(fullRect, brush);
            }

            region.FinalFillEffect.Source = region.PrevFillLayer;
            ds.DrawImage(region.FinalFillEffect);
        }

        private void DrawSubLineRegion(ICanvasResourceCreator resourceCreator, CanvasDrawingSession ds, int regionIndex)
        {
            if (Line?.PrimaryTextRegions == null) return;
            if (Line.RenderLyricsRegions == null || regionIndex >= Line.RenderLyricsRegions.Length) return;

            var subLineRegion = Line.PrimaryTextRegions[regionIndex];

            var playedOpacity = Line.PlayedPrimaryOpacityTransition.Value;
            var unplayedOpacity = Line.UnplayedPrimaryOpacityTransition.Value;

            var subRect = subLineRegion.LayoutBounds;
            Rect subLineRect = new(
                subRect.X + Line.PrimaryPosition.X,
                subRect.Y + Line.PrimaryPosition.Y,
                subRect.Width, subRect.Height);

            double playedWidth = 0;
            if (!Line.IsPrimaryHasRealSyllableInfo)
            {
                playedWidth = subRect.Width;
            }
            else
            {
                for (int ci = subLineRegion.CharacterIndex;
                    ci < subLineRegion.CharacterIndex + subLineRegion.CharacterCount; ci++)
                {
                    if (ci >= Line.PrimaryRenderChars.Count) return;
                    var ch = Line.PrimaryRenderChars[ci];
                    if (ch.IsPlayingLastFrame)
                    {
                        playedWidth += ch.LayoutRect.Width * ch.GetPlayProgress(CurrentProgressMs);
                        break;
                    }
                    if (ch.GetPlayProgress(CurrentProgressMs) >= 1)
                        playedWidth += ch.LayoutRect.Width;
                    else
                        break;
                }
            }

            float progressInRegion = Math.Clamp((float)(playedWidth / subRect.Width), 0f, 1f);
            float fadeInRegion = 1f / subLineRegion.CharacterCount * 0.5f;

            if (subLineRegion.CharacterIndex >= Line.PrimaryRenderChars.Count) return;
            float firstCharProgress = Math.Clamp(
                (float)Line.PrimaryRenderChars[subLineRegion.CharacterIndex].GetPlayProgress(CurrentProgressMs), 0f, 1f);

            var region = Line.RenderLyricsRegions[regionIndex];

            region.FillStops[0].Position = 0;
            region.FillStops[0].Color = PlayedFillColor.WithAlpha((byte)(255 * playedOpacity));
            region.FillStops[1].Position = progressInRegion;
            region.FillStops[1].Color = PlayedFillColor.WithAlpha((byte)(255 * playedOpacity));
            region.FillStops[2].Position = progressInRegion + fadeInRegion * firstCharProgress;
            region.FillStops[2].Color = UnplayedFillColor.WithAlpha((byte)(255 * unplayedOpacity));
            region.FillStops[3].Position = 1 + fadeInRegion;
            region.FillStops[3].Color = UnplayedFillColor.WithAlpha((byte)(255 * unplayedOpacity));

            float targetW = (float)(subLineRect.X + subLineRect.Width + 1);
            float targetH = (float)(subLineRect.Y + subLineRect.Height + 1);

            if (region.PrevFillLayer == null || region.CachedTargetWidth != targetW || region.CachedTargetHeight != targetH)
            {
                region.PrevFillLayer?.Dispose();
                region.PrevFillLayer = new CanvasRenderTarget((Microsoft.Graphics.Canvas.ICanvasResourceCreatorWithDpi)resourceCreator, targetW, targetH);
                region.CachedTargetWidth = targetW;
                region.CachedTargetHeight = targetH;
            }

            using var fillGradientBrush = new CanvasLinearGradientBrush(resourceCreator, region.FillStops)
            {
                StartPoint = new Vector2((float)subLineRect.X, (float)subLineRect.Y),
                EndPoint = new Vector2((float)(subLineRect.X + subLineRect.Width), (float)subLineRect.Y)
            };
            using (var gds = region.PrevFillLayer.CreateDrawingSession())
            {
                gds.Clear(Microsoft.UI.Colors.Transparent);
                gds.FillRectangle(subLineRect, fillGradientBrush);
            }

            region.FinalFillEffect.Source = region.PrevFillLayer;
            ICanvasImage finalOutputImage = region.FinalFillEffect;

            if (!IsFloatEnabled && !IsGlowEnabled && !IsScaleEnabled)
            {
                ds.DrawImage(finalOutputImage);
            }
            else
            {
                int endCharIndex = subLineRegion.CharacterIndex + subLineRegion.CharacterCount;
                for (int ci = subLineRegion.CharacterIndex; ci < endCharIndex; ci++)
                {
                    DrawSingleCharacter(ds, ci, finalOutputImage);
                }
            }
        }

        private void DrawSingleCharacter(CanvasDrawingSession ds, int charIndex, ICanvasImage source)
        {
            if (Line?.PrimaryRenderChars == null) return;
            if (charIndex >= Line.PrimaryRenderChars.Count) return;

            RenderLyricsChar renderChar = Line.PrimaryRenderChars[charIndex];

            var rect = renderChar.LayoutRect;
            var sourceCharRect = new Rect(
                rect.X + Line.PrimaryPosition.X,
                rect.Y + Line.PrimaryPosition.Y,
                rect.Width, rect.Height);

            double scale = renderChar.ScaleTransition.Value;
            double glow = renderChar.GlowTransition.Value;
            double floatOffset = renderChar.FloatTransition.Value;

            var destCharRect = sourceCharRect.Scale(scale).AddY(floatOffset);

            if (glow > 0)
            {
                var sourcePlayedCharRect = new Rect(
                    sourceCharRect.X,
                    sourceCharRect.Y,
                    sourceCharRect.Width * renderChar.ProgressPlayed,
                    sourceCharRect.Height);

                renderChar.Crop.Source = source;
                renderChar.Crop.SourceRectangle = sourcePlayedCharRect;
                renderChar.Glow.BlurAmount = (float)glow;

                ds.DrawImage(renderChar.Glow,
                    destCharRect.Extend(destCharRect.Height),
                    sourceCharRect.Extend(sourceCharRect.Height));
            }

            ds.DrawImage(source, destCharRect, sourceCharRect);
        }
    }
}
