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
        public Color UnplayedFillColor { get; set; } = Color.FromArgb(80, 255, 255, 255);

        public bool IsGlowEnabled { get; set; }
        public bool IsScaleEnabled { get; set; }
        public bool IsFloatEnabled { get; set; }

        private const float FeatherWidth = 22f;

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
            var bounds = Line.SecondaryTextLayout.LayoutBounds;

            if (double.IsNaN(opacity) || opacity <= 0) return;

            var srcRect = new Rect(
                bounds.X + Line.SecondaryPosition.X,
                bounds.Y + Line.SecondaryPosition.Y,
                bounds.Width,
                bounds.Height);

            using var cropEffect = new CropEffect
            {
                Source = Line.UnplayedComposite,
                BorderMode = EffectBorderMode.Hard,
                SourceRectangle = srcRect
            };
            using var blurEffect = new GaussianBlurEffect
            {
                BlurAmount = (float)blur,
                Source = cropEffect,
                BorderMode = EffectBorderMode.Soft
            };
            using var opacityEffect = new OpacityEffect
            {
                Source = blurEffect,
                Opacity = (float)opacity
            };

            ds.DrawImage(opacityEffect, srcRect, srcRect);
        }

        private void DrawPrimaryText(ICanvasResourceCreator resourceCreator, CanvasDrawingSession ds)
        {
            if (Line?.PrimaryTextLayout == null) return;

            var bounds = Line.PrimaryTextLayout.LayoutBounds;
            var srcRect = new Rect(
                bounds.X + Line.PrimaryPosition.X,
                bounds.Y + Line.PrimaryPosition.Y,
                bounds.Width,
                bounds.Height);

            var blur = Line.BlurAmountTransition.Value;

            if (!IsPlaying)
            {
                var opacity = Math.Max(Line.PlayedPrimaryOpacityTransition.Value,
                    Line.UnplayedPrimaryOpacityTransition.Value);

                if (double.IsNaN(opacity) || opacity <= 0) return;

                DrawLineViaEffectChain(ds, srcRect, blur, opacity, UnplayedFillColor);
                return;
            }

            double playedOpacity = Line.PlayedPrimaryOpacityTransition.Value;
            double unplayedOpacity = Line.UnplayedPrimaryOpacityTransition.Value;

            // Draw unplayed (dim) layer
            if (unplayedOpacity > 0)
                DrawLineViaEffectChain(ds, srcRect, blur, unplayedOpacity, UnplayedFillColor);

            // Draw played (bright) layer
            if (playedOpacity > 0)
            {
                if (IsFloatEnabled || IsGlowEnabled || IsScaleEnabled)
                    DrawPlayedWithCharEffects(resourceCreator, ds, srcRect, playedOpacity);
                else
                    DrawPlayedSimple(resourceCreator, ds, srcRect, playedOpacity);
            }
        }

        private void DrawLineViaEffectChain(CanvasDrawingSession ds, Rect srcRect,
            double blur, double opacity, Color color)
        {
            if (Line == null) return;

            using var cropEffect = new CropEffect
            {
                Source = Line.UnplayedComposite,
                BorderMode = EffectBorderMode.Hard,
                SourceRectangle = srcRect
            };
            using var blurEffect = new GaussianBlurEffect
            {
                BlurAmount = (float)blur,
                Source = cropEffect,
                BorderMode = EffectBorderMode.Soft
            };
            using var opacityEffect = new OpacityEffect
            {
                Source = blurEffect,
                Opacity = (float)opacity
            };

            ds.DrawImage(opacityEffect, srcRect, srcRect);
        }

        private void DrawPlayedSimple(ICanvasResourceCreator resourceCreator, CanvasDrawingSession ds,
            Rect srcRect, double playedOpacity)
        {
            if (Line == null) return;

            double playedWidth = CalculatePlayedWidth();
            float progress = Math.Clamp((float)(playedWidth / srcRect.Width), 0f, 1f);

            if (progress <= 0 || playedOpacity <= 0) return;

            float featherEnd = Math.Min(progress + FeatherWidth / Math.Max(1f, (float)srcRect.Width), 1f);

            var stops = new CanvasGradientStop[]
            {
                new() { Position = 0f, Color = Colors.White },
                new() { Position = progress, Color = Colors.White },
                new() { Position = featherEnd, Color = Color.FromArgb(0, 255, 255, 255) },
                new() { Position = 1f, Color = Color.FromArgb(0, 255, 255, 255) },
            };

            using var gradientBrush = new CanvasLinearGradientBrush(resourceCreator, stops)
            {
                StartPoint = new Vector2((float)srcRect.X, (float)srcRect.Y),
                EndPoint = new Vector2((float)(srcRect.X + srcRect.Width), (float)srcRect.Y)
            };

            using var playedLayer = new CanvasCommandList(resourceCreator);
            using (var lds = playedLayer.CreateDrawingSession())
            {
                lds.FillRectangle(srcRect, Colors.White);
            }

            using var alphaMask = new AlphaMaskEffect
            {
                Source = playedLayer,
                AlphaMask = Line.CachedFill
            };
            using var tint = new TintEffect
            {
                Source = alphaMask,
                Color = PlayedFillColor.WithAlpha((byte)(255 * playedOpacity))
            };

            using (ds.CreateLayer(gradientBrush, srcRect))
            {
                ds.DrawImage(tint, srcRect, srcRect);
            }
        }

        private void DrawPlayedWithCharEffects(ICanvasResourceCreator resourceCreator, CanvasDrawingSession ds,
            Rect srcRect, double playedOpacity)
        {
            if (Line?.PrimaryRenderChars == null || Line.PrimaryRenderChars.Count == 0) return;

            foreach (var renderChar in Line.PrimaryRenderChars)
            {
                var charRect = renderChar.LayoutRect;
                var sourceCharRect = new Rect(
                    charRect.X + Line.PrimaryPosition.X,
                    charRect.Y + Line.PrimaryPosition.Y,
                    charRect.Width,
                    charRect.Height);

                bool isCharFullyPlayed = renderChar.GetPlayProgress(CurrentProgressMs) >= 1;
                bool isCharPlaying = renderChar.IsPlayingLastFrame;
                double charPlayedAlpha = (isCharFullyPlayed || isCharPlaying) ? playedOpacity : 0;
                double charProgressWidth = charRect.Width * renderChar.GetPlayProgress(CurrentProgressMs);

                if (charPlayedAlpha <= 0 && renderChar.GlowTransition.Value <= 0) continue;

                double scale = renderChar.ScaleTransition.Value;
                double glow = renderChar.GlowTransition.Value;
                double floatOffset = renderChar.FloatTransition.Value;
                var destCharRect = sourceCharRect.Scale(scale).AddY(floatOffset);

                if (charPlayedAlpha > 0)
                {
                    float charFeather = Math.Min(FeatherWidth, (float)charRect.Width * 0.3f);
                    float charAdvance = Math.Clamp((float)(charProgressWidth / charRect.Width), 0f, 1f);
                    float charFeatherEnd = Math.Min(charAdvance + charFeather / Math.Max(1f, (float)charRect.Width), 1f);

                    var charStops = new CanvasGradientStop[]
                    {
                        new() { Position = 0f, Color = PlayedFillColor.WithAlpha((byte)(255 * charPlayedAlpha)) },
                        new() { Position = charAdvance, Color = PlayedFillColor.WithAlpha((byte)(255 * charPlayedAlpha)) },
                        new() { Position = charFeatherEnd, Color = Color.FromArgb(0, 255, 255, 255) },
                        new() { Position = 1f, Color = Color.FromArgb(0, 255, 255, 255) },
                    };

                    using var charGradientBrush = new CanvasLinearGradientBrush(resourceCreator, charStops)
                    {
                        StartPoint = new Vector2((float)sourceCharRect.X, (float)sourceCharRect.Y),
                        EndPoint = new Vector2((float)(sourceCharRect.X + sourceCharRect.Width), (float)sourceCharRect.Y)
                    };

                    using var charLayer = new CanvasCommandList(resourceCreator);
                    using (var gds = charLayer.CreateDrawingSession())
                        gds.FillRectangle(sourceCharRect, Colors.White);

                    using var charAlphaMask = new AlphaMaskEffect
                    {
                        Source = charLayer,
                        AlphaMask = Line.CachedFill
                    };
                    using var charTint = new TintEffect
                    {
                        Source = charAlphaMask,
                        Color = PlayedFillColor.WithAlpha((byte)(255 * charPlayedAlpha))
                    };

                    // Draw glow under the character
                    if (glow > 0 && IsGlowEnabled)
                    {
                        var sourcePlayedRect = new Rect(sourceCharRect.X, sourceCharRect.Y,
                            sourceCharRect.Width * renderChar.ProgressPlayed, sourceCharRect.Height);
                        renderChar.Crop.Source = charTint;
                        renderChar.Crop.SourceRectangle = sourcePlayedRect;
                        renderChar.Glow.BlurAmount = (float)glow;
                        ds.DrawImage(renderChar.Glow,
                            destCharRect.Extend(destCharRect.Height),
                            sourceCharRect.Extend(sourceCharRect.Height));
                    }

                    using (ds.CreateLayer(charGradientBrush, sourceCharRect))
                    {
                        ds.DrawImage(charTint, destCharRect, sourceCharRect);
                    }
                }
                else if (glow > 0 && IsGlowEnabled)
                {
                    // Only glow, no played text for this character
                    var sourcePlayedRect = new Rect(sourceCharRect.X, sourceCharRect.Y,
                        sourceCharRect.Width * renderChar.ProgressPlayed, sourceCharRect.Height);

                    using var charLayer = new CanvasCommandList(resourceCreator);
                    using (var gds = charLayer.CreateDrawingSession())
                        gds.FillRectangle(sourceCharRect, PlayedFillColor.WithAlpha((byte)(255 * playedOpacity)));

                    using var charAlphaMask = new AlphaMaskEffect
                    {
                        Source = charLayer,
                        AlphaMask = Line.CachedFill
                    };

                    renderChar.Crop.Source = charAlphaMask;
                    renderChar.Crop.SourceRectangle = sourcePlayedRect;
                    renderChar.Glow.BlurAmount = (float)glow;
                    ds.DrawImage(renderChar.Glow,
                        destCharRect.Extend(destCharRect.Height),
                        sourceCharRect.Extend(sourceCharRect.Height));
                }
            }
        }

        private double CalculatePlayedWidth()
        {
            if (Line?.PrimaryTextLayout == null) return 0;
            var bounds = Line.PrimaryTextLayout.LayoutBounds;

            if (!Line.IsPrimaryHasRealSyllableInfo)
                return bounds.Width;

            double played = 0;
            foreach (var ch in Line.PrimaryRenderChars)
            {
                if (ch.IsPlayingLastFrame)
                {
                    played += ch.LayoutRect.Width * ch.GetPlayProgress(CurrentProgressMs);
                    break;
                }
                if (ch.GetPlayProgress(CurrentProgressMs) >= 1)
                    played += ch.LayoutRect.Width;
                else
                    break;
            }
            return played;
        }
    }
}
