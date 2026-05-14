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
        public Color UnplayedFillColor { get; set; } = Colors.White;
        public Color PlayedStrokeColor { get; set; } = Colors.Transparent;
        public Color UnplayedStrokeColor { get; set; } = Colors.Transparent;
        public int StrokeWidth { get; set; }

        public bool IsGlowEnabled { get; set; }
        public bool IsScaleEnabled { get; set; }
        public bool IsFloatEnabled { get; set; }

        public void Draw(ICanvasResourceCreator resourceCreator, CanvasDrawingSession ds)
        {
            DrawSecondaryText(ds);
            DrawPrimaryText(resourceCreator, ds);
        }

        private void DrawSecondaryText(CanvasDrawingSession ds)
        {
            if (Line?.SecondaryTextLayout == null) return;

            var opacity = Line.SecondaryOpacityTransition.Value;
            var blur = Line.BlurAmountTransition.Value;
            var bounds = Line.SecondaryTextLayout.LayoutBounds.Extend(StrokeWidth / 2f);

            if (double.IsNaN(opacity) || opacity <= 0) return;

            var srcRect = new Rect(
                bounds.X + Line.SecondaryPosition.X,
                bounds.Y + Line.SecondaryPosition.Y,
                bounds.Width,
                bounds.Height);

            using var cropEffect = new CropEffect { Source = Line.UnplayedComposite, BorderMode = EffectBorderMode.Hard, SourceRectangle = srcRect };
            using var blurEffect = new GaussianBlurEffect { BlurAmount = (float)blur, Source = cropEffect, BorderMode = EffectBorderMode.Soft };
            using var opacityEffect = new OpacityEffect { Source = blurEffect, Opacity = (float)opacity };
            ds.DrawImage(opacityEffect, srcRect, srcRect);
        }

        private void DrawPrimaryText(ICanvasResourceCreator resourceCreator, CanvasDrawingSession ds)
        {
            if (Line?.PrimaryTextLayout == null) return;

            var bounds = Line.PrimaryTextLayout.LayoutBounds.Extend(StrokeWidth / 2f);
            var srcRect = new Rect(bounds.X + Line.PrimaryPosition.X, bounds.Y + Line.PrimaryPosition.Y, bounds.Width, bounds.Height);

            if (IsPlaying)
            {
                for (int i = 0; i < Line.PrimaryTextRegions?.Length; i++)
                {
                    DrawSubLineRegion(resourceCreator, ds, i);
                }
            }
            else
            {
                var opacity = Math.Max(Line.PlayedPrimaryOpacityTransition.Value, Line.UnplayedPrimaryOpacityTransition.Value);
                var blur = Line.BlurAmountTransition.Value;

                if (double.IsNaN(opacity) || opacity <= 0) return;

                using var cropEffect = new CropEffect { Source = Line.UnplayedComposite, BorderMode = EffectBorderMode.Hard, SourceRectangle = srcRect };
                using var blurEffect = new GaussianBlurEffect { BlurAmount = (float)blur, Source = cropEffect, BorderMode = EffectBorderMode.Soft };
                using var opacityEffect = new OpacityEffect { Source = blurEffect, Opacity = (float)opacity };
                ds.DrawImage(opacityEffect, srcRect, srcRect);
            }
        }

        private void DrawSubLineRegion(ICanvasResourceCreator resourceCreator, CanvasDrawingSession ds, int regionIndex)
        {
            if (Line == null) return;
            if (Line.PrimaryTextRegions == null) return;
            if (Line.RenderLyricsRegions == null || regionIndex >= Line.RenderLyricsRegions.Length) return;

            var subLineRegion = Line.PrimaryTextRegions[regionIndex];

            var playedOpacity = Line.PlayedPrimaryOpacityTransition.Value;
            var unplayedOpacity = Line.UnplayedPrimaryOpacityTransition.Value;

            var subLineLayoutBounds = subLineRegion.LayoutBounds.Extend(StrokeWidth, StrokeWidth / 2f);
            Rect subLineRect = new(
                subLineLayoutBounds.X + Line.PrimaryPosition.X,
                subLineLayoutBounds.Y + Line.PrimaryPosition.Y,
                subLineLayoutBounds.Width,
                subLineLayoutBounds.Height
            );

            double playedWidth = 0;
            if (!Line.IsPrimaryHasRealSyllableInfo)
            {
                playedWidth = subLineRegion.LayoutBounds.Width;
            }
            else
            {
                for (int i = subLineRegion.CharacterIndex; i < subLineRegion.CharacterIndex + subLineRegion.CharacterCount; i++)
                {
                    if (i >= Line.PrimaryRenderChars.Count) return;
                    var ch = Line.PrimaryRenderChars[i];
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

            float progressInRegion = Math.Clamp((float)(playedWidth / subLineRegion.LayoutBounds.Width), 0f, 1f);
            float fadeProgressInRegion = 1f / subLineRegion.CharacterCount * 0.5f;

            if (subLineRegion.CharacterIndex >= Line.PrimaryRenderChars.Count) return;
            float firstCharProgressInRegion = Math.Clamp((float)Line.PrimaryRenderChars[subLineRegion.CharacterIndex].GetPlayProgress(CurrentProgressMs), 0f, 1f);

            var region = Line.RenderLyricsRegions[regionIndex];

            var fillStops = region.FillStops;
            fillStops[0].Position = 0; fillStops[0].Color = PlayedFillColor.WithAlpha((byte)(255 * playedOpacity));
            fillStops[1].Position = progressInRegion; fillStops[1].Color = PlayedFillColor.WithAlpha((byte)(255 * playedOpacity));
            fillStops[2].Position = progressInRegion + fadeProgressInRegion * firstCharProgressInRegion; fillStops[2].Color = UnplayedFillColor.WithAlpha((byte)(255 * unplayedOpacity));
            fillStops[3].Position = 1 + fadeProgressInRegion; fillStops[3].Color = UnplayedFillColor.WithAlpha((byte)(255 * unplayedOpacity));

            var strokeStops = region.StrokeStops;
            strokeStops[0].Position = 0; strokeStops[0].Color = PlayedStrokeColor.WithAlpha((byte)(255 * playedOpacity));
            strokeStops[1].Position = progressInRegion; strokeStops[1].Color = PlayedStrokeColor.WithAlpha((byte)(255 * playedOpacity));
            strokeStops[2].Position = progressInRegion + fadeProgressInRegion * firstCharProgressInRegion; strokeStops[2].Color = UnplayedStrokeColor.WithAlpha((byte)(255 * unplayedOpacity));
            strokeStops[3].Position = 1 + fadeProgressInRegion; strokeStops[3].Color = UnplayedStrokeColor.WithAlpha((byte)(255 * unplayedOpacity));

            using var fillGradientBrush = new CanvasLinearGradientBrush(resourceCreator, fillStops)
            {
                StartPoint = new Vector2((float)subLineRect.X, (float)subLineRect.Y),
                EndPoint = new Vector2((float)(subLineRect.X + subLineRect.Width), (float)subLineRect.Y)
            };
            var fillGradientLayer = new CanvasCommandList(resourceCreator);
            using (var gds = fillGradientLayer.CreateDrawingSession())
            {
                gds.FillRectangle(subLineRect, fillGradientBrush);
            }

            region.FinalFillEffect.Source = fillGradientLayer;
            ICanvasImage finalOutputImage = region.FinalFillEffect;

            bool hasStroke = Line.CachedStroke != null && region.FinalStrokeEffect != null && region.CombinedEffect != null;

            if (hasStroke)
            {
                using var strokeGradientBrush = new CanvasLinearGradientBrush(resourceCreator, strokeStops)
                {
                    StartPoint = new Vector2((float)subLineRect.X, (float)subLineRect.Y),
                    EndPoint = new Vector2((float)(subLineRect.X + subLineRect.Width), (float)subLineRect.Y)
                };

                var strokeGradientLayer = new CanvasCommandList(resourceCreator);
                using (var gds = strokeGradientLayer.CreateDrawingSession())
                {
                    gds.FillRectangle(subLineRect, strokeGradientBrush);
                }

                region.FinalStrokeEffect!.Source = strokeGradientLayer;
                finalOutputImage = region.CombinedEffect!;
            }

            if (!IsFloatEnabled && !IsGlowEnabled && !IsScaleEnabled)
            {
                ds.DrawImage(finalOutputImage);
            }
            else
            {
                int endCharIndex = subLineRegion.CharacterIndex + subLineRegion.CharacterCount;
                for (int i = subLineRegion.CharacterIndex; i < endCharIndex; i++)
                {
                    DrawSingleCharacter(ds, i, finalOutputImage);
                }
            }
        }

        private void DrawSingleCharacter(CanvasDrawingSession ds, int charIndex, ICanvasImage source)
        {
            if (Line == null || Line.PrimaryTextLayout == null) return;
            if (charIndex >= Line.PrimaryRenderChars.Count) return;

            RenderLyricsChar renderChar = Line.PrimaryRenderChars[charIndex];

            var rect = renderChar.LayoutRect;
            var sourceCharRect = new Rect(
                rect.X + Line.PrimaryPosition.X,
                rect.Y + Line.PrimaryPosition.Y,
                rect.Width,
                rect.Height
            );

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
                    sourceCharRect.Height
                );

                renderChar.Crop.Source = source;
                renderChar.Crop.SourceRectangle = sourcePlayedCharRect;
                renderChar.Glow.BlurAmount = (float)glow;

                ds.DrawImage(renderChar.Glow, destCharRect.Extend(destCharRect.Height), sourceCharRect.Extend(sourceCharRect.Height));
            }

            ds.DrawImage(source, destCharRect, sourceCharRect);
        }
    }
}
