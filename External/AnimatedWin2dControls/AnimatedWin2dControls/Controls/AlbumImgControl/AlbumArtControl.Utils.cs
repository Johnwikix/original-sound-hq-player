using AnimatedWin2dControls.Utils;
using System;
using System.Threading;
using Windows.Foundation;

namespace AnimatedWin2dControls.Controls.AlbumImgControl
{
    public sealed partial class AlbumArtControl
    {
        // ── 几何工具 ──────────────────────────────────────────────────────────

        /// <summary>
        /// 计算内容区域，结果缓存至 <see cref="_contentRect"/>。
        /// 仅当画布尺寸变化时重新计算。
        /// </summary>
        private void ComputeContentRect(float cw, float ch)
        {
            // ReSharper disable once CompareOfFloatsByEqualityOperator
            if (cw == _cachedContentW && ch == _cachedContentH) return;

            _cachedContentW = cw;
            _cachedContentH = ch;

            float w = cw - Margin * 2, h = ch - Margin * 2;
            _contentRect = (w > 0 && h > 0) ? new Rect(Margin, Margin, w, h) : Rect.Empty;
        }

        private static Rect CalcAspectFitRect(float srcW, float srcH, Rect cr)
        {
            float cw = (float)cr.Width, ch = (float)cr.Height;
            if (srcW <= 0 || srcH <= 0 || cw <= 0 || ch <= 0) return cr;

            float aspect = srcW / srcH;
            float dw, dh;
            if (aspect >= cw / ch) { dw = cw; dh = dw / aspect; }
            else { dh = ch; dw = dh * aspect; }

            return new Rect(cr.X + (cw - dw) * 0.5f, cr.Y + (ch - dh) * 0.5f, dw, dh);
        }

        // ── 缓动函数 ──────────────────────────────────────────────────────────

        private static float EaseIn(float t) => t * t;
        private static float EaseOut(float t) => 1f - (1f - t) * (1f - t);

        // ── 去重（dedup）工具 ─────────────────────────────────────────────────

        private void InvalidateDedup()
        {
            _lastLength = NeverInitialized;
            _lastHash = 0;
            _currentDisplayHash = -1;
        }

        private bool IsDuplicate(byte[]? b)
        {
            // 从未初始化（NeverInitialized 哨兵），不判重
            if (_lastLength == NeverInitialized) return false;

            if (b == null || b.Length == 0)
                return _lastLength == -1;

            int hash = ToolUtils.ComputeFastHash(b);
            return b.Length == _lastLength && hash == _lastHash;
        }

        private void UpdateLastHash(byte[]? b)
        {
            if (b == null || b.Length == 0)
            {
                _lastLength = -1;
                _lastHash = 0;
                return;
            }
            _lastLength = b.Length;
            _lastHash = ToolUtils.ComputeFastHash(b);
        }

        // ── Dispose ───────────────────────────────────────────────────────────

        private void DetachCanvasEvents()
        {
            if (_canvas is null) return;
            _canvas.CreateResources -= Canvas_CreateResources;
            _canvas.Draw -= Canvas_Draw;
            _canvas.SizeChanged -= Canvas_SizeChanged;
        }

        public void Dispose() { Dispose(true); GC.SuppressFinalize(this); }

        private void Dispose(bool disposing)
        {
            if (!disposing || _disposed) return;
            _disposed = true;
            _isResourcesCreated = false;

            var pCts = Interlocked.Exchange(ref _pipelineCts, new CancellationTokenSource());
            pCts.Cancel();
            pCts.Dispose();

            _decodeChannel.Writer.TryComplete();
            _decodeSignal.Dispose();

            CancelAnimLock();
            CancelSequenceEnd();

            var rCts = Interlocked.Exchange(ref _resizeCts, new CancellationTokenSource());
            rCts.Cancel();
            rCts.Dispose();

            DetachCanvasEvents();
            _canvas = null;

            _currentBmp?.Dispose();
            _nextBmp?.Dispose();
            XamlRoot?.Changed -= XamlRoot_Changed;
        }
    }
}
