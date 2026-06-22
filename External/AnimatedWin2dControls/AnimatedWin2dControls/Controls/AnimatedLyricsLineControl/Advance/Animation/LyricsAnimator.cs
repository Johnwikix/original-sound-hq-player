using System;
using System.Collections.Generic;

namespace AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.Advance
{
    public class LyricsAnimator
    {
        private readonly double _defaultScale = 0.75;
        private readonly double _highlightedScale = 1.0;

        public void UpdateLines(
            IList<RenderLyricsLine>? lines,
            int startIndex,
            int endIndex,
            int primaryPlayingLineIndex,
            double lyricsWidth,
            double lyricsHeight,
            double targetYScrollOffset,
            double playingLineTopOffsetFactor,
            bool isLyricsBlurEffectEnabled,
            bool isLyricsOutOfSightEffectEnabled,
            bool isLyricsFadeOutEffectEnabled,
            double unplayedPrimaryOpacity,
            double playedPrimaryOpacity,
            double secondaryOpacity,
            bool isLyricsGlowEffectEnabled,
            double lyricsGlowEffectAmount,
            double lyricsGlowEffectLongSyllableDuration,
            bool isLyricsFloatAnimationEnabled,
            double lyricsFloatAnimationAmount,
            double lyricsFloatAnimationDuration,
            bool isLyricsScaleEffectEnabled,
            double lyricsScaleEffectAmount,
            double lyricsScaleEffectLongSyllableDuration,
            double blurAmountMax,
            ValueTransition<double> canvasYScrollTransition,
            TimeSpan elapsedTime,
            bool isMouseScrolling,
            bool isMouseScrollingChanged,
            bool isLayoutChanged,
            bool isPrimaryPlayingLineChanged,
            double currentPositionMs)
        {
            if (lines == null || lines.Count == 0) return;
            if (primaryPlayingLineIndex < 0 || primaryPlayingLineIndex >= lines.Count) return;

            var primaryPlayingLine = lines[primaryPlayingLineIndex];

            double topHeightFactor = lyricsHeight * playingLineTopOffsetFactor;
            double bottomHeightFactor = lyricsHeight * (1 - playingLineTopOffsetFactor);

            double canvasTransDuration = canvasYScrollTransition.DurationSeconds;

            bool isBlurEnabled = isLyricsBlurEffectEnabled;
            bool isOutOfSightEnabled = isLyricsOutOfSightEffectEnabled;
            bool isGlowEnabled = isLyricsGlowEffectEnabled;
            bool isFloatEnabled = isLyricsFloatAnimationEnabled;
            bool isScaleEnabled = isLyricsScaleEffectEnabled;

            int safeStart = Math.Max(0, startIndex);
            int safeEnd = Math.Min(lines.Count - 1, endIndex + 1);

            for (int i = safeStart; i <= safeEnd; i++)
            {
                var line = lines[i];
                var lineHeight = line.PrimaryLineHeight;
                if (lineHeight == null || lineHeight <= 0) continue;

                bool isWordAnimationEnabled = line.IsPrimaryHasRealSyllableInfo;

                double targetCharFloat = lyricsFloatAnimationAmount > 0 ? lyricsFloatAnimationAmount : lineHeight.Value * 0.1;
                double targetCharGlow = lyricsGlowEffectAmount > 0 ? lyricsGlowEffectAmount : lineHeight.Value * 0.2;
                double targetCharScale = lyricsScaleEffectAmount > 0 ? lyricsScaleEffectAmount : 1.15;

                var maxAnimationDurationMs = Math.Max(line.EndMs ?? 0 - currentPositionMs, 0);

                bool isSecondaryLinePlaying = line.GetIsPlaying(currentPositionMs);
                bool isSecondaryLinePlayingChanged = line.IsPlayingLastFrame != isSecondaryLinePlaying;
                line.IsPlayingLastFrame = isSecondaryLinePlaying;

                var playProgress = line.GetPlayProgress(currentPositionMs);

                if (isLayoutChanged || isPrimaryPlayingLineChanged || isMouseScrollingChanged || isSecondaryLinePlayingChanged
                    || line.UnplayedPrimaryOpacityTransition.Value == 0)
                {
                    int lineCountDelta = i - primaryPlayingLineIndex;
                    double distanceFromPlayingLine = Math.Abs(line.TopLeftPosition.Y - primaryPlayingLine.TopLeftPosition.Y);

                    double distanceFactor;
                    if (lineCountDelta < 0)
                        distanceFactor = Math.Clamp(distanceFromPlayingLine / topHeightFactor, 0, 1);
                    else
                        distanceFactor = Math.Clamp(distanceFromPlayingLine / bottomHeightFactor, 0, 1);

                    double yScrollDuration = canvasTransDuration + distanceFactor * (0.5 - canvasTransDuration);

                    double targetPlayedOpacity = CalculateTargetOpacity(unplayedPrimaryOpacity, isSecondaryLinePlaying ? 1.0 : unplayedPrimaryOpacity, distanceFactor, isMouseScrolling, isLyricsFadeOutEffectEnabled);
                    double targetUnplayedOpacity = CalculateTargetOpacity(unplayedPrimaryOpacity, unplayedPrimaryOpacity, distanceFactor, isMouseScrolling, isLyricsFadeOutEffectEnabled);
                    double targetSecondaryOpacity = CalculateTargetOpacity(secondaryOpacity, secondaryOpacity, distanceFactor, isMouseScrolling, isLyricsFadeOutEffectEnabled);

                    line.BlurAmountTransition.SetDuration(yScrollDuration);
                    line.BlurAmountTransition.Start(
                        (isMouseScrolling || isSecondaryLinePlaying) ? 0 :
                        (isBlurEnabled ? (blurAmountMax * distanceFactor) : 0));

                    line.ScaleTransition.SetDuration(yScrollDuration);
                    line.ScaleTransition.Start(
                        isSecondaryLinePlaying ? _highlightedScale :
                        (isOutOfSightEnabled ?
                        (_highlightedScale - distanceFactor * (_highlightedScale - _defaultScale)) :
                        _highlightedScale));

                    line.PlayedPrimaryOpacityTransition.SetDuration(yScrollDuration);
                    line.PlayedPrimaryOpacityTransition.Start(
                        isSecondaryLinePlaying ? 1.0 : targetPlayedOpacity);

                    line.UnplayedPrimaryOpacityTransition.SetDuration(yScrollDuration);
                    line.UnplayedPrimaryOpacityTransition.Start(
                        isSecondaryLinePlaying ? unplayedPrimaryOpacity : targetUnplayedOpacity);

                    line.SecondaryOpacityTransition.SetDuration(yScrollDuration);
                    line.SecondaryOpacityTransition.Start(
                        isSecondaryLinePlaying ? secondaryOpacity : targetSecondaryOpacity);

                    if (isLayoutChanged || isPrimaryPlayingLineChanged)
                    {
                        line.YOffsetTransition.SetInterpolator(canvasYScrollTransition.Interpolator);
                        line.YOffsetTransition.SetDuration(yScrollDuration);
                        if (isLayoutChanged)
                            line.YOffsetTransition.JumpTo(targetYScrollOffset);
                        else
                            line.YOffsetTransition.Start(targetYScrollOffset);
                    }
                }

                if (isWordAnimationEnabled)
                {
                    if (isSecondaryLinePlayingChanged)
                    {
                        if (isFloatEnabled)
                        {
                            foreach (var renderChar in line.PrimaryRenderChars)
                            {
                                if (isSecondaryLinePlaying)
                                {
                                    if (renderChar.EndMs < currentPositionMs)
                                        renderChar.FloatTransition.JumpTo(0);
                                    else
                                        renderChar.FloatTransition.Start(targetCharFloat);
                                }
                                else
                                {
                                    renderChar.FloatTransition.Start(0);
                                }
                            }
                        }
                    }

                    foreach (var renderChar in line.PrimaryRenderChars)
                    {
                        renderChar.ProgressPlayed = renderChar.GetPlayProgress(currentPositionMs);

                        bool isCharPlaying = renderChar.GetIsPlaying(currentPositionMs);
                        bool isCharPlayingChanged = renderChar.IsPlayingLastFrame != isCharPlaying;

                        if (isCharPlayingChanged)
                        {
                            if (isFloatEnabled)
                            {
                                renderChar.FloatTransition.SetDurationMs(Math.Min(lyricsFloatAnimationDuration, maxAnimationDurationMs));
                                renderChar.FloatTransition.Start(0);
                            }
                            renderChar.IsPlayingLastFrame = isCharPlaying;
                        }
                        else
                        {
                            if (!isCharPlaying && currentPositionMs > renderChar.EndMs && renderChar.FloatTransition.Value != 0)
                            {
                                renderChar.FloatTransition.SetDurationMs(Math.Min(lyricsFloatAnimationDuration, maxAnimationDurationMs));
                                renderChar.FloatTransition.Start(0);
                            }
                        }
                    }

                    foreach (var syllable in line.PrimaryRenderSyllables)
                    {
                        bool isSyllablePlaying = syllable.GetIsPlaying(currentPositionMs);
                        bool isSyllablePlayingChanged = syllable.IsPlayingLastFrame != isSyllablePlaying;

                        if (isSyllablePlayingChanged)
                        {
                            if (isScaleEnabled && isSyllablePlaying)
                            {
                                foreach (var renderChar in syllable.ChildrenRenderLyricsChars)
                                {
                                    if (syllable.DurationMs >= lyricsScaleEffectLongSyllableDuration)
                                    {
                                        var (inDuration, outDuration) = CalculateSegmentDuration(syllable.DurationMs / 1000.0, maxAnimationDurationMs / 1000.0);
                                        renderChar.ScaleTransition.Start(
                                            new Keyframe<double>(targetCharScale, inDuration),
                                            new Keyframe<double>(1.0, outDuration));
                                    }
                                }
                            }

                            if (isGlowEnabled && isSyllablePlaying && syllable.DurationMs >= lyricsGlowEffectLongSyllableDuration)
                            {
                                foreach (var renderChar in syllable.ChildrenRenderLyricsChars)
                                {
                                    var (inDuration, outDuration) = CalculateSegmentDuration(syllable.DurationMs / 1000.0, maxAnimationDurationMs / 1000.0);
                                    renderChar.GlowTransition.Start(
                                        new Keyframe<double>(targetCharGlow, inDuration),
                                        new Keyframe<double>(0, outDuration));
                                }
                            }

                            syllable.IsPlayingLastFrame = isSyllablePlaying;
                        }
                    }

                    foreach (var renderChar in line.PrimaryRenderChars)
                    {
                        renderChar.Update(elapsedTime);
                    }
                }

                line.Update(elapsedTime);
            }
        }

        private static double CalculateTargetOpacity(double baseOpacity, double baseOpacityWhenZeroDistanceFactor,
            double distanceFactor, bool isMouseScrolling, bool isFadeOutEnabled)
        {
            if (distanceFactor == 0)
                return baseOpacityWhenZeroDistanceFactor;

            if (isMouseScrolling)
                return baseOpacity;

            if (isFadeOutEnabled)
                return (1 - distanceFactor) * baseOpacity;

            return baseOpacity;
        }

        private static (double InDuration, double OutDuration) CalculateSegmentDuration(double desiredDuration, double maxDuration)
        {
            var inDuration = Math.Min(desiredDuration, maxDuration);
            var outDuration = Math.Min(maxDuration - inDuration, 1.0);
            return (inDuration, outDuration);
        }
    }
}
