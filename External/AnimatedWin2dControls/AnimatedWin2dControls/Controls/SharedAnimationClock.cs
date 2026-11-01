#nullable enable
using System;
using AnimatedWin2dControls.Interop;
using System.Collections.Generic;
using System.Diagnostics;

namespace AnimatedWin2dControls.Controls
{
    public static class SharedAnimationClock
    {
        private static readonly List<WeakReference<ISharedTickable>?> _activeInstances = new();
        private static FrameEventSubscription? _subscription;
        private static int _dispatchDepth;
        private static long _lastTimestamp;

        public static void Register(ISharedTickable instance)
        {
            if (_dispatchDepth == 0) Compact();

            foreach (var wr in _activeInstances)
            {
                if (wr is not null && wr.TryGetTarget(out var existing) && ReferenceEquals(existing, instance))
                    return;
            }

            _activeInstances.Add(new WeakReference<ISharedTickable>(instance));

            if (_subscription is null)
            {
                _lastTimestamp = Stopwatch.GetTimestamp();
                try { _subscription = FrameEventSubscription.SubscribeRendering(OnRendering); }
                catch
                {
                    Unregister(instance);
                    throw;
                }
            }
        }

        public static void Unregister(ISharedTickable instance)
        {
            for (int i = 0; i < _activeInstances.Count; i++)
            {
                var reference = _activeInstances[i];
                if (reference is null || !reference.TryGetTarget(out var target) || ReferenceEquals(target, instance))
                    _activeInstances[i] = null;
            }
            if (_dispatchDepth == 0) Compact();
            StopIfEmpty();
        }

        private static void OnRendering()
        {
            var now = Stopwatch.GetTimestamp();
            var elapsed = Stopwatch.GetElapsedTime(_lastTimestamp, now);
            _lastTimestamp = now;

            // 防止暂停/首帧后超大 delta 造成动画跳变
            if (elapsed.TotalSeconds > 0.1)
                elapsed = TimeSpan.FromSeconds(0.1);

            _dispatchDepth++;
            try
            {
                // Tick 可以自行注销或注册其他实例；本帧保留槽位，避免移动索引或重复推进。
                for (int i = _activeInstances.Count - 1; i >= 0; i--)
                {
                    var reference = _activeInstances[i];
                    if (reference is not null && reference.TryGetTarget(out var instance))
                        instance.OnSharedTick(elapsed);
                    else
                        _activeInstances[i] = null;
                }
            }
            finally
            {
                _dispatchDepth--;
                if (_dispatchDepth == 0) Compact();
                StopIfEmpty();
            }
        }

        private static void Compact()
            => _activeInstances.RemoveAll(static reference => reference is null || !reference.TryGetTarget(out _));

        private static void StopIfEmpty()
        {
            foreach (var reference in _activeInstances)
                if (reference is not null && reference.TryGetTarget(out _)) return;
            var subscription = _subscription;
            _subscription = null;
            subscription?.Dispose();
        }
    }
}
