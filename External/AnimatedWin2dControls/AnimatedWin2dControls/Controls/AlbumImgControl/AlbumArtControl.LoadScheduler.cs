using System.Threading;
using Windows.Foundation;

namespace AnimatedWin2dControls.Controls.AlbumImgControl
{
    public sealed partial class AlbumArtControl
    {
        // ── 加载请求入口 ──────────────────────────────────────────────────────

        /// <summary>
        /// 请求加载新图片。
        ///
        /// isResize=true：绕过所有锁，直接替换，不播动画。
        ///
        /// 普通请求流程（序列防抖）：
        ///
        ///   阶段 A — _animLock=false 且 _sequenceActive=false（空闲状态）：
        ///     dedup 通过后 → 设 _animLock=true → 进解码 → 播第一帧动画
        ///     这是"序列开始"动画。
        ///
        ///   阶段 B — _animLock=true（动画正在播放）：
        ///     只更新 _pendingAfterAnim，不进解码。
        ///     动画播完 → UnlockAndCheckPending：
        ///       有 pending → 进入阶段 C（序列检测）
        ///       无 pending → 回到阶段 A
        ///
        ///   阶段 C — _sequenceActive=true（序列结束检测窗口）：
        ///     启动 _sequenceEndCts 等待 AnimLockMs。
        ///     等待期间收到新请求 → 只更新 _pendingAfterAnim，重置计时。
        ///     等待期满无新请求 → SequenceEndFire：
        ///       触发 RequestLoad(pending) → 播最后一帧动画 → 回到阶段 A/B。
        ///
        ///   对外效果：连续快速切换只播两次动画（第一张 + 最后一张），中间静默跳过。
        /// </summary>
        public void RequestLoad(byte[]? bytes, bool isResize = false)
        {
            if (_disposed) return;
            if (!IsActive && _isResourcesCreated) return;

            byte[]? targetBytes = (bytes is { Length: > 0 }) ? bytes : null;

            // isResize 绕过所有状态，直接进解码
            if (isResize)
            {
                DispatchToDecoder(targetBytes, isResize: true);
                return;
            }

            // 阶段 B：动画正在播放，暂存请求
            if (_animLock)
            {
                _pendingAfterAnim = targetBytes;
                return;
            }

            // 阶段 C：序列检测窗口，重置计时并暂存
            if (_sequenceActive)
            {
                _pendingAfterAnim = targetBytes;
                RestartSequenceEndTimer();
                return;
            }

            // 阶段 A：空闲，dedup 检查后进解码
            if (IsDuplicate(targetBytes)) return;

            UpdateLastHash(targetBytes);
            _animLock = true;

            DispatchToDecoder(targetBytes, isResize: false);
        }

        // ── 派发到解码器 ──────────────────────────────────────────────────────

        private void DispatchToDecoder(byte[]? bytes, bool isResize)
        {
            float cw = 0f, ch = 0f;
            if (_canvas is { } c)
            {
                ComputeContentRect((float)c.Size.Width, (float)c.Size.Height);
                cw = (float)_contentRect.Width;
                ch = (float)_contentRect.Height;
            }

            Interlocked.Exchange(ref _pendingRequest,
                new PendingRequest(bytes, cw, ch, IsShadowEnabled, isResize, IsDark));

            TrySignalDecode();
        }

        private void TrySignalDecode()
        {
            if (_decodeSignal.CurrentCount == 0)
                _decodeSignal.Release();
        }
    }
}
