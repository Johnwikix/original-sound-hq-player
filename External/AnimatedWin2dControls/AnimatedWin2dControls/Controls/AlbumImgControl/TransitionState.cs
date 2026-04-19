using Microsoft.Graphics.Canvas;
using System;
using System.Collections.Generic;
using Windows.Foundation;

namespace AnimatedWin2dControls.Controls.AlbumImgControl
{
    /// <summary>
    /// 管理 current/incoming/queued 三张位图的过渡状态机。
    /// <para>
    /// 所有位图的所有权都在此类中集中管理：
    /// 不再需要的位图将进入 <see cref="_disposeQueue"/>，
    /// 只有在确认没有任何字段引用该位图后才真正 Dispose，
    /// 以彻底避免"Dispose 后仍被 GPU 引用"的显存泄漏。
    /// </para>
    /// </summary>
    internal sealed class TransitionState : IDisposable
    {
        // ── 公开状态（DrawingRenderer 只读） ─────────────────────────────────

        public CanvasBitmap? CurrentBitmap { get; private set; }
        public CanvasBitmap? IncomingBitmap { get; private set; }

        /// <summary>过渡进度，线性 [0,1]。0 = 完全 current，1 = 完全 incoming。</summary>
        public float TransitionT { get; private set; }

        /// <summary>是否正在过渡中。</summary>
        public bool IsFading { get; private set; }

        /// <summary>
        /// incoming 目标矩形是否需要重新计算（canvas 尺寸变化时置 true）。
        /// DrawingRenderer 计算后应将此字段置 false。
        /// </summary>
        public bool TargetRectDirty { get; set; } = true;

        /// <summary>过渡开始时 current 的绘制矩形（用于 Lerp）。</summary>
        public Rect CurrentDestRectAtStart { get; set; } = Rect.Empty;

        /// <summary>incoming 的目标矩形（DrawingRenderer 写入）。</summary>
        public Rect IncomingTargetRect { get; set; } = Rect.Empty;

        // ── 私有 ─────────────────────────────────────────────────────────────

        private CanvasBitmap? _queuedBitmap;

        /// <summary>
        /// 准备安全释放的位图队列。
        /// 仅在确认位图不被任何字段引用时才执行 Dispose。
        /// </summary>
        private readonly Queue<CanvasBitmap> _disposeQueue = new();

        private const float FadeSpeed = 1.5f;
        private bool _disposed;

        // ── 主入口 ────────────────────────────────────────────────────────────

        /// <summary>
        /// 接收一张新位图并驱动状态机：
        /// 若当前无过渡则立即开始；否则进入队列（只保留最新一张）。
        /// </summary>
        /// <returns>true 表示需要启动渲染循环。</returns>
        public bool Enqueue(CanvasBitmap newBitmap)
        {
            if (IsFading)
            {
                // 只保留最新一帧，旧的 queued 入队等待释放
                if (_queuedBitmap != null)
                    SafeEnqueueDispose(_queuedBitmap);
                _queuedBitmap = newBitmap;
            }
            else
            {
                BeginTransition(newBitmap);
            }

            return true; // 调用方启动渲染循环
        }

        /// <summary>
        /// 每帧推进过渡进度。
        /// </summary>
        /// <param name="delta">帧间隔秒数（已 clamp 到 0.1）。</param>
        /// <returns>true 表示仍有动画需要继续，false 表示可停止渲染循环。</returns>
        public bool Advance(float delta)
        {
            FlushDisposeQueue();

            if (!IsFading && _queuedBitmap == null)
                return false;

            if (!IsFading)
                return true; // 有 queued，等下一帧再检查

            TransitionT = Math.Min(1f, TransitionT + delta * FadeSpeed);

            if (TransitionT >= 1f)
            {
                // 过渡完成：incoming 提升为 current
                CommitTransition();

                if (_queuedBitmap != null)
                {
                    var next = _queuedBitmap;
                    _queuedBitmap = null;
                    BeginTransition(next);
                }
            }

            return IsFading || _queuedBitmap != null;
        }

