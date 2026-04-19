using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using System;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;

namespace AnimatedWin2dControls.Controls.AlbumImgControl
{
    /// <summary>
    /// 集中管理 mask <see cref="CanvasRenderTarget"/> 和两张 <see cref="BakedRT"/>（current / incoming）。
    /// <para>
    /// 设计原则：
    /// <list type="bullet">
    ///   <item>mask 和 baked RT 均只在此类内创建和销毁；外部只持读引用。</item>
    ///   <item>任何一次 Invalidate 都会原子地先 Dispose 旧资源，再触发异步重建，
    ///         避免"已 Dispose 的 RT 仍被 GPU 使用"导致的显存泄漏。</item>
    ///   <item>异步烘焙任务启动时记录本次 <c>epoch</c>；
    ///         完成后若 epoch 已变化（说明资源已被再次 Invalidate）则直接丢弃结果，
    ///         不写回字段，确保不出现"旧烘焙覆盖新烘焙"的竞争。</item>
    /// </list>
    /// </para>
    /// </summary>
    internal sealed class BakedRTCache : IDisposable
    {
        // ── 公开只读 RT ───────────────────────────────────────────────────────

        /// <summary>当前封面的烘焙结果；null 表示尚未就绪（渲染时静默跳过）。</summary>
        public BakedRT? Current { get; private set; }

        /// <summary>即将切换封面的烘焙结果；null 表示尚未就绪。</summary>
        public BakedRT? Incoming { get; private set; }

        // ── 私有状态 ──────────────────────────────────────────────────────────

        private CanvasRenderTarget? _maskRT;
        private (float w, float h, float radius, float dpi) _maskParams;

        /// <summary>每次全量 Invalidate（尺寸/DPI/圆角变化）时递增，用于检测竞争。</summary>
        private int _epoch;

        private bool _disposed;

        // ── Incoming 提升 ────────────────────────────────────────────────────

        /// <summary>
        /// 由 <see cref="TransitionState.OnIncomingPromotedToCurrent"/> 驱动：
        /// 将 incoming BakedRT 提升为 current，旧 current 立即 Dispose。
        /// </summary>
        public void PromoteIncomingToCurrent()
        {
            Current?.Dispose();
            Current = Incoming;
            Incoming = null;
        }

        // ── mask 维护（必须在 UI 线程调用）──────────────────────────────────

        /// <summary>
        /// 确保 mask RT 与给定参数匹配；若不匹配则重建，并同步 Dispose 旧的 baked RT，
        /// 触发异步重烘焙。
        /// <para>必须在 UI 线程（CanvasControl.Draw 回调内）调用。</para>
        /// </summary>
        public bool EnsureMask(
            CanvasDevice device,
            float w, float h, float radius, float dpi,
            CanvasBitmap? currentBitmap,
            CanvasBitmap? incomingBitmap,
            bool shadow,
            Action<CanvasBitmap, float, float>? onNeedRebake)
        {
            bool needsRebuild =
                _maskRT == null ||
                MathF.Abs(_maskParams.w - w) > 0.5f ||
                MathF.Abs(_maskParams.h - h) > 0.5f ||
                MathF.Abs(_maskParams.radius - radius) > 0.5f ||
                MathF.Abs(_maskParams.dpi - dpi) > 0.5f;

            if (!needsRebuild) return false;

            // 先 Dispose 旧资源
            _maskRT?.Dispose();
            _maskRT = null;
            InvalidateBaked();

            // 重建 mask
            _maskRT = new CanvasRenderTarget(device, w, h, dpi);
            using (var ds = _maskRT.CreateDrawingSession())
            {
                ds.Clear(Microsoft.UI.Colors.Transparent);
                ds.FillRoundedRectangle(0, 0, w, h, radius, radius, Microsoft.UI.Colors.White);
            }
            _maskParams = (w, h, radius, dpi);
            _epoch++;

            // 通知外部触发重烘焙
            if (onNeedRebake != null)
            {
                if (currentBitmap != null) onNeedRebake(currentBitmap, w, h);
                if (incomingBitmap != null) onNeedRebake(incomingBitmap, w, h);
            }

            return true;
        }

        // ── 异步烘焙 ─────────────────────────────────────────────────────────

