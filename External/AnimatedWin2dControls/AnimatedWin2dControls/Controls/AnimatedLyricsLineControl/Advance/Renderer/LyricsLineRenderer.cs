using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI;
using System;
using System.Numerics;
using Windows.Foundation;
using Windows.UI;

namespace AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.Advance
{
    public class LyricsLineRenderer
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

        public void ClearSharedResources()
        {
            // 自 3-slice 重构起播放行填充不再持有可复用的共享 CommandList：
            // 全部资源挂在 RenderLyricsLine/RenderLyricsRegion 上，随布局重建，此方法保留仅为 API 兼容。
        }

        private static readonly float CropHorizonPadding = 10f;
        private static readonly float CropVerticalPadding = 5f;
        private const float FullLineFadeFraction = 0.05f;
        /// <summary>属性更新脏检查阈值：进度变化不足 0.5px 时跳过，肉眼不可见。</summary>
        private const float DirtyEpsilonPx = 0.5f;

        // ── 行级填充矩阵（脏检查缓存）────────────────────────────────────
        private RenderLyricsLine? _lastFillLine;
        private Color _lastPlayedColor;
        private Color _lastUnplayedColor;
        private float _lastPlayedAlpha = float.NegativeInfinity;
        private float _lastUnplayedAlpha = float.NegativeInfinity;
        private float _rampX0;
        private float _rampWidth;
        private Matrix5x4 _playedMatrix;
        private Matrix5x4 _unplayedMatrix;
        // straight（非预乘）端点（fade 线性插值用）。
        // ColorMatrixEffect 默认 AlphaMode=Premultiplied：矩阵在 straight 空间应用，
        // 输出再 premultiply，因此端点必须用 straight 分量（R/255 不带 ×alpha），
        // 否则 unplayed/fade 部分会被平方变暗。
        private float _playedR, _playedG, _playedB, _playedA;
        private float _unplayedR, _unplayedG, _unplayedB, _unplayedA;

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

