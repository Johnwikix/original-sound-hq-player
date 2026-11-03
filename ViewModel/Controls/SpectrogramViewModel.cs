using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using System;
using System.ComponentModel;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.AudioAnalysis;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.ViewModel;

/// <summary>拥有详情歌曲的声谱图任务和查看状态；播放位置直接读取共享 AppViewModel。</summary>
public sealed class SpectrogramViewModel : ObservableObject
{
    private readonly Music _music;
    private readonly AppViewModel _app;
    private readonly ISpectrogramService _service;
    private readonly ILogger _logger;
    private readonly IRelayCommand<long>? _seek;
    private CancellationTokenSource? _request;
    private Task _work = Task.CompletedTask;
    private bool _active, _closed;
    private int _generation;
    public SpectrogramData? Data { get; private set { if (SetProperty(ref field, value)) OnPropertyChanged(nameof(TechnicalInfo)); } }
    public bool IsBusy { get; private set => SetProperty(ref field, value); }
    public double Progress { get; private set => SetProperty(ref field, value); }
    public string Status { get; private set => SetProperty(ref field, value); } = "";
    public string Inspection { get; private set => SetProperty(ref field, value); } = "";
    public string TechnicalInfo => Data is { } data ? $"{data.SampleRate:N0} Hz · {data.Channels} ch · FFT {data.FftSize}"
        + (data.Channels > 2 ? " · " + ToolUtils.GetString("SpectrumDownmix") : "") : "";
    public int Channel { get; set { value = Math.Clamp(value, 0, 2); if (SetProperty(ref field, value)) Inspection = ""; } }
    public int ResolutionIndex
    {
        get;
        set { value = Math.Clamp(value, 0, 2); if (SetProperty(ref field, value) && _active) Restart(); }
    }
    public double MinimumDb
    {
        get;
        set
        {
            if (double.IsFinite(value) && SetProperty(ref field, Math.Clamp(value, -160, MaximumDb - 10)))
                OnPropertyChanged(nameof(MaximumDbFloor));
        }
    } = -120;
    public double MaximumDb
    {
        get;
        set
        {
            if (double.IsFinite(value) && SetProperty(ref field, Math.Clamp(value, MinimumDb + 10, 0)))
                OnPropertyChanged(nameof(MinimumDbCeiling));
        }
    }
    public double MinimumDbCeiling => MaximumDb - 10;
    public double MaximumDbFloor => MinimumDb + 10;
    public double Zoom { get; private set => SetProperty(ref field, value); } = 1;
    public double ViewStart { get; private set => SetProperty(ref field, value); }
    public double VerticalZoom { get; private set => SetProperty(ref field, value); } = 1;
    public double FrequencyViewStart { get; private set => SetProperty(ref field, value); }
    public bool IsTransparencyEnabled
    {
        get;
        set
        {
            if (SetProperty(ref field, value)) OnPropertyChanged(nameof(IsOpaque));
        }
    } = true;
    public bool IsOpaque { get => !IsTransparencyEnabled; set => IsTransparencyEnabled = !value; }
    public bool HasPlaybackCursor => SameMusic(_app.CurrentPlayingMusic);
    public bool IsPlaying => HasPlaybackCursor && _app.IsPlaying;
    public double PositionSeconds => HasPlaybackCursor ? _app.CurrentPlayingTime.TotalSeconds : 0;
    public IRelayCommand RetryCommand { get; }
    public IRelayCommand ResetViewCommand { get; }

    public SpectrogramViewModel(Music music, AppViewModel app, ISpectrogramService service, ILogger logger, IRelayCommand<long>? seek = null)
    {
        _music = music;
        _app = app;
        _service = service;
        _logger = logger;
        _seek = seek;
        RetryCommand = new RelayCommand(Restart, () => _active && !_closed && !IsBusy);
        ResetViewCommand = new RelayCommand(() =>
        {
            Zoom = 1;
            ViewStart = 0;
            VerticalZoom = 1;
            FrequencyViewStart = 0;
            Inspection = "";
        });
    }

    public void SetActive(bool active)
    {
        if (_closed || _active == active) return;
        _active = active;
        if (active)
        {
            _app.PropertyChanged += PlaybackChanged;
            Restart();
            NotifyPlayback();
        }
        else
        {
            _app.PropertyChanged -= PlaybackChanged;
            _generation++;
            _request?.Cancel();
            IsBusy = false;
            RetryCommand.NotifyCanExecuteChanged();
        }
    }

    private void Restart()
    {
        if (!_active || _closed) return;
        _request?.Cancel();
        var request = new CancellationTokenSource();
        _request = request;
        int generation = ++_generation;
        IsBusy = true;
        Status = ToolUtils.GetString("SpectrumAnalyzing");
        Progress = 0;
        RetryCommand.NotifyCanExecuteChanged();
        var input = new SpectrogramInput(_music.Id, _music.SourceId, _music.Path);
        _work = LoadAfterAsync(_work, request, generation, input, 2048 << ResolutionIndex);
    }

