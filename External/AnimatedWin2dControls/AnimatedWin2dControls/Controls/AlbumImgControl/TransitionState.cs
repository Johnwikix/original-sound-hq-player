//using Microsoft.Graphics.Canvas;
//using System;
//using System.Collections.Generic;
//using Windows.Foundation;

//namespace AnimatedWin2dControls.Controls.AlbumImgControl
//{
//    /// <summary>
//    /// 忠实还原原始代码的过渡状态机，仅做搬运，不引入新的状态协议。
//    /// current/incoming/queued 三张位图的所有权集中在此类。
//    /// </summary>
//    internal sealed class TransitionState : IDisposable
//    {
//        // ── 公开状态 ──────────────────────────────────────────────────────────

//        public CanvasBitmap? CurrentBitmap { get; private set; }
//        public CanvasBitmap? IncomingBitmap { get; private set; }

//        public float TransitionT { get; private set; }
//        public bool IsFading { get; private set; }

//        /// <summary>过渡开始时 current 的绘制矩形（用于位置插值起点）。</summary>
//        public Rect CurrentDestRectAtStart { get; set; } = Rect.Empty;

//        /// <summary>incoming 的目标矩形；DrawingRenderer 在首帧写入后缓存。</summary>
//        public Rect IncomingTargetRect { get; set; } = Rect.Empty;

//        /// <summary>true 时 DrawingRenderer 需要重新计算并写入 IncomingTargetRect。</summary>
//        public bool TargetRectDirty { get; set; } = true;

//        // ── 私有 ─────────────────────────────────────────────────────────────

//        private CanvasBitmap? _queuedBitmap;
//        private readonly Queue<CanvasBitmap> _disposeQueue = new();
//        private const float FadeSpeed = 1.5f;
//        private bool _disposed;

//        // ── 事件：通知外部同步 BakedRTCache ──────────────────────────────────

//        /// <summary>
//        /// incoming bitmap 被提升为 current 时触发（包括打断过渡和正常完成两种情况）。
//        /// BakedRTCache 在此事件中将自己的 Incoming RT 提升为 Current RT。
//        /// </summary>
//        public event Action? IncomingPromotedToCurrent;

//        // ── 主入口 ────────────────────────────────────────────────────────────

//        public bool Enqueue(CanvasBitmap newBitmap)
//        {
//            if (IsFading)
//            {
//                if (_queuedBitmap != null) SafeEnqueueDispose(_queuedBitmap);
//                _queuedBitmap = newBitmap;
//            }
//            else
//            {
//                BeginTransition(newBitmap);
//            }
//            return true;
//        }

//        public bool Advance(float delta)
//        {
//            FlushDisposeQueue();

//            if (!IsFading && _queuedBitmap == null) return false;
//            if (!IsFading) return true;

//            TransitionT = Math.Min(1f, TransitionT + delta * FadeSpeed);

//            if (TransitionT >= 1f)
//            {
//                CommitTransition();
//                if (_queuedBitmap != null)
//                {
//                    var next = _queuedBitmap;
//                    _queuedBitmap = null;
//                    BeginTransition(next);
//                }
//            }

//            return IsFading || _queuedBitmap != null;
//        }

//        // ── 查询辅助（供 BakedRTCache 的 isStillValid 回调使用）─────────────

//        public bool IsStillIncoming(CanvasBitmap bmp) => ReferenceEquals(IncomingBitmap, bmp);
//        public bool IsStillCurrent(CanvasBitmap bmp) => ReferenceEquals(CurrentBitmap, bmp);

//        // ── 内部状态机 ────────────────────────────────────────────────────────

//        private void BeginTransition(CanvasBitmap newBitmap)
//        {
//            if (IncomingBitmap != null)
//                PromoteIncomingToCurrent(); // 打断旧过渡

//            IncomingBitmap = newBitmap;
//            TransitionT = 0f;
//            TargetRectDirty = true;
//            IsFading = true;
//        }

//        private void CommitTransition()
//        {
//            // 正常完成：incoming → current
//            if (CurrentBitmap != null) SafeEnqueueDispose(CurrentBitmap);
//            CurrentBitmap = IncomingBitmap;
//            IncomingBitmap = null;
//            TransitionT = 0f;
//            IsFading = false;
//            IncomingPromotedToCurrent?.Invoke();
//        }

//        private void PromoteIncomingToCurrent()
//        {
//            // 打断完成：incoming → current（跳过剩余动画）
//            if (CurrentBitmap != null) SafeEnqueueDispose(CurrentBitmap);
//            CurrentBitmap = IncomingBitmap;
//            IncomingBitmap = null;
//            IncomingPromotedToCurrent?.Invoke();
//        }

//        // ── Dispose 队列 ─────────────────────────────────────────────────────

//        private void SafeEnqueueDispose(CanvasBitmap bmp)
//        {
//            if (!ReferenceEquals(bmp, CurrentBitmap) &&
//                !ReferenceEquals(bmp, IncomingBitmap) &&
//                !ReferenceEquals(bmp, _queuedBitmap))
//                _disposeQueue.Enqueue(bmp);
//        }

//        public void FlushDisposeQueue()
//        {
//            while (_disposeQueue.TryDequeue(out var bmp))
//            {
//                if (!ReferenceEquals(bmp, CurrentBitmap) &&
//                    !ReferenceEquals(bmp, IncomingBitmap) &&
//                    !ReferenceEquals(bmp, _queuedBitmap))
//                    bmp.Dispose();
//            }
//        }

//        // ── 缓动（供 DrawingRenderer 使用）───────────────────────────────────

//        public static float EaseOut(float t, float n = 8f)
//            => 1f - MathF.Pow(1f - t, n);

//        // ── Dispose ───────────────────────────────────────────────────────────

//        public void Dispose()
//        {
//            if (_disposed) return;
//            _disposed = true;
//            if (_queuedBitmap != null) _disposeQueue.Enqueue(_queuedBitmap);
//            if (IncomingBitmap != null) _disposeQueue.Enqueue(IncomingBitmap);
//            if (CurrentBitmap != null) _disposeQueue.Enqueue(CurrentBitmap);
//            _queuedBitmap = null;
//            IncomingBitmap = null;
//            CurrentBitmap = null;
//            while (_disposeQueue.TryDequeue(out var bmp)) bmp.Dispose();
//        }
//    }
//}