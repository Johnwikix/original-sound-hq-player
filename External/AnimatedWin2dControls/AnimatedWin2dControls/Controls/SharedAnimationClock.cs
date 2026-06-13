using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace AnimatedWin2dControls.Controls
{
    public static class SharedAnimationClock
    {
        private static readonly List<WeakReference<ISharedTickable>> _activeInstances = new();
        private static bool _isRunning = false;
        private static long _lastTimestamp;

        public static void Register(ISharedTickable instance)
        {
            _activeInstances.RemoveAll(wr => !wr.TryGetTarget(out _));

            foreach (var wr in _activeInstances)
            {
                if (wr.TryGetTarget(out var existing) && ReferenceEquals(existing, instance))
                    return;
            }

            _activeInstances.Add(new WeakReference<ISharedTickable>(instance));

            if (!_isRunning)
            {
                _lastTimestamp = Stopwatch.GetTimestamp();
                CompositionTarget.Rendering += OnRendering;
                _isRunning = true;
            }
        }

        public static void Unregister(ISharedTickable instance)
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
            var now = Stopwatch.GetTimestamp();
            var elapsed = Stopwatch.GetElapsedTime(_lastTimestamp, now);
            _lastTimestamp = now;

            // 防止暂停/首帧后超大 delta 造成动画跳变
            if (elapsed.TotalSeconds > 0.1)
                elapsed = TimeSpan.FromSeconds(0.1);

            for (int i = _activeInstances.Count - 1; i >= 0; i--)
            {
                if (_activeInstances[i].TryGetTarget(out var instance))
                    instance.OnSharedTick(elapsed);
                else
                    _activeInstances.RemoveAt(i);
            }

            if (_activeInstances.Count == 0)
            {
                CompositionTarget.Rendering -= OnRendering;
                _isRunning = false;
            }
        }
    }
}