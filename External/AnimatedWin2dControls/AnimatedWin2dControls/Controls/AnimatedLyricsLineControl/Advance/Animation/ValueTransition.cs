using System;

namespace AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.Advance
{
    public class ValueTransition<T> where T : struct
    {
        private T _currentValue;
        private T _startValue;
        private T _targetValue;

        private Keyframe<T> _k1;
        private Keyframe<T> _k2;
        private Keyframe<T> _kDelay;
        private bool _hasK2;
        private bool _hasDelay;

        private Keyframe<T>[]? _extraKeyframes;
        private int _extraCount;

        private int _segmentCount;
        private int _currentSegment;

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
            _segmentCount = 0;
            _currentSegment = 0;
            _hasK2 = false;
            _hasDelay = false;
            _extraCount = 0;
            _currentValue = value;
            _startValue = value;
            _targetValue = value;
            _isTransitioning = false;
            _progress = 0;
        }

        public void Start(T value)
        {
            if (value.Equals(_currentValue) && _configuredDelaySeconds <= 0) return;
            ResetSegments();
            if (_configuredDelaySeconds > 0)
            {
                _kDelay = new Keyframe<T>(_currentValue, _configuredDelaySeconds);
                _hasDelay = true;
            }
            _k1 = new Keyframe<T>(value, _totalDurationForAutoSplit);
            _hasK2 = false;
            _segmentCount = _hasDelay ? 2 : 1;
            BeginFirstSegment();
        }

        public void Start(T v1, T v2)
        {
            ResetSegments();
            if (_configuredDelaySeconds > 0)
            {
                _kDelay = new Keyframe<T>(_currentValue, _configuredDelaySeconds);
                _hasDelay = true;
            }
            double autoStepDuration = _totalDurationForAutoSplit / 2;
            _k1 = new Keyframe<T>(v1, autoStepDuration);
            _k2 = new Keyframe<T>(v2, autoStepDuration);
            _hasK2 = true;
            _segmentCount = _hasDelay ? 3 : 2;
            BeginFirstSegment();
        }

        public void Start(params T[] values)
        {
            if (values == null || values.Length == 0) return;
            if (values.Length == 1) { Start(values[0]); return; }
            if (values.Length == 2) { Start(values[0], values[1]); return; }

            ResetSegments();
            if (_configuredDelaySeconds > 0)
            {
                _kDelay = new Keyframe<T>(_currentValue, _configuredDelaySeconds);
                _hasDelay = true;
            }
            double autoStepDuration = _totalDurationForAutoSplit / values.Length;
            EnsureExtraCapacity(values.Length);
            for (int i = 0; i < values.Length; i++)
                _extraKeyframes![i] = new Keyframe<T>(values[i], autoStepDuration);
            _extraCount = values.Length;
            _segmentCount = _hasDelay ? values.Length + 1 : values.Length;
            BeginFirstSegment();
        }

        public void Start(Keyframe<T> kf)
        {
            ResetSegments();
            if (_configuredDelaySeconds > 0)
            {
                _kDelay = new Keyframe<T>(_currentValue, _configuredDelaySeconds);
                _hasDelay = true;
            }
            _k1 = kf;
            _hasK2 = false;
            _segmentCount = _hasDelay ? 2 : 1;
            BeginFirstSegment();
        }

        public void Start(Keyframe<T> kf1, Keyframe<T> kf2)
        {
            ResetSegments();
            if (_configuredDelaySeconds > 0)
            {
                _kDelay = new Keyframe<T>(_currentValue, _configuredDelaySeconds);
                _hasDelay = true;
            }
            _k1 = kf1;
            _k2 = kf2;
            _hasK2 = true;
            _segmentCount = _hasDelay ? 3 : 2;
            BeginFirstSegment();
        }

        public void Start(params Keyframe<T>[] keyframes)
        {
            if (keyframes == null || keyframes.Length == 0) return;
            if (keyframes.Length == 1) { Start(keyframes[0]); return; }
            if (keyframes.Length == 2) { Start(keyframes[0], keyframes[1]); return; }

            ResetSegments();
            if (_configuredDelaySeconds > 0)
            {
                _kDelay = new Keyframe<T>(_currentValue, _configuredDelaySeconds);
                _hasDelay = true;
            }
            EnsureExtraCapacity(keyframes.Length);
            for (int i = 0; i < keyframes.Length; i++)
                _extraKeyframes![i] = keyframes[i];
            _extraCount = keyframes.Length;
            _segmentCount = _hasDelay ? keyframes.Length + 1 : keyframes.Length;
            BeginFirstSegment();
        }

        private void ResetSegments()
        {
            _isTransitioning = true;
            _currentSegment = 0;
            _hasK2 = false;
            _hasDelay = false;
            _extraCount = 0;
        }

        private void EnsureExtraCapacity(int n)
        {
            if (_extraKeyframes == null || _extraKeyframes.Length < n)
                _extraKeyframes = new Keyframe<T>[Math.Max(n, 4)];
        }

        private void BeginFirstSegment()
        {
            if (_segmentCount == 0) { _isTransitioning = false; return; }
            AdvanceToSegment(0);
            _progress = 0f;
        }

        private ref readonly Keyframe<T> CurrentKeyframe()
        {
            if (_hasDelay && _currentSegment == _segmentCount - 1)
                return ref _kDelay;
            if (!_hasK2 || _currentSegment == 0)
                return ref _k1;
            if (_currentSegment == 1)
                return ref _k2;
            return ref _extraKeyframes![_currentSegment - 2];
        }

        private void AdvanceToSegment(int seg)
        {
            _currentSegment = seg;
            var kf = CurrentKeyframe();
            _startValue = _currentValue;
            _targetValue = kf.Value;
            _stepDuration = kf.Duration;
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
                    if (_currentSegment + 1 < _segmentCount)
                    {
                        AdvanceToSegment(_currentSegment + 1);
                        _progress = 0f;
                    }
                    else
                    {
                        _isTransitioning = false;
                        _progress = 1f;
                    }
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
