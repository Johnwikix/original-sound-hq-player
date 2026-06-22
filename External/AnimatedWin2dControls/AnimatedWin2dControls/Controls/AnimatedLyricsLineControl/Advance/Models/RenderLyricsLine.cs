using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.Advance
{
    public class RenderLyricsLine : BaseRenderLyrics
    {
        public List<RenderLyricsChar> PrimaryRenderChars { get; private set; } = [];
        public List<RenderLyricsSyllable> PrimaryRenderSyllables { get; private set; } = [];

        public ValueTransition<double> BlurAmountTransition { get; set; }
        public ValueTransition<double> ScaleTransition { get; set; }

        public ValueTransition<double> PlayedPrimaryOpacityTransition { get; set; }
        public ValueTransition<double> UnplayedPrimaryOpacityTransition { get; set; }
        public ValueTransition<double> SecondaryOpacityTransition { get; set; }

        public ValueTransition<double> PrimaryXOffsetTransition { get; set; }
        public ValueTransition<double> SecondaryXOffsetTransition { get; set; }

        public ValueTransition<double> YOffsetTransition { get; set; }

        public CanvasTextLayout? PrimaryTextLayout { get; private set; }
        public CanvasTextLayout? SecondaryTextLayout { get; private set; }

        public Vector2 PrimaryPosition { get; set; }
        public Vector2 SecondaryPosition { get; set; }

        public Vector2 TopLeftPosition { get; set; }
        public Vector2 CenterPosition { get; set; }
        public Vector2 BottomRightPosition { get; set; }

        public CanvasGeometry? PrimaryCanvasGeometry { get; private set; }
        public CanvasGeometry? SecondaryCanvasGeometry { get; private set; }

        public string PrimaryText { get; set; } = "";
        public string SecondaryText { get; set; } = "";

        public CanvasCommandList? CachedStroke { get; private set; }
        public CanvasCommandList? CachedFill { get; private set; }

        public TintEffect? UnplayedFillTint { get; private set; }
        public TintEffect? UnplayedStrokeTint { get; private set; }
        public CompositeEffect? UnplayedComposite { get; private set; }

        public CanvasTextLayoutRegion[]? PrimaryTextRegions { get; private set; }
        public RenderLyricsRegion[]? RenderLyricsRegions { get; private set; }

        public double? PrimaryLineHeight => _cachedPrimaryLineHeight;
        private double _cachedPrimaryLineHeight;

        public bool IsPrimaryHasRealSyllableInfo { get; set; }

        public CropEffect? CachedCropEffect { get; private set; }
        public GaussianBlurEffect? CachedBlurEffect { get; private set; }
        public OpacityEffect? CachedOpacityEffect { get; private set; }

        public RenderLyricsLine()
        {
            var interpolator = EasingHelper.GetInterpolatorByEasingType<double>(EasingType.Sine);

            BlurAmountTransition = new(0, interpolator, 0.3);
            ScaleTransition = new(1.0, interpolator, 0.3);
            PlayedPrimaryOpacityTransition = new(0, interpolator, 0.3);
            UnplayedPrimaryOpacityTransition = new(0, interpolator, 0.3);
            SecondaryOpacityTransition = new(0, interpolator, 0.3);
            PrimaryXOffsetTransition = new(0, interpolator, 0.3);
            SecondaryXOffsetTransition = new(0, interpolator, 0.3);
            YOffsetTransition = new(0, interpolator, 0.3);
        }

        public void LoadFromLyricLine(LyricLine lyricLine, double nextLineStartMs)
        {
            PrimaryRenderSyllables.Clear();

            var words = CollectionsMarshal.AsSpan(lyricLine.Words);
            int totalLen = 0;
            for (int i = 0; i < words.Length; i++)
                totalLen += (words[i].Word ?? "null").Length;

            PrimaryText = string.Create(totalLen, lyricLine.Words, static (span, ws) =>
            {
                int pos = 0;
                foreach (var w in ws)
                {
                    var s = w.Word ?? "null";
                    s.AsSpan().CopyTo(span[pos..]);
                    pos += s.Length;
                }
            });
            SecondaryText = lyricLine.TransLateText ?? "";

            StartMs = lyricLine.StartMs;
            EndMs = nextLineStartMs;

            int charIndex = 0;
            bool hasRealSyllable = false;
            foreach (var word in lyricLine.Words)
            {
                var syllable = new RenderLyricsSyllable
                {
                    Text = word.Word,
                    StartMs = word.StartMs,
                    StartIndex = charIndex,
                };
                syllable.EndMs = syllable.StartMs + word.DurationMs;
                PrimaryRenderSyllables.Add(syllable);
                charIndex += word.Word.Length;

                if (word.DurationMs > 0)
                    hasRealSyllable = true;
            }
            IsPrimaryHasRealSyllableInfo = lyricLine.Words.Count > 0 && hasRealSyllable;
        }

        public void DisposeTextLayout()
        {
            PrimaryTextLayout?.Dispose();
            PrimaryTextLayout = null;

            SecondaryTextLayout?.Dispose();
            SecondaryTextLayout = null;
        }

        public void RecreateTextLayout(
            ICanvasResourceCreator resourceCreator,
            int originalTextFontSize,
            int translatedTextFontSize,
            string fontFamily,
            double maxWidth,
            double maxHeight,
            CanvasHorizontalAlignment horizontalAlignment,
            CanvasTextFormat? sharedFormat = null)
        {
            DisposeTextLayout();

            var format = sharedFormat ?? new CanvasTextFormat
            {
                FontFamily = fontFamily,
                FontWeight = new Windows.UI.Text.FontWeight(700),
                VerticalAlignment = CanvasVerticalAlignment.Top,
                WordWrapping = CanvasWordWrapping.WholeWord,
            };

            if (translatedTextFontSize > 0 && !string.IsNullOrWhiteSpace(SecondaryText))
            {
                format.FontSize = translatedTextFontSize;
                SecondaryTextLayout = new CanvasTextLayout(resourceCreator, SecondaryText, format, (float)maxWidth, (float)maxHeight)
                {
                    HorizontalAlignment = horizontalAlignment,
                    Options = CanvasDrawTextOptions.NoPixelSnap,
                };
            }

            format.FontSize = originalTextFontSize;
            PrimaryTextLayout = new CanvasTextLayout(resourceCreator, PrimaryText, format, (float)maxWidth, (float)maxHeight)
            {
                HorizontalAlignment = horizontalAlignment,
                Options = CanvasDrawTextOptions.NoPixelSnap,
            };
            PrimaryTextRegions = PrimaryTextLayout.GetCharacterRegions(0, PrimaryText.Length);
        }

        public void DisposeTextGeometry()
        {
            PrimaryCanvasGeometry?.Dispose();
            PrimaryCanvasGeometry = null;

            SecondaryCanvasGeometry?.Dispose();
            SecondaryCanvasGeometry = null;
        }

        public void RecreateTextGeometry()
        {
            DisposeTextGeometry();

            if (PrimaryTextLayout != null)
                PrimaryCanvasGeometry = CanvasGeometry.CreateText(PrimaryTextLayout);

            if (SecondaryTextLayout != null)
                SecondaryCanvasGeometry = CanvasGeometry.CreateText(SecondaryTextLayout);
        }

        public void RecreateRenderChars(int strokeWidth)
        {
            PrimaryRenderChars.Clear();
            foreach (var syllable in PrimaryRenderSyllables)
                syllable.ChildrenRenderLyricsChars.Clear();

            if (PrimaryTextLayout == null) return;

            var textLength = PrimaryText.Length;
            int syllableIdx = 0;
            var syllables = PrimaryRenderSyllables;

            for (int startCharIndex = 0; startCharIndex < textLength; startCharIndex++)
            {
                var region = PrimaryTextLayout.GetCharacterRegions(startCharIndex, 1)[0];
                var bounds = region.LayoutBounds.Extend(
                    startCharIndex == 0 ? strokeWidth : strokeWidth / 4f,
                    strokeWidth / 2f,
                    startCharIndex == textLength - 1 ? strokeWidth : strokeWidth / 4f,
                    strokeWidth / 2f);

                while (syllableIdx < syllables.Count && startCharIndex > syllables[syllableIdx].EndIndex)
                    syllableIdx++;
                if (syllableIdx >= syllables.Count) break;
                var syllable = syllables[syllableIdx];

                var avgCharDuration = syllable.DurationMs / syllable.Length;
                var charStartMs = syllable.StartMs + (startCharIndex - syllable.StartIndex) * avgCharDuration;
                var charEndMs = charStartMs + avgCharDuration;

                var renderChar = new RenderLyricsChar(bounds)
                {
                    StartMs = charStartMs,
                    EndMs = charEndMs,
                    StartIndex = startCharIndex,
                };

                syllable.ChildrenRenderLyricsChars.Add(renderChar);
                PrimaryRenderChars.Add(renderChar);
            }
            _cachedPrimaryLineHeight = PrimaryRenderChars.Count > 0 ? PrimaryRenderChars[0].LayoutRect.Height : 0;
        }

        public void EnsureCaches(ICanvasResourceCreator resourceCreator, double strokeWidth)
        {
            if (CachedStroke != null && CachedFill != null) return;

            CachedFill = new CanvasCommandList(resourceCreator);
            using (var ds = CachedFill.CreateDrawingSession())
            {
                if (SecondaryTextLayout != null)
                    ds.DrawTextLayout(SecondaryTextLayout, SecondaryPosition, Microsoft.UI.Colors.White);
                if (PrimaryTextLayout != null)
                    ds.DrawTextLayout(PrimaryTextLayout, PrimaryPosition, Microsoft.UI.Colors.White);
            }

            CachedStroke = new CanvasCommandList(resourceCreator);
            if (strokeWidth > 0)
            {
                using var roundStrokeStyle = new CanvasStrokeStyle
                {
                    LineJoin = CanvasLineJoin.Round,
                    StartCap = CanvasCapStyle.Round,
                    EndCap = CanvasCapStyle.Round,
                };
                using var ds = CachedStroke.CreateDrawingSession();
                if (PrimaryCanvasGeometry != null)
                    ds.DrawGeometry(PrimaryCanvasGeometry, PrimaryPosition, Microsoft.UI.Colors.White, (float)strokeWidth, roundStrokeStyle);
                if (SecondaryCanvasGeometry != null)
                    ds.DrawGeometry(SecondaryCanvasGeometry, SecondaryPosition, Microsoft.UI.Colors.White, (float)strokeWidth, roundStrokeStyle);
            }

            UnplayedFillTint = new TintEffect { Source = CachedFill, Color = Microsoft.UI.Colors.White };
            UnplayedStrokeTint = new TintEffect { Source = CachedStroke, Color = Microsoft.UI.Colors.White };
            UnplayedComposite = new CompositeEffect
            {
                Sources = { UnplayedStrokeTint, UnplayedFillTint },
                Mode = CanvasComposite.SourceOver
            };

            CachedCropEffect ??= new CropEffect { Source = UnplayedComposite, BorderMode = EffectBorderMode.Hard };
            CachedBlurEffect ??= new GaussianBlurEffect { Source = CachedCropEffect, BorderMode = EffectBorderMode.Soft };
            CachedOpacityEffect ??= new OpacityEffect { Source = CachedBlurEffect };

            if (PrimaryTextRegions != null && (RenderLyricsRegions == null || RenderLyricsRegions.Length != PrimaryTextRegions.Length))
            {
                DisposeRenderLyricsRegions();
                RenderLyricsRegions = new RenderLyricsRegion[PrimaryTextRegions.Length];
                for (int i = 0; i < PrimaryTextRegions.Length; i++)
                {
                    RenderLyricsRegions[i] = new RenderLyricsRegion(CachedFill, CachedStroke);
                }
            }
        }

        public void DisposeCaches()
        {
            UnplayedComposite?.Dispose();
            UnplayedStrokeTint?.Dispose();
            UnplayedFillTint?.Dispose();
            CachedStroke?.Dispose();
            CachedFill?.Dispose();
            CachedCropEffect?.Dispose();
            CachedBlurEffect?.Dispose();
            CachedOpacityEffect?.Dispose();

            UnplayedComposite = null;
            UnplayedStrokeTint = null;
            UnplayedFillTint = null;
            CachedStroke = null;
            CachedFill = null;
            CachedCropEffect = null;
            CachedBlurEffect = null;
            CachedOpacityEffect = null;

            DisposeRenderLyricsRegions();
            DisposePrimaryRenderCharsEffects();
        }

        private void DisposeRenderLyricsRegions()
        {
            if (RenderLyricsRegions != null)
            {
                foreach (var region in RenderLyricsRegions)
                    region?.Dispose();
                RenderLyricsRegions = null;
            }
        }

        private void DisposePrimaryRenderCharsEffects()
        {
            foreach (var ch in PrimaryRenderChars)
                ch?.DisposeEffects();
        }

        public void Update(TimeSpan elapsedTime)
        {
            ScaleTransition.Update(elapsedTime);
            BlurAmountTransition.Update(elapsedTime);

            PlayedPrimaryOpacityTransition.Update(elapsedTime);
            UnplayedPrimaryOpacityTransition.Update(elapsedTime);
            SecondaryOpacityTransition.Update(elapsedTime);

            PrimaryXOffsetTransition.Update(elapsedTime);
            SecondaryXOffsetTransition.Update(elapsedTime);
            YOffsetTransition.Update(elapsedTime);
        }
    }
}