    private async Task LoadAfterAsync(Task previous, CancellationTokenSource request, int generation, SpectrogramInput input, int fftSize)
    {
        try
        {
            await previous;
            request.Token.ThrowIfCancellationRequested();
            var progress = new Progress<double>(value => { if (Current(generation)) Progress = value; });
            var data = await _service.AnalyzeAsync(input, fftSize, progress, request.Token);
            if (!Current(generation)) return;
            Data = data;
            Inspection = "";
            Status = data is null ? ToolUtils.GetString("SpectrumNeedsCache") : "";
            Progress = 100;
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "分析歌曲声谱图失败");
            if (Current(generation))
            {
                Data = null;
                Status = ToolUtils.GetString("SpectrumFailed");
            }
        }
        finally
        {
            if (ReferenceEquals(_request, request)) _request = null;
            request.Dispose();
            if (Current(generation))
            {
                IsBusy = false;
                RetryCommand.NotifyCanExecuteChanged();
            }
        }
    }
    private bool Current(int generation) => !_closed && _active && generation == _generation;
    private bool SameMusic(Music? music) => music is not null && (ReferenceEquals(music, _music)
        || music.SourceId == _music.SourceId && (music.Id != 0 && music.Id == _music.Id
            || music.Id == 0 && _music.Id == 0 && string.Equals(music.Path, _music.Path, StringComparison.OrdinalIgnoreCase)));
    private void PlaybackChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(AppViewModel.CurrentPlayingMusic)) NotifyPlayback();
        else if (args.PropertyName == nameof(AppViewModel.CurrentPlayingTime)) OnPropertyChanged(nameof(PositionSeconds));
        else if (args.PropertyName == nameof(AppViewModel.IsPlaying))
        {
            OnPropertyChanged(nameof(IsPlaying));
            OnPropertyChanged(nameof(PositionSeconds));
        }
    }
    private void NotifyPlayback()
    {
        OnPropertyChanged(nameof(HasPlaybackCursor));
        OnPropertyChanged(nameof(IsPlaying));
        OnPropertyChanged(nameof(PositionSeconds));
    }
    public void ZoomAt(double anchor, double factor)
    {
        anchor = Math.Clamp(anchor, 0, 1);
        double time = ViewStart + anchor / Zoom;
        Zoom = Math.Clamp(Zoom * factor, 1, 8);
        ViewStart = Math.Clamp(time - anchor / Zoom, 0, 1 - 1 / Zoom);
    }
    public void ZoomFrequencyAt(double anchor, double factor)
    {
        anchor = Math.Clamp(anchor, 0, 1);
        double frequency = FrequencyViewStart + (1 - anchor) / VerticalZoom;
        VerticalZoom = Math.Clamp(VerticalZoom * factor, 1, 8);
        FrequencyViewStart = Math.Clamp(frequency - (1 - anchor) / VerticalZoom, 0, 1 - 1 / VerticalZoom);
    }
    public void Pan(double fraction) => ViewStart = Math.Clamp(ViewStart + fraction / Zoom, 0, 1 - 1 / Zoom);
    public void PanFrequency(double fraction) => FrequencyViewStart = Math.Clamp(FrequencyViewStart + fraction / VerticalZoom, 0, 1 - 1 / VerticalZoom);
    public void Inspect(double x, double y)
    {
        if (Data is not { } data) return;
        double time = Math.Clamp(ViewStart + x / Zoom, 0, 1);
        double frequency = Math.Clamp(FrequencyViewStart + (1 - y) / VerticalZoom, 0, 1);
        int column = Math.Clamp((int)(time * data.Width), 0, data.Width - 1);
        int bin = Math.Clamp((int)(frequency * (data.Height - 1)), 0, data.Height - 1);
        double seconds = time * data.Duration;
        Inspection = string.Create(CultureInfo.CurrentCulture, $"{(int)seconds / 60:00}:{seconds % 60:00.0} · {bin * data.SampleRate / (double)data.FftSize:N0} Hz · {data.Values(Channel)[column * data.Height + bin] - 160} dBFS");
    }
    public void ClearInspection() => Inspection = "";
    public void Seek(double fraction)
    {
        if (Data is not { } data || !HasPlaybackCursor || _seek is null) return;
        long milliseconds = (long)(Math.Clamp(ViewStart + fraction / Zoom, 0, 1) * data.Duration * 1000);
        if (_seek.CanExecute(milliseconds)) _seek.Execute(milliseconds);
    }
    public void BeginClose()
    {
        if (_closed) return;
        SetActive(false);
        _closed = true;
        Data = null;
        Inspection = "";
    }
    public async Task StopAsync()
    {
        BeginClose();
        await _work;
    }
}
