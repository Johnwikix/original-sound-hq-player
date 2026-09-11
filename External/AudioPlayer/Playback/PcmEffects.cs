using BassPlayerIpc.Shared;

namespace AudioPlayer.Playback;

/// <summary>PCM 音效链：控制线程发布不可变目标，渲染线程独占平滑值和滤波历史。</summary>
internal sealed class PcmEffects : IDisposable
{
    private sealed record Target(DspSettings Settings, double Gain);
    private readonly object _control = new();
    private readonly int _rate, _channels;
    private readonly double _lowPassAlpha, _parameterStep;
    private Target _target = new(new(), 1);
    private Target? _renderTarget;
    private DspSettings _settings = new();
    private LoudnessMeasurement? _measurement;
    private LoudnessStatus _status;
    private CancellationTokenSource? _scan;
    private string? _path;
    private int _dsdRate, _dsdGain;
    private bool _disposed, _attempted;
    private int _resetVersion, _renderResetVersion;
    private double _gain = 1, _gainStep;
    private int _gainFrames;
    private double _balance, _width = 1, _crossfeed, _swap, _mono;
    private double _lowLeft, _lowRight;

    internal PcmEffects(int rate, int channels)
    {
        _rate = rate; _channels = channels;
        _lowPassAlpha = 1 - Math.Exp(-2 * Math.PI * 700 / rate);
        _parameterStep = 1.0 / Math.Max(1, rate / 50);
    }

    internal void SetFile(string path, int dsdRate, int dsdGain)
    {
        _path = path; _dsdRate = dsdRate; _dsdGain = dsdGain;
    }

    internal void Configure(DspSettings settings)
    {
        lock (_control)
        {
            if (_disposed) return;
            _settings = settings;
            if (!settings.IsEnabled || !settings.NormalizeLoudness)
            {
                _scan?.Cancel();
                _scan = null;
                _attempted = false;
                _status = LoudnessStatus.Off;
            }
            else if (_measurement != null) _status = LoudnessStatus.Applied;
            else if (_channels is < 1 or > 2 || _rate < 8000 || string.IsNullOrEmpty(_path))
                _status = LoudnessStatus.Unavailable;
            else if (!_attempted)
            {
                _attempted = true;
                _status = LoudnessStatus.Analyzing;
                var scan = new CancellationTokenSource();
                _scan = scan;
                _ = AnalyzeAsync(scan);
            }
            Publish();
        }
    }