        // ── 通知 BakedRTCache 何时可以烘焙 incoming ──────────────────────────

        /// <summary>
        /// 当 incoming bitmap 的烘焙任务完成后，外部调用此方法通知状态机。
        /// （TransitionState 本身不持有 BakedRT，仅作协调）
        /// </summary>
        public bool IsIncomingStillPending(CanvasBitmap bitmap)
            => ReferenceEquals(IncomingBitmap, bitmap);

        public bool IsCurrentStillPending(CanvasBitmap bitmap)
            => ReferenceEquals(CurrentBitmap, bitmap);

        // ── 内部过渡逻辑 ─────────────────────────────────────────────────────

        private void BeginTransition(CanvasBitmap newBitmap)
        {
            // 若有未完成的 incoming，先提升为 current（跳过其剩余过渡）
            if (IncomingBitmap != null)
                PromoteIncomingToCurrent();

            IncomingBitmap = newBitmap;
            TransitionT = 0f;
            TargetRectDirty = true;
            IsFading = true;
        }

        private void CommitTransition()
        {
            if (CurrentBitmap != null)
                SafeEnqueueDispose(CurrentBitmap);

            CurrentBitmap = IncomingBitmap;
            IncomingBitmap = null;
            TransitionT = 0f;
            IsFading = false;
        }

        /// <summary>
        /// 将 incoming 直接提升为 current（用于新过渡打断旧过渡时）。
        /// incoming baked RT 由 BakedRTCache 通过 OnPromoteIncoming 回调处理。
        /// </summary>
        private void PromoteIncomingToCurrent()
        {
            if (CurrentBitmap != null)
                SafeEnqueueDispose(CurrentBitmap);

            CurrentBitmap = IncomingBitmap;
            IncomingBitmap = null;
            OnIncomingPromotedToCurrent?.Invoke();
        }

        /// <summary>
        /// 当 incoming bitmap 被提升为 current 时触发，
        /// 通知 <see cref="BakedRTCache"/> 相应提升其 baked RT。
        /// </summary>
        public event Action? OnIncomingPromotedToCurrent;

        // ── Dispose 队列 ─────────────────────────────────────────────────────

        private void SafeEnqueueDispose(CanvasBitmap bmp)
        {
            // 三重检查：确保该位图确实不再被任何活跃字段引用
            if (!ReferenceEquals(bmp, CurrentBitmap) &&
                !ReferenceEquals(bmp, IncomingBitmap) &&
                !ReferenceEquals(bmp, _queuedBitmap))
            {
                _disposeQueue.Enqueue(bmp);
            }
            // 若仍被引用，说明调用方逻辑有误，静默跳过（不 Dispose，避免悬空）
        }

        private void FlushDisposeQueue()
        {
            while (_disposeQueue.TryDequeue(out var bmp))
            {
                // 最终安全检查（防御性）
                if (!ReferenceEquals(bmp, CurrentBitmap) &&
                    !ReferenceEquals(bmp, IncomingBitmap) &&
                    !ReferenceEquals(bmp, _queuedBitmap))
                {
                    bmp.Dispose();
                }
            }
        }

        // ── 缓动函数（静态，供 DrawingRenderer 使用）────────────────────────

        /// <summary>Cubic ease-out：快进慢出。</summary>
        public static float EaseOut(float t, float n = 8f)
        {
            float f = 1f - t;
            return 1f - MathF.Pow(f, n);
        }

        // ── Dispose ───────────────────────────────────────────────────────────

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // 先将所有活跃位图入队，再全量 flush
            if (_queuedBitmap != null) _disposeQueue.Enqueue(_queuedBitmap);
            if (IncomingBitmap != null) _disposeQueue.Enqueue(IncomingBitmap);
            if (CurrentBitmap != null) _disposeQueue.Enqueue(CurrentBitmap);

            _queuedBitmap = null;
            IncomingBitmap = null;
            CurrentBitmap = null;

            // 此时三个字段均为 null，可以安全 Dispose 所有排队项
            while (_disposeQueue.TryDequeue(out var bmp))
                bmp.Dispose();
        }
    }
}