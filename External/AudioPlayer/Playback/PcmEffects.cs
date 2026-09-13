using BassPlayerIpc.Shared;

namespace AudioPlayer.Playback;

/// <summary>PCM 音效链：控制线程发布不可变目标，渲染线程独占平滑值和滤波历史。</summary>
internal sealed class PcmEffects : IDisposable
{
    internal event Action? StateChanged;

    private sealed record Target(DspSettings Settings, double Gain, ConvolutionFilter? Filter = null, double ConvolutionGain = 1);
    private ConvolutionFilter? _filter, _renderFilter;
    private string? _impulsePath;
    private string? _curveKey;
    private bool _autoHeadroom;
    private double _autoGainDb;
    private CancellationTokenSource? _prepareCancellation;
    private ConvolutionFilter? _oldFilter;
    private readonly double[] _dryConvolution = new double[1024], _oldConvolution = new double[1024];
    private double _oldMix, _oldGain;
    private ConvolutionStatus _convolutionStatus;
    private int _impulseVersion;
    private double _convolutionMix, _convolutionGain = 1;
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
            ConfigureConvolution();
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
        StateChanged?.Invoke();
    }

    private void ConfigureConvolution()
    {
        bool curve = CorrectionCurve.UsesCurve(_settings);
        string? curveKey = curve ? _settings.CurvePoints : null;
        string? path = curve ? null : _settings.ImpulsePath;
        bool autoHeadroom = curve && _settings.AutoConvolutionHeadroom;
        if (_impulsePath == path && _curveKey == curveKey && _autoHeadroom == autoHeadroom) return;
        _impulsePath = path; _curveKey = curveKey; _autoHeadroom = autoHeadroom;
        _prepareCancellation?.Cancel();
        _prepareCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _prepareCancellation = cancellation;
        var token = cancellation.Token;
        int version = ++_impulseVersion;
        _convolutionStatus = ConvolutionStatus.Off;
        if (!curve && string.IsNullOrEmpty(path)) { _filter = null; _autoGainDb = 0; return; }
        if (_channels is < 1 or > 2) { _filter = null; _convolutionStatus = ConvolutionStatus.Unsupported; return; }
        _convolutionStatus = ConvolutionStatus.Loading;
        var settings = _settings;
        _ = Task.Run(() =>
        {
            ConvolutionFilter? filter = null;
            double autoGain = 0;
            try
            {
                var impulse = CorrectionCurve.Prepare(settings, _rate, token);
                if (autoHeadroom) autoGain = ResponseMath.AutoAttenuationDb(impulse);
                token.ThrowIfCancellationRequested();
                filter = new ConvolutionFilter(impulse, _rate, _channels);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { Console.WriteLine($"[convolution] IR load failed: {ex.Message}"); }
            lock (_control)
            {
                if (_disposed || version != _impulseVersion) return;
                _filter = filter;
                _autoGainDb = autoGain;
                _convolutionStatus = filter != null ? ConvolutionStatus.Active : ConvolutionStatus.Failed;
                Publish();
            }
            StateChanged?.Invoke();
        });
    }

    private async Task AnalyzeAsync(CancellationTokenSource scan)
    {
        LoudnessMeasurement? result = null;
        bool failed = false, changed = false;
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
                changed = true;
            }
        }
        scan.Dispose();
        if (changed) StateChanged?.Invoke();
    }

    private void Publish()
    {
        double normalization = NormalizationGainDb;
        if (_settings.IsEnabled && _settings.NormalizeLoudness && _measurement != null)
        {
            normalization = _measurement.GainDb(_settings.TargetLufs);
            _status = normalization < _settings.TargetLufs - _measurement.IntegratedLufs - 0.01
                ? LoudnessStatus.PeakLimited : LoudnessStatus.Applied;
        }
        Volatile.Write(ref _target, new Target(_settings, _settings.IsEnabled
            ? Math.Pow(10, (normalization + _settings.HeadroomDb) / 20) : 1,
            _settings.ConvolutionEnabled ? _filter : null, Math.Pow(10, (_settings.ConvolutionTrimDb + _autoGainDb) / 20)));
    }

    /// <summary>未知响度采用固定保守衰减；失败时保持衰减，避免突然回到原始音量。</summary>
    private double NormalizationGainDb => !_settings.IsEnabled || !_settings.NormalizeLoudness ? 0
        : _measurement?.GainDb(_settings.TargetLufs) ?? Math.Min(-12, _settings.TargetLufs + 6);

    internal DspState GetState(byte kind, bool eq)
    {
        lock (_control)
            return new(kind, eq, _channels, _status,
                NormalizationGainDb,
                _measurement?.IntegratedLufs ?? double.NaN, _settings.IsEnabled,
                !_settings.IsEnabled || !_settings.ConvolutionEnabled ? ConvolutionStatus.Off : _convolutionStatus, _rate);
    }

    /// <summary>seek 仅投递代数，滤波历史由渲染线程在下一块清空。</summary>
    internal void RequestReset() => Interlocked.Increment(ref _resetVersion);

    /// <summary>仅由渲染线程在重新启用 DSP 时调用，丢弃旁路前的平滑值和尾音。</summary>
    internal void ResetRenderState()
    {
        _renderTarget = null;
        _renderFilter?.Reset();
        _oldFilter = null;
        _convolutionMix = 0;
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
            // 降低增益在 50ms 内完成，抬升用 1s，避免分析完成时突然变响。
            // .NET 11 渲染路径只更新标量，不分配、不锁定、不跟踪短时响度。
            _gainFrames = Math.Max(1, target.Gain < _gain ? _rate / 20 : _rate);
            _gainStep = (target.Gain - _gain) / _gainFrames;
        }
        int reset = Volatile.Read(ref _resetVersion);
        if (reset != _renderResetVersion)
        {
            _lowLeft = _lowRight = 0;
            _renderFilter?.Reset();
            _oldFilter = null;
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

    internal void ApplyConvolution(Span<double> samples, int frames)
    {
        var filter = _renderTarget?.Filter;
        if (!ReferenceEquals(filter, _renderFilter))
        {
            _oldFilter = _renderFilter;
            _oldMix = _convolutionMix;
            _oldGain = _convolutionGain;
            _renderFilter = filter;
            filter?.Reset();
            _convolutionMix = 0;
            _convolutionGain = _renderTarget?.ConvolutionGain ?? 1;
        }
        if (filter == null && _oldFilter == null) return;
        double gain = _renderTarget?.ConvolutionGain ?? 1;
        for (int offset = 0; offset < frames; offset += 512)
        {
            int count = Math.Min(512, frames - offset);
            var block = samples.Slice(offset * _channels, count * _channels);
            if (_oldFilter != null)
            {
                block.CopyTo(_dryConvolution);
                block.CopyTo(_oldConvolution);
            }
            filter?.Process(block, count, gain, ref _convolutionMix, ref _convolutionGain, _parameterStep);
            if (_oldFilter != null)
            {
                _oldFilter.Process(_oldConvolution, count, _oldGain, ref _oldMix, ref _oldGain, _parameterStep, false);
                for (int i = 0; i < block.Length; i++) block[i] += _oldConvolution[i] - _dryConvolution[i];
                if (_oldMix == 0) _oldFilter = null;
            }
        }
    }

    private double Move(double current, double target) => current < target
        ? Math.Min(target, current + _parameterStep) : Math.Max(target, current - _parameterStep);

    public void Dispose()
    {
        lock (_control)
        {
            _disposed = true;
            _prepareCancellation?.Cancel();
            _prepareCancellation?.Dispose();
            _prepareCancellation = null;
            _scan?.Cancel();
            _scan = null;
        }
    }
}