    private async Task AnalyzeAsync(CancellationTokenSource scan)
    {
        LoudnessMeasurement? result = null;
        bool failed = false;
        try
        {
            result = await LoudnessScanner.ScanAsync(_path!, _rate, _channels, _dsdRate, _dsdGain, scan.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { failed = true; Console.WriteLine($"[loudness] analysis failed: {ex.Message}"); }
        lock (_control)
        {
            if (!_disposed && ReferenceEquals(_scan, scan) && !scan.IsCancellationRequested)
            {
                _measurement = result;
                _status = result != null ? LoudnessStatus.Applied : failed ? LoudnessStatus.Failed : LoudnessStatus.Unavailable;
                _scan = null;
                Publish();
            }
        }
        scan.Dispose();
    }

    private void Publish()
    {
        double normalization = 0;
        if (_settings.IsEnabled && _settings.NormalizeLoudness && _measurement != null)
        {
            normalization = _measurement.GainDb(_settings.TargetLufs);
            _status = normalization < _settings.TargetLufs - _measurement.IntegratedLufs - 0.01
                ? LoudnessStatus.PeakLimited : LoudnessStatus.Applied;
        }
        Volatile.Write(ref _target, new Target(_settings, _settings.IsEnabled
            ? Math.Pow(10, (normalization + _settings.HeadroomDb) / 20) : 1));
    }

    internal DspState GetState(byte kind, bool eq)
    {
        lock (_control)
            return new(kind, eq, _channels, _status,
                _settings.IsEnabled && _settings.NormalizeLoudness && _measurement != null ? _measurement.GainDb(_settings.TargetLufs) : 0,
                _measurement?.IntegratedLufs ?? double.NaN, _settings.IsEnabled);
    }

    /// <summary>seek 仅投递代数，滤波历史由渲染线程在下一块清空。</summary>
    internal void RequestReset() => Interlocked.Increment(ref _resetVersion);

    /// <summary>仅由渲染线程在重新启用 DSP 时调用，丢弃旁路前的平滑值和尾音。</summary>
    internal void ResetRenderState()
    {
        _renderTarget = null;
        _gainFrames = 0;
        _gain = 1;
        _gainStep = _lowLeft = _lowRight = 0;
    }

    /// <summary>先施加固定响度增益和前置衰减；.NET 11 Span 热路径无锁、无分配。</summary>
    internal void ApplyInput(Span<double> samples, int frames)
    {
        var target = Volatile.Read(ref _target);
        if (!ReferenceEquals(target, _renderTarget))
        {
            if (_renderTarget == null)
            {
                _gain = target.Gain;
                _balance = target.Settings.Balance; _width = target.Settings.StereoWidth;
                _crossfeed = target.Settings.Crossfeed; _mono = target.Settings.Mono ? 1 : 0;
                _swap = target.Settings.SwapChannels ? 1 : 0;
            }
            _renderTarget = target;
            _gainFrames = Math.Max(1, _rate); // 整曲分析完成或设置改变时仅过渡一次，绝不跟踪短时响度。
            _gainStep = (target.Gain - _gain) / _gainFrames;
        }
        int reset = Volatile.Read(ref _resetVersion);
        if (reset != _renderResetVersion)
        {
            _lowLeft = _lowRight = 0;
            _renderResetVersion = reset;
        }
        if (_gain == target.Gain)
        {
            _gainFrames = 0;
            if (_gain == 1) return;
        }
        for (int frame = 0; frame < frames; frame++)
        {
            if (_gainFrames > 0)
            {
                _gain += _gainStep;
                if (--_gainFrames == 0) _gain = target.Gain;
            }
            for (int ch = 0; ch < _channels; ch++) samples[frame * _channels + ch] *= _gain;
        }
    }

    /// <summary>处理双声道空间设置；其他声道布局明确直通。</summary>
    internal void ApplyStereo(Span<double> samples, int frames)
    {
        if (_channels != 2 || _renderTarget == null) return;
        DspSettings settings = _renderTarget.Settings;
        if (settings.Balance == 0 && settings.StereoWidth == 1 && settings.Crossfeed == 0
            && !settings.Mono && !settings.SwapChannels && _balance == 0 && _width == 1
            && _crossfeed == 0 && _mono == 0 && _swap == 0)
        {
            _lowLeft = _lowRight = 0;
            return;
        }
        for (int frame = 0; frame < frames; frame++)
        {
            _balance = Move(_balance, settings.Balance);
            _width = Move(_width, settings.StereoWidth);
            _crossfeed = Move(_crossfeed, settings.Crossfeed);
            _swap = Move(_swap, settings.SwapChannels ? 1 : 0);
            _mono = Move(_mono, settings.Mono ? 1 : 0);
            int offset = frame * 2;
            double left = samples[offset], right = samples[offset + 1];
            double swappedLeft = left + (right - left) * _swap;
            right += (left - right) * _swap;
            left = swappedLeft;
            double middle = (left + right) * 0.5;
            double side = (left - right) * 0.5 * _width * (1 - _mono);
            // 扩宽时预留峰值余量；原始宽度下增益精确为 1。
            double widthScale = 1 / Math.Max(1, _width);
            left = (middle + side) * widthScale;
            right = (middle - side) * widthScale;
            _lowLeft += _lowPassAlpha * (left - _lowLeft);
            _lowRight += _lowPassAlpha * (right - _lowRight);
            double crossfeedScale = 1 / (1 + _crossfeed);
            double outputLeft = (left + _crossfeed * _lowRight) * crossfeedScale;
            double outputRight = (right + _crossfeed * _lowLeft) * crossfeedScale;
            samples[offset] = outputLeft * (1 - Math.Max(0, _balance));
            samples[offset + 1] = outputRight * (1 + Math.Min(0, _balance));
        }
    }

    private double Move(double current, double target) => current < target
        ? Math.Min(target, current + _parameterStep) : Math.Max(target, current - _parameterStep);

    public void Dispose()
    {
        lock (_control)
        {
            _disposed = true;
            _scan?.Cancel();
            _scan = null;
        }
    }
}