        /// <summary>
        /// 异步烘焙 <paramref name="bitmap"/> 为 <see cref="BakedRT"/>，完成后更新 <see cref="Current"/>。
        /// 若在完成前 epoch 已变化（资源已 Invalidate），结果会被丢弃。
        /// </summary>
        public async Task BakeCurrentAsync(
            CanvasBitmap bitmap,
            CanvasDevice device,
            float bakeW, float bakeH,
            bool shadow, float dpiScale,
            Func<bool>? isBitmapStillCurrent,
            Action? onReady,
            CancellationToken ct = default)
        {
            await BakeAsync(bitmap, device, bakeW, bakeH, shadow, dpiScale,
                isBitmapStillCurrent,
                result =>
                {
                    Current?.Dispose();
                    Current = result;
                    onReady?.Invoke();
                },
                ct);
        }

        /// <summary>
        /// 异步烘焙 <paramref name="bitmap"/> 为 <see cref="BakedRT"/>，完成后更新 <see cref="Incoming"/>。
        /// </summary>
        public async Task BakeIncomingAsync(
            CanvasBitmap bitmap,
            CanvasDevice device,
            float bakeW, float bakeH,
            bool shadow, float dpiScale,
            Func<bool>? isBitmapStillIncoming,
            Action? onReady,
            CancellationToken ct = default)
        {
            await BakeAsync(bitmap, device, bakeW, bakeH, shadow, dpiScale,
                isBitmapStillIncoming,
                result =>
                {
                    Incoming?.Dispose();
                    Incoming = result;
                    onReady?.Invoke();
                },
                ct);
        }

        private async Task BakeAsync(
            CanvasBitmap bitmap,
            CanvasDevice device,
            float bakeW, float bakeH,
            bool shadow, float dpiScale,
            Func<bool>? isStillValid,
            Action<BakedRT> commit,
            CancellationToken ct)
        {
            if (_maskRT == null) return;

            int epochAtStart = _epoch;
            var maskSnapshot = _maskRT; // 本地快照，防止 Task.Run 期间被 null 化

            BakedRT? baked = null;
            try
            {
                baked = await Task.Run(
                    () => BakeCore(device, bitmap, bakeW, bakeH, maskSnapshot, shadow, dpiScale),
                    ct);
            }
            catch (OperationCanceledException) { return; }
            catch { return; }

            // epoch 变化 → mask/尺寸已变，此烘焙结果已过时
            if (_epoch != epochAtStart || (isStillValid != null && !isStillValid()))
            {
                baked?.Dispose();
                return;
            }

            commit(baked!);
        }

        // ── 核心烘焙（纯计算，可在后台线程执行）────────────────────────────

        private static BakedRT BakeCore(
            CanvasDevice device,
            CanvasBitmap bitmap,
            float w, float h,
            CanvasRenderTarget maskRT,
            bool shadow, float dpiScale)
        {
            float pad = shadow ? 10f * 3f + 4f : 0f;
            float rtW = w + pad * 2f;
            float rtH = h + pad * 2f;
            float dpi = 96f * dpiScale;

            var rt = new CanvasRenderTarget(device, rtW, rtH, dpi);
            using var ds = rt.CreateDrawingSession();
            ds.Clear(Microsoft.UI.Colors.Transparent);

            float scaleRatio = Math.Min(w / bitmap.SizeInPixels.Width,
                                          h / bitmap.SizeInPixels.Height);
            var interpolation = scaleRatio < 0.5f
                ? CanvasImageInterpolation.HighQualityCubic
                : CanvasImageInterpolation.Linear;

            using var scale = new ScaleEffect
            {
                Source = bitmap,
                Scale = new Vector2(w / bitmap.SizeInPixels.Width,
                                     h / bitmap.SizeInPixels.Height),
                InterpolationMode = interpolation
            };
            using var masked = new AlphaMaskEffect
            {
                Source = scale,
                AlphaMask = maskRT
            };

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
                    TransformMatrix = Matrix3x2.CreateTranslation(2f, 3f)
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

        // ── 局部失效 ─────────────────────────────────────────────────────────

        /// <summary>
        /// 仅 Dispose 两张 baked RT（mask 保留），用于 shadow/DpiScale 仅影响烘焙而不影响 mask 时。
        /// </summary>
        public void InvalidateBaked()
        {
            Current?.Dispose(); Current = null;
            Incoming?.Dispose(); Incoming = null;
            _epoch++;
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

    // ── BakedRT ───────────────────────────────────────────────────────────────

    /// <summary>
    /// 包含阴影 padding 信息的烘焙渲染目标。
    /// <see cref="Pad"/> 是图像内容四周预留给阴影的像素数。
    /// </summary>
    internal sealed class BakedRT : IDisposable
    {
        public readonly CanvasRenderTarget RT;
        public readonly float Pad;

        public BakedRT(CanvasRenderTarget rt, float pad) { RT = rt; Pad = pad; }
        public void Dispose() => RT.Dispose();
    }
}