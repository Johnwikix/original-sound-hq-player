using System;

namespace AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.Advance
{
    /// <summary>
    /// One lyric group's scroll position. Delayed targets do not freeze an existing
    /// movement; spring retargeting preserves velocity. Updated independently of glyph visibility.
    /// </summary>
    public sealed class LyricScrollMotion
    {
        private double _target, _start, _elapsed, _duration;
        private double _pendingTarget, _pendingDuration, _delay;
        private bool _spring, _pendingSpring, _pending, _moving;
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
        }

        public void Start(double target, double duration, double delay, bool spring,
            Func<double, double, double, double> interpolator)
        {
            if (duration <= 0) { JumpTo(target); return; }
            if (target == TargetValue) return;
            if (delay > 0)
            {
                // Repeated short lines replace the queued destination, not its deadline.
                _delay = _pending ? Math.Min(_delay, delay) : delay;
                _pendingTarget = target;
                _pendingDuration = duration;
                _pendingSpring = spring;
                _pendingInterpolator = interpolator;
                _pending = true;
            }
            else
            {
                _pending = false;
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
                Advance(beforeTarget);
                _delay -= beforeTarget;
                seconds -= beforeTarget;
                if (_delay > 1e-9) return;
                _pending = false;
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
                double omega = 7.0 / _duration;
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
