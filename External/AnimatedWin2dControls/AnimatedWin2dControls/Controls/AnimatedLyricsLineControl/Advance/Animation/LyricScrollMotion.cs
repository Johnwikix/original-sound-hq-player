using System;

namespace AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.Advance
{
    /// <summary>
    /// Tracks one lyric group's scroll position. Delayed targets normally hold the current
    /// position and preserve spring velocity for resumption; FlowWave may let an already
    /// moving row finish its current motion while the replacement target waits.
    /// Updated independently of glyph visibility.
    /// </summary>
    public sealed class LyricScrollMotion
    {
        private double _target, _start, _elapsed, _duration;
        private double _pendingTarget, _pendingDuration, _delay;
        private bool _spring, _pendingSpring, _pending, _moving;
        private bool _advanceWhilePending;
        private Func<double, double, double, double>? _interpolator, _pendingInterpolator;

        public double Value { get; private set; }
        public double Velocity { get; private set; }
        public double TargetValue => _pending ? _pendingTarget : _target;
        public bool IsMoving => _moving || _pending;

        public void JumpTo(double target)
        {
            Value = _target = target;
            Velocity = 0;
            _pending = _moving = false;
            _delay = 0;
            _advanceWhilePending = false;
        }

        public void Start(double target, double duration, double delay, bool spring,
            Func<double, double, double, double> interpolator,
            bool continueCurrentMotionDuringDelay = false)
        {
            if (duration <= 0) { JumpTo(target); return; }
            if (target == TargetValue) return;
            if (delay > 0)
            {
                _delay = delay;
                _pendingTarget = target;
                _pendingDuration = duration;
                _pendingSpring = spring;
                _pendingInterpolator = interpolator;
                _pending = true;
                _advanceWhilePending = continueCurrentMotionDuringDelay && _moving;
            }
            else
            {
                _pending = false;
                _advanceWhilePending = false;
                Begin(target, duration, spring, interpolator);
            }
        }

        private void Begin(double target, double duration, bool spring,
            Func<double, double, double, double> interpolator)
        {
            _target = target;
            _start = Value;
            _elapsed = 0;
            _duration = duration;
            _spring = spring;
            _interpolator = interpolator;
            _moving = true;
        }

        public void Update(double seconds)
        {
            if (!double.IsFinite(seconds) || seconds <= 0) return;
            if (_pending)
            {
                double beforeTarget = Math.Min(seconds, _delay);
                _delay -= beforeTarget;
                seconds -= beforeTarget;
                if (_advanceWhilePending) Advance(beforeTarget);
                if (_delay > 1e-9) return;
                _pending = false;
                _advanceWhilePending = false;
                Begin(_pendingTarget, _pendingDuration, _pendingSpring, _pendingInterpolator!);
            }
            Advance(seconds);
        }

        private void Advance(double seconds)
        {
            if (!_moving || seconds <= 0) return;
            if (_spring)
            {
                // Exact critically damped spring solution; independent of frame rate.
                // At the configured duration a resting spring has covered about 99.3%.
                double omega = 7.0 / Math.Max(0.01, _duration);
                double displacement = Value - _target;
                double coefficient = Velocity + omega * displacement;
                double decay = Math.Exp(-omega * seconds);
                Value = _target + (displacement + coefficient * seconds) * decay;
                Velocity = (Velocity - omega * coefficient * seconds) * decay;
                if (Math.Abs(Value - _target) < 0.01 && Math.Abs(Velocity) < 0.01)
                {
                    Value = _target;
                    Velocity = 0;
                    _moving = false;
                }
            }
            else
            {
                _elapsed += seconds;
                double previous = Value;
                Value = _interpolator!(_start, _target, Math.Min(1, _elapsed / _duration));
                Velocity = (Value - previous) / seconds;
                if (_elapsed >= _duration)
                {
                    Value = _target;
                    Velocity = 0;
                    _moving = false;
                }
            }
        }

        /// <summary>
        /// Shares the next line's time budget between propagation and tween response.
        /// The first visible row leads the wave; short lines lose stagger smoothly.
        /// Springs keep their configured response: shrinking it to each short line creates
        /// repeated acceleration pulses. Only propagation is shortened for springs.
        /// </summary>
        public static (double Duration, double Delay) FlowWaveTiming(
            int index, int firstVisibleIndex, double duration, double interval,
            bool spring = false)
        {
            // 值元组和标量运算：逐帧调用无堆分配，沿用项目现有 .NET 版本。
            duration = Math.Max(0, duration);
            double configuredDuration = duration;
            bool hasInterval = double.IsFinite(interval) && interval > 0;
            if (hasInterval) duration = Math.Min(duration, interval * 0.85);
            double strength = hasInterval ? Math.Clamp((interval - 0.25) / 0.25, 0, 1) : 1;
            strength = strength * strength * (3 - 2 * strength);
            double delay = StaggerDelay(index, firstVisibleIndex, duration) * strength;
            if (hasInterval) duration = Math.Min(duration, interval * 0.9 - delay);
            return (spring ? configuredDuration : duration, delay);
        }

        public static double StaggerDelay(int index, int firstVisibleIndex, double duration)
        {
            double budget = Math.Min(0.4, Math.Max(0, duration) * 0.75);
            if (budget <= 0) return 0;
            // Taper the intervals instead of making all distant rows start at the cap.
            // The leading rows start ~80 ms apart so the cascade stays readable.
            return budget * (1.0 - Math.Exp(-Math.Max(0, index - firstVisibleIndex) * 0.09 / budget));
        }
    }
}
