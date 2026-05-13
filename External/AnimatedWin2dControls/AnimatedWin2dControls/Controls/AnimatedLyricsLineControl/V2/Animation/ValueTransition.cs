using System;
using System.Collections.Generic;

namespace AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.V2
{
    public class ValueTransition<T> where T : struct
    {
        private T _currentValue;
        private T _startValue;
        private T _targetValue;

        private readonly Queue<Keyframe<T>> _keyframeQueue = new();

        private double _stepDuration;
        private double _totalDurationForAutoSplit;
        private double _configuredDelaySeconds;

        private Func<T, T, double, T> _interpolator;
        private bool _isTransitioning;
        private double _progress;

        public T Value => _currentValue;
        public bool IsTransitioning => _isTransitioning;
        public T TargetValue => _targetValue;
        public double DurationSeconds => _totalDurationForAutoSplit;
        public double Progress => _progress;

        public Func<T, T, double, T> Interpolator => _interpolator;

        public ValueTransition(T initialValue, Func<T, T, double, T> interpolator, double defaultTotalDuration = 0.3)
        {
            _currentValue = initialValue;
            _startValue = initialValue;
            _targetValue = initialValue;
            _totalDurationForAutoSplit = defaultTotalDuration;
            _interpolator = interpolator;
        }

        public void SetDuration(double seconds)
        {
            if (seconds < 0) throw new ArgumentOutOfRangeException(nameof(seconds));
            _totalDurationForAutoSplit = seconds;
        }

        public void SetDurationMs(double millionSeconds) => SetDuration(millionSeconds / 1000.0);

        public void SetDelay(double seconds)
        {
            _configuredDelaySeconds = seconds;
        }

        public void SetInterpolator(Func<T, T, double, T> interpolator)
        {
            _interpolator = interpolator;
        }

        public void JumpTo(T value)
        {
            _keyframeQueue.Clear();
            _currentValue = value;
            _startValue = value;
            _targetValue = value;
            _isTransitioning = false;
            _progress = 0;
        }

        public void Start(params Keyframe<T>[] keyframes)
        {
            if (keyframes == null || keyframes.Length == 0) return;
            PrepareStart();

            if (_configuredDelaySeconds > 0)
                _keyframeQueue.Enqueue(new Keyframe<T>(_currentValue, _configuredDelaySeconds));

            foreach (var kf in keyframes)
                _keyframeQueue.Enqueue(kf);

            MoveToNextSegment(true);
        }

        public void Start(params T[] values)
        {
            if (values == null || values.Length == 0) return;

            if (values.Length == 1 && values[0].Equals(_currentValue) && _configuredDelaySeconds <= 0) return;

            PrepareStart();

            if (_configuredDelaySeconds > 0)
                _keyframeQueue.Enqueue(new Keyframe<T>(_currentValue, _configuredDelaySeconds));

            double autoStepDuration = _totalDurationForAutoSplit / values.Length;

            foreach (var val in values)
                _keyframeQueue.Enqueue(new Keyframe<T>(val, autoStepDuration));

            MoveToNextSegment(true);
        }

        private void PrepareStart()
        {
            _keyframeQueue.Clear();
            _isTransitioning = true;
        }

        private void MoveToNextSegment(bool firstStart = false)
        {
            if (_keyframeQueue.Count > 0)
            {
                var kf = _keyframeQueue.Dequeue();
                _startValue = firstStart ? _currentValue : _targetValue;
                _targetValue = kf.Value;
                _stepDuration = kf.Duration;

                if (firstStart) _progress = 0f;
            }
            else
            {
                _currentValue = _targetValue;
                _isTransitioning = false;
                _progress = 1f;
            }
        }

        public void Update(TimeSpan elapsedTime)
        {
            if (!_isTransitioning) return;

            double timeStep = elapsedTime.TotalSeconds;

            while (timeStep > 0 && _isTransitioning)
            {
                double progressDelta = (_stepDuration > 0.000001) ? (timeStep / _stepDuration) : 1.0;

                if (_progress + progressDelta >= 1.0)
                {
                    double timeConsumed = (1.0 - _progress) * _stepDuration;
                    timeStep -= timeConsumed;
                    _progress = 1.0;
                    _currentValue = _targetValue;
                    MoveToNextSegment();
                    if (_isTransitioning) _progress = 0f;
                }
                else
                {
                    _progress += progressDelta;
                    timeStep = 0;
                    _currentValue = _interpolator(_startValue, _targetValue, _progress);
                }
            }
        }
    }
}
