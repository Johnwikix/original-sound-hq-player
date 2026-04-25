using AnimatedWin2dControls.Utils;
using Microsoft.UI.Dispatching;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace AnimatedWin2dControls.Controls.AlbumImgControl
{
    public sealed partial class AlbumArtControl
    {
        // ── 过渡动画启动 ──────────────────────────────────────────────────────

        private void BeginTransition()
        {
            _t             = 0f;
            _isFading      = true;
            _lastDrawTicks = 0;

            // 先构造新 CTS，再取消旧的，避免 double-dispose
            var newCts = new CancellationTokenSource();
            var oldCts = Interlocked.Exchange(ref _animLockCts, newCts);
            oldCts.Cancel();
            oldCts.Dispose();

            var ct = newCts.Token;
            var dq = _canvas?.DispatcherQueue;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(AnimLockMs, ct).ConfigureAwait(false);
                    dq?.TryEnqueue(DispatcherQueuePriority.Normal, UnlockAndCheckPending);
                }
                catch (OperationCanceledException) { }
            });

            _canvas?.Invalidate();
        }

        // ── 动画帧推进 ────────────────────────────────────────────────────────

        private void TickAnimation()
        {
            long  now   = System.Diagnostics.Stopwatch.GetTimestamp();
            float delta = 0f;

            if (_lastDrawTicks != 0)
            {
                float elapsed = (float)((double)(now - _lastDrawTicks)
                                        / System.Diagnostics.Stopwatch.Frequency);
                delta = Math.Min(elapsed, 0.1f);
            }
            _lastDrawTicks = now;

            _t = Math.Min(1f, _t + delta * FadeSpeed);

            if (_t >= 0.5f && _nextBmp != null)
            {
                _currentBmp?.Dispose();
                _currentBmp  = _nextBmp;
                _currentInfo = _nextInfo;
                _nextBmp     = null;
            }

            if (_t >= 1f)
            {
                _t             = 0f;
                _isFading      = false;
                _lastDrawTicks = 0;
                _currentDisplayHash = _lastHash;
            }
        }

        // ── 动画锁解除与序列检测 ──────────────────────────────────────────────

        private void UnlockAndCheckPending()
        {
            if (_disposed) return;

            _animLock = false;

            var pending         = _pendingAfterAnim;
            _pendingAfterAnim   = null;

            if (pending == null) return;

            _sequenceActive   = true;
            _pendingAfterAnim = pending;
            RestartSequenceEndTimer();
        }

        private void CheckPendingAfterUnlock() => UnlockAndCheckPending();

        // ── 序列结束计时器 ────────────────────────────────────────────────────

        private void RestartSequenceEndTimer()
        {
            // 先构造新 CTS，再取消旧的，避免 double-dispose
            var newCts = new CancellationTokenSource();
            var oldCts = Interlocked.Exchange(ref _sequenceEndCts, newCts);
            oldCts.Cancel();
            oldCts.Dispose();

            var ct = newCts.Token;
            var dq = _canvas?.DispatcherQueue;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(AnimLockMs, ct).ConfigureAwait(false);
                    dq?.TryEnqueue(DispatcherQueuePriority.Normal, SequenceEndFire);
                }
                catch (OperationCanceledException) { }
            });
        }

        private void SequenceEndFire()
        {
            if (_disposed) return;

            _sequenceActive = false;

            var pending       = _pendingAfterAnim;
            _pendingAfterAnim = null;

            if (pending == null) return;

            int pendingHash = ToolUtils.ComputeFastHash(pending);
            if (pending.Length == _lastLength && pendingHash == _currentDisplayHash)
                return;

            RequestLoad(pending);
        }

        // ── 动画锁取消 ────────────────────────────────────────────────────────

        private void CancelAnimLock()
        {
            // 替换为已取消的 dummy CTS，确保旧 CTS 只被 Cancel+Dispose 一次
            var oldCts = Interlocked.Exchange(ref _animLockCts, new CancellationTokenSource());
            oldCts.Cancel();
            oldCts.Dispose();
        }

        private void CancelSequenceEnd()
        {
            var oldCts = Interlocked.Exchange(ref _sequenceEndCts, new CancellationTokenSource());
            oldCts.Cancel();
            oldCts.Dispose();
        }
    }
}
