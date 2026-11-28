using AnimatedWin2dControls.Enums;
using AnimatedWin2dControls.Interop;
using AnimatedWin2dControls.Shaders;
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
        private PlaybackHdrColor _hdr;
        private float _hdrGlowPeak;
        private double _glowAmount;
        private double _hdrGlowFullStrengthRadius;

        internal void SetPlaybackColor(PlaybackHdrColor hdr, double glowAmount)
        {
            if (_hdr.IsEnabled && !hdr.IsEnabled)
                ClearSharedResources();
            _hdr = hdr;
            IsGlowEnabled = glowAmount > 0;
            _glowAmount = glowAmount;
            _hdrGlowPeak = Math.Min(float.Lerp(hdr.White, hdr.Peak, (float)(glowAmount / 10d)), hdr.Peak);
            _hdrGlowFullStrengthRadius = Math.Min(glowAmount, 1d);
        }

        private PlaybackHdrColor _lastHdr;

        public bool IsPlaying { get; set; }
        public WordByWordEffectMode WordByWordEffectMode { get; set; }
        public double CurrentProgressMs { get; set; }
        public RenderLyricsLine? Line { get; set; }

        public Color PlayedFillColor { get; set; } = Colors.White;
        public Color UnplayedFillColor { get; set; } = Colors.Black;

        public bool IsGlowEnabled { get; set; }
        public bool IsScaleEnabled { get; set; }
        public bool IsFloatEnabled { get; set; }

        /// <summary>
        /// 逐字路径下随字符绘制描边切片。字浮/字缩/发光会按字符重定位/缩放填充，
        /// 整行固定位置的描边会与填充错位；开启后每个字符的描边切片使用与填充
        /// 完全相同的 dest 映射（见 <see cref="DrawSingleCharacter"/>），描边始终贴合字符。
        /// 桌面歌词等宿主用；主界面描边宽度恒为 0，保持默认 false 零行为变化。
        /// </summary>
        public bool IsStrokeEnabled { get; set; }

        public void Draw(ICanvasResourceCreator resourceCreator, CanvasDrawingSession ds)
        {
            using var scope = CanvasDrawingSessionScope.Borrow(ds);
            Draw(resourceCreator, scope.Context);
        }

        /// <summary>Draws cached lyric resources in the current native session.</summary>
        public void Draw(ICanvasResourceCreator resourceCreator, CanvasDrawContext ds)
        {
            DrawSecondaryText(resourceCreator, ds);
            DrawPrimaryText(ds);
        }

        public void ClearSharedResources()
        {
            _lastFillLine = null;
        }

        private static readonly float CropHorizonPadding = 10f;
        /// <summary>属性更新脏检查阈值：进度变化不足 0.5px 时跳过，肉眼不可见。</summary>
        private const float DirtyEpsilonPx = 0.5f;

        // ── 行级填充矩阵（脏检查缓存）────────────────────────────────────
        private RenderLyricsLine? _lastFillLine;
        private Color _lastPlayedColor;
        private Color _lastUnplayedColor;
        private float _lastPlayedAlpha = float.NegativeInfinity;
        private float _lastUnplayedAlpha = float.NegativeInfinity;
        private Matrix5x4 _playedMatrix;
        private Matrix5x4 _unplayedMatrix;
        // straight（非预乘）端点（fade 线性插值用）。
        // ColorMatrixEffect 默认 AlphaMode=Premultiplied：矩阵在 straight 空间应用，
        // 输出再 premultiply，因此端点必须用 straight 分量（R/255 不带 ×alpha），
        // 否则 unplayed/fade 部分会被平方变暗。
        private float _playedR, _playedG, _playedB, _playedA;
        private float _unplayedR, _unplayedG, _unplayedB, _unplayedA;

        private void DrawSecondaryText(ICanvasResourceCreator creator, CanvasDrawContext ds)
        {
            if (Line == null) return;
            // The host already translates the cached primary origin to its live position.
            // Auxiliary caches use local coordinates and compensate for that translation.
            var offset = new Vector2(0, (float)Line.LayoutOffsetY);
            var color = Line.UnplayedFillTint?.Color ?? UnplayedFillColor;
            Line.PronunciationLayer.Draw(creator, ds, Line.PronunciationLayer.Position - offset,
                Line.SecondaryOpacityTransition.Value, Line.BlurAmountTransition.Value, 0, color, _hdr);
            Line.TranslationLayer.Draw(creator, ds, Line.TranslationLayer.Position - offset,
                Line.SecondaryOpacityTransition.Value, Line.BlurAmountTransition.Value, 0, color, _hdr);
        }

        private void DrawPrimaryText(CanvasDrawContext ds)
        {
            if (Line?.PrimaryTextLayout == null || Line.PrimaryTextRegions == null) return;
            if (Line.UnplayedComposite == null) return;

            var bounds = Line.PrimaryTextLayout.LayoutBounds;
            var srcRect = new Rect(
                bounds.X + Line.CachedPrimaryPosition.X - CropHorizonPadding,
                bounds.Y + Line.CachedPrimaryPosition.Y - CropHorizonPadding,
                bounds.Width + CropHorizonPadding * 2, bounds.Height + CropHorizonPadding * 2);

            float entryGlowStrength = IsGlowEnabled && Line.EntryGlowTransition is { Value: > 0 } glow
                && !Line.UsesWordAnimation(WordByWordEffectMode) ? (float)glow.Value : 0;

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
                    var output = srcRect.ExtendForGaussianBlur(blur);
                    ICanvasImage ink = _hdr.IsEnabled ? Line.GetHdrUnplayed(opacityFx, _hdr.White) : opacityFx;
                    if (entryGlowStrength > 0)
                        ink = DrawEntryGlowHalo(ds, ink, output, entryGlowStrength);
                    ds.DrawImage(ink, output, output);
                }
                return;
            }

            var playedOpacity = Line.PlayedPrimaryOpacityTransition.Value;
            var unplayedOpacity = Line.UnplayedPrimaryOpacityTransition.Value;
            if (double.IsNaN(playedOpacity) || double.IsNaN(unplayedOpacity)) return;
            if (playedOpacity <= 0 && unplayedOpacity <= 0) return;

            if (entryGlowStrength > 0)
            {
                var color = BuildSolidMatrix(_hdr.GetRgb(PlayedFillColor), (float)playedOpacity);
                var ink = Line.GetEntryGlowFill(color);
                ds.DrawImage(DrawEntryGlowHalo(ds, ink, srcRect, entryGlowStrength));
                return;
            }

            DrawPrimaryRegions(ds, Line.UsesWordAnimation(WordByWordEffectMode));
        }

        private ICanvasImage DrawEntryGlowHalo(CanvasDrawContext ds, ICanvasImage source, Rect bounds, float strength)
        {
            float radius = (float)(_glowAmount * strength);
            var (ink, halo) = Line!.PrepareEntryGlow(source, radius, _hdr, _hdrGlowPeak, strength);
            var haloBounds = bounds.ExtendForGaussianBlur(radius);
            ds.DrawImage(halo, haloBounds, haloBounds, strength);
            return ink;
        }

        private void DrawPrimaryRegions(CanvasDrawContext ds, bool useWordAnimation)
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
                    subRect.X + Line.CachedPrimaryPosition.X,
                    subRect.Y + Line.CachedPrimaryPosition.Y,
                    subRect.Width, subRect.Height);

                float progressInRegion = 1f;
                float fade = 0f;
                if (useWordAnimation)
                {
                    double playedWidth = ComputeRegionPlayedWidth(subLineRegion);
                    progressInRegion = Math.Clamp((float)(playedWidth / subRect.Width), 0f, 1f);
                    if (float.IsNaN(progressInRegion)) progressInRegion = 0f;
                    if (subLineRegion.CharacterCount > 0 && subLineRegion.CharacterIndex < primaryChars.Count)
                    {
                        float firstCharProgress = Math.Clamp(
                            (float)primaryChars[subLineRegion.CharacterIndex].GetPlayProgress(CurrentProgressMs), 0f, 1f);
                        if (!float.IsNaN(firstCharProgress))
                            fade = 0.5f * firstCharProgress / subLineRegion.CharacterCount;
                    }
                }
                UpdateRegionFill(renderRegion, subLineRect, progressInRegion, fade, lineFillChanged);

                ICanvasImage finalOutputImage = renderRegion.FinalFillEffect;

                if (!useWordAnimation || (!IsFloatEnabled && !IsGlowEnabled && !IsScaleEnabled))
                {
                    ds.DrawImage(finalOutputImage);
                }
                else
                {
                    int endCharIndex = Math.Min(primaryChars.Count, subLineRegion.CharacterIndex + subLineRegion.CharacterCount);
                    if (_hdr.IsEnabled && IsGlowEnabled)
                    {
                        // Finish the halos before drawing the glyphs to keep dark halos off the ink.
                        for (int ci = subLineRegion.CharacterIndex; ci < endCharIndex; ci++)
                            DrawCharacterGlow(ds, primaryChars[ci], finalOutputImage);
                    }

                    for (int ci = subLineRegion.CharacterIndex; ci < endCharIndex; ci++)
                        DrawSingleCharacter(ds, ci, finalOutputImage);
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

            // 几何（裁剪矩形 + fade 平移）与矩阵同门：新 region 的 Transform2DEffect
            // 初始为恒等矩阵，若沿用旧几何会按 [0, fadeWMax] 处采样（band 之外透明）。
            bool needGeometry = regionDirty || !region.FillMatricesApplied;
            if (needGeometry)
            {
                region.LastPlayedWidthPx = playedW;
                region.LastFadeWidthPx = fadeW;

                region.FillCropPlayed.SourceRectangle = new Rect(rX, rY, playedW, rH);
                region.FillCropUnplayed.SourceRectangle = new Rect(rX + unplayedX * rW, rY, unplayedW, rH);
                region.FillCropFade.SourceRectangle = new Rect(rX + p * rW, rY, fadeW, rH);
                region.FillTransformFade.TransformMatrix = Matrix3x2.CreateTranslation(rX + p * rW, 0);
            }

            if (lineFillChanged || !region.FillMatricesApplied)
            {
                region.FillMatricesApplied = true;
                region.FillColorPlayed.ColorMatrix = _playedMatrix;
                region.FillColorUnplayed.ColorMatrix = _unplayedMatrix;
            }

            if (lineFillChanged || regionDirty)
            {
                region.SetHdr(_hdr,
                    new Vector4(_playedR, _playedG, _playedB, _playedA),
                    new Vector4(_unplayedR, _unplayedG, _unplayedB, _unplayedA),
                    Line!.FadeBandWidthMax / Math.Max(fadeW, DirtyEpsilonPx));
                if (!_hdr.IsEnabled && fadeW >= DirtyEpsilonPx)
                    region.FillColorFade.ColorMatrix = BuildFadeMatrix(fadeW);
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

            bool changed = !ReferenceEquals(Line, _lastFillLine)
                || playedAlpha != _lastPlayedAlpha
                || unplayedAlpha != _lastUnplayedAlpha
                || _hdr != _lastHdr
                || PlayedFillColor != _lastPlayedColor
                || UnplayedFillColor != _lastUnplayedColor;
            if (!changed) return false;

            _lastFillLine = Line;
            _lastPlayedAlpha = playedAlpha;
            _lastUnplayedAlpha = unplayedAlpha;
            _lastPlayedColor = PlayedFillColor;
            _lastUnplayedColor = UnplayedFillColor;

            _lastHdr = _hdr;
            Vector3 playedRgb = _hdr.GetRgb(PlayedFillColor);
            Vector3 unplayedRgb = _hdr.GetRgb(UnplayedFillColor);
            _playedMatrix = BuildSolidMatrix(playedRgb, playedAlpha);
            _unplayedMatrix = BuildSolidMatrix(unplayedRgb, unplayedAlpha);

            (_playedR, _playedG, _playedB) = (playedRgb.X, playedRgb.Y, playedRgb.Z);
            _playedA = playedAlpha;
            (_unplayedR, _unplayedG, _unplayedB) = (unplayedRgb.X, unplayedRgb.Y, unplayedRgb.Z);
            _unplayedA = unplayedAlpha;

            return true;
        }

        /// <summary>
        /// 纯色切片矩阵：输出 straight 色 (R/255, G/255, B/255, alpha)。
        /// ColorMatrixEffect 默认 AlphaMode=Premultiplied 会先 un-premultiply 再应用
        /// 矩阵，输出后再 premultiply，故此处直接给 straight 分量即可与原始
        /// 渐变（straight stops → premultiply）逐像素一致。
        /// </summary>
        private static Matrix5x4 BuildSolidMatrix(Vector3 color, float alpha)
        {
            return new Matrix5x4
            {
                M44 = alpha,
                M51 = color.X,
                M52 = color.Y,
                M53 = color.Z,
            };
        }

        /// <summary>
        /// 淡出带切片矩阵：把 BandFill 局部渐变采样值 v = 1 - x_local/fadeWMax
        /// （x_local 相对 band 左缘，<see cref="RenderLyricsLine.BandFillCommandList"/>）
        /// 线性映射到 t∈[0,1]，在带内做 played→unplayed 的 straight 线性插值。
        /// 因 BandFill 全量程只铺 fadeWMax 像素（≥1 电平/px），带内输出与直接
        /// 渐变逐像素一致，不再出现全行 Ramp 采样放大 8-bit 量化造成的色阶断层。
        /// </summary>
        private Matrix5x4 BuildFadeMatrix(float fadePx)
        {
            if (Line == null) return _playedMatrix;
            float fadeWMax = Line.FadeBandWidthMax;
            if (fadePx <= 0 || fadeWMax <= 0) return _playedMatrix;

            float a = fadeWMax / fadePx;
            float b = -fadeWMax / fadePx;

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
                double progress = ch.GetPlayProgress(CurrentProgressMs);
                if (!double.IsFinite(progress)) break;
                playedWidth += ch.LayoutRect.Width * progress;
                if (progress < 1) break;
            }
            return playedWidth;
        }

        private void DrawCharacterGlow(CanvasDrawContext ds, RenderLyricsChar character, ICanvasImage source)
        {
            double radius = character.GlowTransition.Value;
            if (radius <= 0 || Line is null || character.ProgressPlayed <= 0) return;

            var rect = character.LayoutRect;
            var sourceRect = new Rect(rect.X + Line.CachedPrimaryPosition.X, rect.Y + Line.CachedPrimaryPosition.Y,
                rect.Width, rect.Height);
            var destination = sourceRect.Scale(character.ScaleTransition.Value).AddY(character.FloatTransition.Value);
            character.SetEffectPrecision(_hdr.IsEnabled);
            character.Crop.Source = source;
            character.Crop.SourceRectangle = new Rect(sourceRect.X, sourceRect.Y,
                sourceRect.Width * character.ProgressPlayed, sourceRect.Height);
            character.Glow.BlurAmount = (float)radius;

            // Reduce the light contribution as the outgoing blur approaches zero radius.
            float opacity = Line.IsWordEffectsRetiring
                ? (character.GlowExitStartAmount > 0 ? (float)Math.Clamp(radius / character.GlowExitStartAmount, 0, 1) : 0)
                : 1;
            ICanvasImage glow = character.Glow;
            if (_hdr.IsEnabled && _hdrGlowPeak > _hdr.White)
            {
                var halo = character.GetHdrHalo();
                // Reuse the SDR animation and alpha mask. Only the visible glow's RGB gets extra headroom.
                halo.ConstantBuffer = new PlaybackTextGlowCoreEffect(_hdr.White, _hdrGlowPeak, GetHdrGlowStrength(character));
                glow = halo;
            }

            ds.DrawImage(glow, destination.ExtendForGaussianBlur(radius, character.ScaleTransition.Value),
                sourceRect.ExtendForGaussianBlur(radius), opacity);
        }

        private float GetHdrGlowStrength(RenderLyricsChar character)
        {
            double radius = character.GlowTransition.Value;
            // Subpixel glow settings reach full strength at their configured radius.
            float strength = (float)Math.Clamp(radius / _hdrGlowFullStrengthRadius, 0d, 1d);
            if (!Line!.IsWordEffectsRetiring)
                return strength;
            double start = character.GlowExitStartAmount;
            return start > 0 ? strength * (float)Math.Clamp(radius / start, 0d, 1d) : 0;
        }

        private void DrawSingleCharacter(CanvasDrawContext ds, int charIndex, ICanvasImage source)
        {
            if (Line?.PrimaryRenderChars == null) return;
            if (charIndex >= Line.PrimaryRenderChars.Count) return;

            RenderLyricsChar renderChar = Line.PrimaryRenderChars[charIndex];

            var rect = renderChar.LayoutRect;
            var sourceCharRect = new Rect(
                rect.X + Line.CachedPrimaryPosition.X,
                rect.Y + Line.CachedPrimaryPosition.Y,
                rect.Width, rect.Height);

            double scale = renderChar.ScaleTransition.Value;
            double glow = renderChar.GlowTransition.Value;
            double floatOffset = renderChar.FloatTransition.Value;

            var destCharRect = sourceCharRect.Scale(scale).AddY(floatOffset);

            // 描边切片与填充共用同一 dest 映射，随字浮/字缩一起变换（见 IsStrokeEnabled 注释）
            if (IsStrokeEnabled && Line.UnplayedStrokeTint is { } stroke)
                ds.DrawImage(stroke, destCharRect, sourceCharRect);

            if (!_hdr.IsEnabled && glow > 0)
                DrawCharacterGlow(ds, renderChar, source);

            double blur = Line.BlurAmountTransition.Value;
            bool defocus = Line.IsWordEffectsRetiring && blur > 0;
            if (!defocus && _hdr.IsEnabled && IsGlowEnabled && _hdrGlowPeak > _hdr.White && glow > 0
                && renderChar.ProgressPlayed > 0)
            {
                var litInk = renderChar.GetHdrGlow();
                if (!ReferenceEquals(litInk.Sources[0], source))
                    litInk.Sources[0] = source;
                float strength = GetHdrGlowStrength(renderChar) * (float)renderChar.ProgressPlayed;
                litInk.ConstantBuffer = new PlaybackTextGlowCoreEffect(_hdr.White, _hdrGlowPeak, strength);
                source = litInk;
            }

            if (defocus)
            {
                // 普通退场散焦沿用纸白字形，额外亮度只留在单独绘制的余辉。
                renderChar.SetEffectPrecision(_hdr.IsEnabled);
                renderChar.Crop.Source = source;
                renderChar.Crop.SourceRectangle = sourceCharRect;
                renderChar.Glow.BlurAmount = (float)blur;
                ds.DrawImage(renderChar.Glow, destCharRect.ExtendForGaussianBlur(blur, scale),
                    sourceCharRect.ExtendForGaussianBlur(blur));
            }
            else
                ds.DrawImage(source, destCharRect, sourceCharRect);
        }
    }
}
