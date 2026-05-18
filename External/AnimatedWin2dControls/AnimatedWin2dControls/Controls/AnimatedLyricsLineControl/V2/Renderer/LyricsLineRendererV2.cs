using System;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Text;
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

        private CanvasCommandList? _sharedFillCommandList;

        public void Draw(ICanvasResourceCreator resourceCreator, CanvasDrawingSession ds)
        {
            DrawSecondaryText(ds);
            DrawPrimaryText(resourceCreator, ds);
        }

        public void ClearSharedResources()
        {
            _sharedFillCommandList?.Dispose();
            _sharedFillCommandList = null;
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
                DrawSubLineRegionsWithSharedCL(resourceCreator, ds);
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

            using var brush = new CanvasLinearGradientBrush(resourceCreator, stops)
            {
                StartPoint = new Vector2((float)fullRect.X, (float)fullRect.Y),
                EndPoint = new Vector2((float)(fullRect.X + fullRect.Width), (float)fullRect.Y)
            };
            region.PrevFillLayer?.Dispose();
            region.PrevFillLayer = new CanvasCommandList(resourceCreator);
            using (var gds = region.PrevFillLayer.CreateDrawingSession())
                gds.FillRectangle(fullRect, brush);

            region.FinalFillEffect.Source = region.PrevFillLayer;
            ds.DrawImage(region.FinalFillEffect);
        }

        private void DrawSubLineRegionsWithSharedCL(ICanvasResourceCreator resourceCreator, CanvasDrawingSession ds)
        {
            if (Line?.PrimaryTextRegions == null || Line.RenderLyricsRegions == null) return;

            var regions = Line.PrimaryTextRegions;
            var renderRegions = Line.RenderLyricsRegions;
            int regionCount = regions.Length;

            _sharedFillCommandList?.Dispose();
            _sharedFillCommandList = new CanvasCommandList(resourceCreator);
            using (var gds = _sharedFillCommandList.CreateDrawingSession())
            {
                for (int i = 0; i < regionCount; i++)
                {
                    RecordRegionGradient(resourceCreator, gds, i);
                }
            }

            for (int i = 0; i < regionCount; i++)
            {
                DrawRegionFromSharedCL(ds, i);
            }
        }

        private void RecordRegionGradient(ICanvasResourceCreator resourceCreator, CanvasDrawingSession gds, int regionIndex)
        {
            if (Line?.PrimaryTextRegions == null || Line.RenderLyricsRegions == null) return;

            var subLineRegion = Line.PrimaryTextRegions[regionIndex];
            var renderRegion = Line.RenderLyricsRegions[regionIndex];

            var playedOpacity = Line.PlayedPrimaryOpacityTransition.Value;
            var unplayedOpacity = Line.UnplayedPrimaryOpacityTransition.Value;

            var subRect = subLineRegion.LayoutBounds;
            Rect subLineRect = new(
                subRect.X + Line.PrimaryPosition.X,
                subRect.Y + Line.PrimaryPosition.Y,
                subRect.Width, subRect.Height);

            double playedWidth = ComputeRegionPlayedWidth(subLineRegion);
            float progressInRegion = Math.Clamp((float)(playedWidth / subRect.Width), 0f, 1f);
            float fadeInRegion = 1f / subLineRegion.CharacterCount * 0.5f;

            float firstCharProgress = 1f;
            if (subLineRegion.CharacterIndex < Line.PrimaryRenderChars.Count)
                firstCharProgress = Math.Clamp(
                    (float)Line.PrimaryRenderChars[subLineRegion.CharacterIndex].GetPlayProgress(CurrentProgressMs), 0f, 1f);

            renderRegion.FillStops[0].Position = 0;
            renderRegion.FillStops[0].Color = PlayedFillColor.WithAlpha((byte)(255 * playedOpacity));
            renderRegion.FillStops[1].Position = progressInRegion;
            renderRegion.FillStops[1].Color = PlayedFillColor.WithAlpha((byte)(255 * playedOpacity));
            renderRegion.FillStops[2].Position = progressInRegion + fadeInRegion * firstCharProgress;
            renderRegion.FillStops[2].Color = UnplayedFillColor.WithAlpha((byte)(255 * unplayedOpacity));
            renderRegion.FillStops[3].Position = 1 + fadeInRegion;
            renderRegion.FillStops[3].Color = UnplayedFillColor.WithAlpha((byte)(255 * unplayedOpacity));

            using var fillGradientBrush = new CanvasLinearGradientBrush(resourceCreator, renderRegion.FillStops)
            {
                StartPoint = new Vector2((float)subLineRect.X, (float)subLineRect.Y),
                EndPoint = new Vector2((float)(subLineRect.X + subLineRect.Width), (float)subLineRect.Y)
            };

            gds.FillRectangle(subLineRect, fillGradientBrush);
        }

        private double ComputeRegionPlayedWidth(CanvasTextLayoutRegion subLineRegion)
        {
            if (Line == null) return 0;

            double playedWidth = 0;
            for (int ci = subLineRegion.CharacterIndex;
                ci < subLineRegion.CharacterIndex + subLineRegion.CharacterCount; ci++)
            {
                if (ci >= Line.PrimaryRenderChars.Count) break;
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
            return playedWidth;
        }

        private void DrawRegionFromSharedCL(CanvasDrawingSession ds, int regionIndex)
        {
            if (Line?.PrimaryTextRegions == null || Line.RenderLyricsRegions == null) return;
            if (_sharedFillCommandList == null) return;

            var subLineRegion = Line.PrimaryTextRegions[regionIndex];
            var renderRegion = Line.RenderLyricsRegions[regionIndex];

            var subRect = subLineRegion.LayoutBounds;
            Rect subLineRect = new(
                subRect.X + Line.PrimaryPosition.X,
                subRect.Y + Line.PrimaryPosition.Y,
                subRect.Width, subRect.Height);

            renderRegion.FillCrop ??= new CropEffect { BorderMode = EffectBorderMode.Hard };
            renderRegion.FillCrop.Source = _sharedFillCommandList;
            renderRegion.FillCrop.SourceRectangle = subLineRect;
            renderRegion.FinalFillEffect.Source = renderRegion.FillCrop;

            ICanvasImage finalOutputImage = renderRegion.FinalFillEffect;

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
