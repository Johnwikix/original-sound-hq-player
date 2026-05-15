using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;

namespace AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.V2
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

        public double? PrimaryLineHeight => PrimaryRenderChars.FirstOrDefault().LayoutRect.Height;

        public bool IsPrimaryHasRealSyllableInfo { get; set; }

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

            string joinedText = string.Join("", lyricLine.Words.Select(w => w.Word));
            PrimaryText = joinedText;
            SecondaryText = lyricLine.TransLateText ?? "";

            StartMs = lyricLine.StartMs;
            EndMs = nextLineStartMs;

            int charIndex = 0;
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
            }

            IsPrimaryHasRealSyllableInfo = lyricLine.Words.Count > 0 && lyricLine.Words.Any(w => w.DurationMs > 0);
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

            for (int startCharIndex = 0; startCharIndex < textLength; startCharIndex++)
            {
                var region = PrimaryTextLayout.GetCharacterRegions(startCharIndex, 1).FirstOrDefault();
                var bounds = region.LayoutBounds.Extend(
                    startCharIndex == 0 ? strokeWidth : strokeWidth / 4f,
                    strokeWidth / 2f,
                    startCharIndex == textLength - 1 ? strokeWidth : strokeWidth / 4f,
                    strokeWidth / 2f);

                var syllable = PrimaryRenderSyllables.FirstOrDefault(x => x.StartIndex <= startCharIndex && startCharIndex <= x.EndIndex);
                if (syllable == null) continue;

                var avgCharDuration = syllable.DurationMs / syllable.Length;
                var charStartMs = syllable.StartMs + (startCharIndex - syllable.StartIndex) * avgCharDuration;
                var charEndMs = charStartMs + avgCharDuration;

                var renderChar = new RenderLyricsChar(bounds)
                {
                    StartMs = charStartMs,
                    EndMs = charEndMs,
                    StartIndex = startCharIndex,
                    Text = PrimaryText[startCharIndex].ToString(),
                };

                syllable.ChildrenRenderLyricsChars.Add(renderChar);
                PrimaryRenderChars.Add(renderChar);
            }
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

            UnplayedComposite = null;
            UnplayedStrokeTint = null;
            UnplayedFillTint = null;
            CachedStroke = null;
            CachedFill = null;

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