            if (Line.CachedCropEffect is { } crop && Line.CachedBlurEffect is { } blurFx && Line.CachedOpacityEffect is { } opacityFx)
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
                if (Line.CachedCropEffect is { } crop && Line.CachedBlurEffect is { } blurFx && Line.CachedOpacityEffect is { } opacityFx)
                {
                    crop.SourceRectangle = srcRect;
                    blurFx.BlurAmount = (float)blur;
                    opacityFx.Opacity = (float)opacity;
                    ds.DrawImage(opacityFx, srcRect, srcRect);
                }
                return;
            }

            var playedOpacity = Line.PlayedPrimaryOpacityTransition.Value;
            var unplayedOpacity = Line.UnplayedPrimaryOpacityTransition.Value;
            if (double.IsNaN(playedOpacity) || double.IsNaN(unplayedOpacity)) return;
            if (playedOpacity <= 0 && unplayedOpacity <= 0) return;

            if (!Line.IsPrimaryHasRealSyllableInfo)
            {
                DrawFullLineRegion(ds);
            }
            else
            {
                DrawSubLineRegions(resourceCreator, ds);
            }
        }

        private void DrawFullLineRegion(CanvasDrawingSession ds)
        {
            if (Line?.PrimaryTextLayout == null || Line.RenderLyricsRegions == null) return;

            var bounds = Line.PrimaryTextLayout.LayoutBounds;
            Rect fullRect = new(
                bounds.X + Line.PrimaryPosition.X,
                bounds.Y + Line.PrimaryPosition.Y,
                bounds.Width, bounds.Height);
            if (fullRect.Width <= 0 || fullRect.Height <= 0) return;

            float progress = Math.Clamp((float)Line.GetPlayProgress(CurrentProgressMs), 0f, 1f);

            var region = Line.RenderLyricsRegions[0];
            if (region == null) return;

            // 矩阵脏检查每 line 只做一次（同一 line 的所有 region 必须看到相同的
            // lineFillChanged，否则只有循环中第一个 region 能消费到 true，后续
            // region 的 ColorMatrix 会停留在首次赋值的旧值）。
            bool lineFillChanged = UpdateLineFillMatrices();
            UpdateRegionFill(region, fullRect, progress, FullLineFadeFraction, lineFillChanged);
            ds.DrawImage(region.FinalFillEffect);
        }

        private void DrawSubLineRegions(ICanvasResourceCreator resourceCreator, CanvasDrawingSession ds)
        {
            if (Line?.PrimaryTextRegions == null || Line.RenderLyricsRegions == null) return;

            var regions = Line.PrimaryTextRegions;
            var renderRegions = Line.RenderLyricsRegions;
            int regionCount = regions.Length;
            var primaryChars = Line.PrimaryRenderChars;

            // 矩阵脏检查每 line 一次：同一 line 的所有 region 必须看到相同的
            // lineFillChanged（若在循环内逐 region 调用，只有第一个 region 能
            // 消费到 true，后续 region 的 ColorMatrix 停留在首次赋值的旧值，
            // 表现为 wrap 多行时第二行及之后扫光无效果）。
            bool lineFillChanged = UpdateLineFillMatrices();

            for (int i = 0; i < regionCount; i++)
            {
                var subLineRegion = regions[i];
                var renderRegion = renderRegions[i];
                if (renderRegion == null) continue;

                var subRect = subLineRegion.LayoutBounds;
                if (subRect.Width <= 0) continue;

                Rect subLineRect = new(
                    subRect.X + Line.PrimaryPosition.X,
                    subRect.Y + Line.PrimaryPosition.Y,
                    subRect.Width, subRect.Height);

                double playedWidth = ComputeRegionPlayedWidth(subLineRegion);
                float progressInRegion = Math.Clamp((float)(playedWidth / subRect.Width), 0f, 1f);
                if (float.IsNaN(progressInRegion)) progressInRegion = 0f;
                float fadeInRegion = 1f / subLineRegion.CharacterCount * 0.5f;
                float firstCharProgress = 1f;
                if (subLineRegion.CharacterIndex < primaryChars.Count)
                    firstCharProgress = Math.Clamp(
                        (float)primaryChars[subLineRegion.CharacterIndex].GetPlayProgress(CurrentProgressMs), 0f, 1f);
                // DurationMs=0 的字符 GetPlayProgress 会产生 NaN，NaN 会污染 fade
                // 宽度进而使 CropEffect 的 SourceRectangle 含 NaN（矩形行为未定义）。
                if (float.IsNaN(firstCharProgress)) firstCharProgress = 0f;
                float fade = fadeInRegion * firstCharProgress;
                if (float.IsNaN(fade)) fade = 0f;

                UpdateRegionFill(renderRegion, subLineRect, progressInRegion, fade, lineFillChanged);

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
        }

        /// <summary>
        /// 更新 region 的三切片（已播 / 淡出带 / 未播）裁剪矩形与颜色矩阵。
        /// 仅做属性写入，零资源创建；变化不足 DirtyEpsilonPx 时整体跳过。
        /// </summary>
        /// <param name="lineFillChanged">由调用方每 line 计算一次的矩阵脏标记（见 <see cref="UpdateLineFillMatrices"/>）。</param>
        private void UpdateRegionFill(RenderLyricsRegion region, Rect rect, float p, float fade, bool lineFillChanged)
        {
            float rX = (float)rect.X, rY = (float)rect.Y;
            float rW = (float)rect.Width, rH = (float)rect.Height;
            if (rW <= 0 || rH <= 0) return;

            float playedW = Math.Max(0f, p * rW);
            float unplayedX = p + fade;
            float unplayedW = Math.Max(0f, (1f - unplayedX) * rW);
            float fadeW = Math.Max(0f, fade * rW);

            bool regionDirty =
                Math.Abs(playedW - region.LastPlayedWidthPx) > DirtyEpsilonPx
                || Math.Abs(fadeW - region.LastFadeWidthPx) > DirtyEpsilonPx;

            // 注意 !FillMatricesApplied：布局重建可能用同一 line 对象重建 region，
            // 此时 lineFillChanged 不会命中，但新 region 的 ColorMatrix 初始为透明矩阵，
            // 必须强制赋值一次，否则切片永久显示透明（或恒等矩阵透传 Ramp 渐变）。
            if (!lineFillChanged && !regionDirty && region.FillMatricesApplied) return;

            if (regionDirty)
            {
                region.LastPlayedWidthPx = playedW;
                region.LastFadeWidthPx = fadeW;

                region.FillCropPlayed.SourceRectangle = new Rect(rX, rY, playedW, rH);
                region.FillCropUnplayed.SourceRectangle = new Rect(rX + unplayedX * rW, rY, unplayedW, rH);
                region.FillCropFade.SourceRectangle = new Rect(rX + p * rW, rY, fadeW, rH);
            }

            if (lineFillChanged || !region.FillMatricesApplied)
            {
                region.FillMatricesApplied = true;
                region.FillColorPlayed.ColorMatrix = _playedMatrix;
                region.FillColorUnplayed.ColorMatrix = _unplayedMatrix;
            }

            if (fadeW >= DirtyEpsilonPx && (lineFillChanged || regionDirty))
            {
                region.FillColorFade.ColorMatrix = BuildFadeMatrix(rX, p * rW, fadeW);
            }
        }

        /// <summary>
        /// 行级填充矩阵脏检查：仅当颜色/不透明度（含过渡动画）/行对象变化时重建。
        /// </summary>
        private bool UpdateLineFillMatrices()
        {
            if (Line == null) return false;

            float playedAlpha = (float)Line.PlayedPrimaryOpacityTransition.Value;
            float unplayedAlpha = (float)Line.UnplayedPrimaryOpacityTransition.Value;

            // Ramp 几何基准每次调用都刷新：布局重建可能用同一 line 对象重建
            // PrimaryTextLayout（lineFillChanged 的引用比较无法感知），若沿用旧值，
            // fade 矩阵会按陈旧的 _rampX0/_rampWidth 映射渐变采样。
            if (Line.PrimaryTextLayout != null)
            {
                var bounds = Line.PrimaryTextLayout.LayoutBounds;
                _rampX0 = (float)(Line.PrimaryPosition.X + bounds.X);
                _rampWidth = (float)bounds.Width;
            }

            bool changed = !ReferenceEquals(Line, _lastFillLine)
                || playedAlpha != _lastPlayedAlpha
                || unplayedAlpha != _lastUnplayedAlpha
                || PlayedFillColor != _lastPlayedColor
                || UnplayedFillColor != _lastUnplayedColor;
            if (!changed) return false;

            _lastFillLine = Line;
            _lastPlayedAlpha = playedAlpha;
            _lastUnplayedAlpha = unplayedAlpha;
            _lastPlayedColor = PlayedFillColor;
            _lastUnplayedColor = UnplayedFillColor;

            _playedMatrix = BuildSolidMatrix(PlayedFillColor, playedAlpha);
            _unplayedMatrix = BuildSolidMatrix(UnplayedFillColor, unplayedAlpha);

            _playedR = PlayedFillColor.R / 255f;
            _playedG = PlayedFillColor.G / 255f;
            _playedB = PlayedFillColor.B / 255f;
            _playedA = playedAlpha;
            _unplayedR = UnplayedFillColor.R / 255f;
            _unplayedG = UnplayedFillColor.G / 255f;
            _unplayedB = UnplayedFillColor.B / 255f;
            _unplayedA = unplayedAlpha;

            return true;
        }

        /// <summary>
        /// 纯色切片矩阵：输出 straight 色 (R/255, G/255, B/255, alpha)。
        /// ColorMatrixEffect 默认 AlphaMode=Premultiplied 会先 un-premultiply 再应用
        /// 矩阵，输出后再 premultiply，故此处直接给 straight 分量即可与原始
        /// 渐变（straight stops → premultiply）逐像素一致。
        /// </summary>
        private static Matrix5x4 BuildSolidMatrix(Color color, float alpha)
        {
            return new Matrix5x4
            {
                M44 = alpha,
                M51 = color.R / 255f,
                M52 = color.G / 255f,
                M53 = color.B / 255f,
            };
        }

        /// <summary>
        /// 淡出带切片矩阵：把 Ramp CL 采样值 v=1-(d+xl)/W 线性映射到 t∈[0,1]，
        /// 在带内做 played→unplayed 的 straight 线性插值（与原 4-stop 渐变
        /// 的 straight 空间插值逐像素等价，无任何偏差）。
        /// </summary>
        private Matrix5x4 BuildFadeMatrix(float rX, float pPx, float fadePx)
        {
            float W = _rampWidth;
            if (W <= 0) return _playedMatrix;

            float d = rX - _rampX0;
            float a = (W - d - pPx) / fadePx;
            float b = -W / fadePx;

            float kR = b * (_unplayedR - _playedR), cR = _playedR + a * (_unplayedR - _playedR);
            float kG = b * (_unplayedG - _playedG), cG = _playedG + a * (_unplayedG - _playedG);
            float kB = b * (_unplayedB - _playedB), cB = _playedB + a * (_unplayedB - _playedB);
            float kA = b * (_unplayedA - _playedA), cA = _playedA + a * (_unplayedA - _playedA);

            return new Matrix5x4
            {
                M11 = kR, M51 = cR,
                M12 = kG, M52 = cG,
                M13 = kB, M53 = cB,
                M14 = kA, M44 = cA,
            };
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
