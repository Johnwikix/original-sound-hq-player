using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AnimatedWin2dControls.Controls.AnimatedTextBlock
{
    public class SharedAnimationClock
    {
        private static readonly List<WeakReference<AnimatedTextBlock>> _activeInstances = new();
        private static bool _isRunning = false;
        private static DateTimeOffset _lastTick = DateTimeOffset.Now;

        public static void Register(AnimatedTextBlock instance)
        {
            // 清理已失效的弱引用
            _activeInstances.RemoveAll(wr => !wr.TryGetTarget(out _));
            _activeInstances.Add(new WeakReference<AnimatedTextBlock>(instance));

            if (!_isRunning)
            {
                _lastTick = DateTimeOffset.Now;
                CompositionTarget.Rendering += OnRendering;
                _isRunning = true;
            }
        }

        public static void Unregister(AnimatedTextBlock instance)
        {
            _activeInstances.RemoveAll(wr =>
                !wr.TryGetTarget(out var t) || ReferenceEquals(t, instance));

            if (_activeInstances.Count == 0 && _isRunning)
            {
                CompositionTarget.Rendering -= OnRendering;
                _isRunning = false;
            }
        }

        private static void OnRendering(object sender, object e)
        {
            var now = DateTimeOffset.Now;
            var elapsed = now - _lastTick;
            _lastTick = now;

            // 清理失效引用并驱动所有活跃实例
            for (int i = _activeInstances.Count - 1; i >= 0; i--)
            {
                if (_activeInstances[i].TryGetTarget(out var instance))
                    instance.OnSharedTick(elapsed);
                else
                    _activeInstances.RemoveAt(i);
            }

            // 没有任何活跃实例时自动停止
            if (_activeInstances.Count == 0)
            {
                CompositionTarget.Rendering -= OnRendering;
                _isRunning = false;
            }
        }
    }
}
