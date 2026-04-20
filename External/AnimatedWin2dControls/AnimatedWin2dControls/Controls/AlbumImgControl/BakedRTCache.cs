using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using System;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace AnimatedWin2dControls.Controls.AlbumImgControl
{
    /// <summary>
    /// 管理 mask RT 和两张 BakedRT（Current / Incoming）。
    ///
    /// 与 TransitionState 的同步规则（忠实还原原始逻辑）：
    ///   - Current  RT ↔ TransitionState.CurrentBitmap
    ///   - Incoming RT ↔ TransitionState.IncomingBitmap
    ///   - 当 TransitionState 触发 IncomingPromotedToCurrent 时，
    ///     调用 PromoteIncomingToCurrent() 同步翻转两张 RT。
    ///
    /// 异步安全：每次 Bake 任务启动时快照 _epoch；完成后若 epoch 已变则丢弃结果。
    /// </summary>
    internal sealed class BakedRTCache : IDisposable
    {
        public BakedRT? Current { get; private set; }
        public BakedRT? Incoming { get; private set; }

        private CanvasRenderTarget? _maskRT;
        private (float w, float h, float radius, float dpi) _maskParams;
        private int _epoch;
        private bool _disposed;

        // ── Incoming → Current 提升 ───────────────────────────────────────────
        // 与原始代码完全一致：直接翻转，不加任何延迟/pending 标志。
        // 若 Incoming RT 尚未就绪（null），Current 会暂时变 null；
        // 但烘焙完成时会立即触发 Invalidate→ Canvas.Invalidate，下一帧恢复显示。
        // 这与原始单文件的行为相同。

        public void PromoteIncomingToCurrent()
        {
            Current?.Dispose();
            Current = Incoming;
            Incoming = null;
        }

        // ── mask 维护 ────────────────────────────────────────────────────────

        /// <summary>
        /// 确保 mask 与给定参数匹配。若需重建，同步清空所有 baked RT 并回调触发重烘焙。
        /// 必须在 UI 线程（Draw 回调内）调用。
        /// </summary>
        public void EnsureMask(
            CanvasDevice device,
            float w, float h, float radius, float dpi,
            CanvasBitmap? currentBitmap,
            CanvasBitmap? incomingBitmap,
            bool shadow,
            Action<CanvasBitmap, float, float> onNeedRebake)
        {
            bool same = _maskRT != null
                && MathF.Abs(_maskParams.w - w) < 0.5f
                && MathF.Abs(_maskParams.h - h) < 0.5f
                && MathF.Abs(_maskParams.radius - radius) < 0.5f
                && MathF.Abs(_maskParams.dpi - dpi) < 0.5f;
            if (same) return;

            _maskRT?.Dispose();
            Current?.Dispose(); Current = null;
            Incoming?.Dispose(); Incoming = null;
            _epoch++;

            _maskRT = new CanvasRenderTarget(device, w, h, dpi);
            using (var ds = _maskRT.CreateDrawingSession())
            {
                ds.Clear(Microsoft.UI.Colors.Transparent);
                ds.FillRoundedRectangle(0, 0, w, h, radius, radius, Microsoft.UI.Colors.White);
            }
            _maskParams = (w, h, radius, dpi);

            if (currentBitmap != null) onNeedRebake(currentBitmap, w, h);
            if (incomingBitmap != null) onNeedRebake(incomingBitmap, w, h);
        }

        // ── 异步烘焙 ─────────────────────────────────────────────────────────

        public async Task BakeCurrentAsync(
            CanvasBitmap bitmap, CanvasDevice device,
            float w, float h, bool shadow, float dpiScale,
            Func<bool> isStillValid, Action onReady)
        {
            var result = await BakeAsync(bitmap, device, w, h, shadow, dpiScale, isStillValid);
            if (result == null) return;
            Current?.Dispose();
            Current = result;
            onReady();
        }

        public async Task BakeIncomingAsync(
            CanvasBitmap bitmap, CanvasDevice device,
            float w, float h, bool shadow, float dpiScale,
            Func<bool> isStillValid, Action onReady)
        {
            var result = await BakeAsync(bitmap, device, w, h, shadow, dpiScale, isStillValid);
            if (result == null) return;
            Incoming?.Dispose();
            Incoming = result;
            onReady();
        }

        private async Task<BakedRT?> BakeAsync(
            CanvasBitmap bitmap, CanvasDevice device,
            float w, float h, bool shadow, float dpiScale,
            Func<bool> isStillValid)
        {
            if (_maskRT == null) return null;
            int epochAtStart = _epoch;
            var maskSnap = _maskRT;

            BakedRT? baked;
            try
            {
                baked = await Task.Run(
                    () => BakeCore(device, bitmap, w, h, maskSnap, shadow, dpiScale));
            }
            catch { return null; }

            if (_epoch != epochAtStart || !isStillValid())
            {
                baked.Dispose();
                return null;
            }
            return baked;
        }

        // ── 失效 ─────────────────────────────────────────────────────────────

        /// <summary>清空 baked RT（保留 mask），epoch 递增使进行中的烘焙任务结果失效。</summary>
        public void InvalidateBaked()
        {
            Current?.Dispose(); Current = null;
            Incoming?.Dispose(); Incoming = null;
            _epoch++;
        }

        // ── 核心烘焙（后台线程）─────────────────────────────────────────────

        private static BakedRT BakeCore(
            CanvasDevice device, CanvasBitmap bitmap,
            float w, float h, CanvasRenderTarget maskRT,
            bool shadow, float dpiScale)
        {
            float pad = shadow ? 10f * 3f + 4f : 0f;
            float dpi = 96f * dpiScale;
            var rt = new CanvasRenderTarget(device, w + pad * 2f, h + pad * 2f, dpi);

            using var ds = rt.CreateDrawingSession();
            ds.Clear(Microsoft.UI.Colors.Transparent);

            float scaleRatio = Math.Min(w / bitmap.SizeInPixels.Width,
                                         h / bitmap.SizeInPixels.Height);
            var interp = scaleRatio < 0.5f
                ? CanvasImageInterpolation.HighQualityCubic
                : CanvasImageInterpolation.Linear;

            using var scale = new ScaleEffect
            {
                Source = bitmap,
                Scale = new Vector2(w / bitmap.SizeInPixels.Width,
                                                h / bitmap.SizeInPixels.Height),
                InterpolationMode = interp
            };
            using var masked = new AlphaMaskEffect { Source = scale, AlphaMask = maskRT };

            if (shadow)
            {
                using var shadowFx = new ShadowEffect
                {
                    Source = masked,
                    BlurAmount = 10f,
                    ShadowColor = Windows.UI.Color.FromArgb(100, 0, 0, 0)
                };
                using var shadowOffset = new Transform2DEffect
                {
                    Source = shadowFx,
                    TransformMatrix = System.Numerics.Matrix3x2.CreateTranslation(2f, 3f)
                };
                using var composite = new CompositeEffect();
                composite.Sources.Add(shadowOffset);
                composite.Sources.Add(masked);
                ds.DrawImage(composite, pad, pad);
            }
            else
            {
                ds.DrawImage(masked, pad, pad);
            }

            return new BakedRT(rt, pad);
        }

        // ── Dispose ───────────────────────────────────────────────────────────

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Current?.Dispose(); Current = null;
            Incoming?.Dispose(); Incoming = null;
            _maskRT?.Dispose(); _maskRT = null;
        }
    }

    internal sealed class BakedRT : IDisposable
    {
        public readonly CanvasRenderTarget RT;
        public readonly float Pad;
        public BakedRT(CanvasRenderTarget rt, float pad) { RT = rt; Pad = pad; }
        public void Dispose() => RT.Dispose();
    }
}