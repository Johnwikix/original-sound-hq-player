using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using WinUIMusicPlayer.Model.Stats;
using WinUIMusicPlayer.Services;
using static WinUIMusicPlayer.Utils.ToolUtils;

namespace WinUIMusicPlayer.ViewModel;

public sealed partial class ListeningReportViewModel : ObservableObject
{
    private readonly ListeningReportQuery _query;
    private readonly ILogger<ListeningReportViewModel> _logger;
    private ListeningPeriod _period;
    private ListeningPeriod _annual;
    private ListeningPeriod _monthly;
    private DateOnly _today;
    private DateOnly? _firstRecord;
    private DateOnly? _latestRecord;
    private TimeZoneInfo _zone = TimeZoneInfo.Utc;
    private bool _active;
    private bool _stopping;
    private bool _settingPeriod;
    private int _requestVersion;
    private Task _loadTask = Task.CompletedTask;
    private CancellationTokenSource? _loadCancellation;
    private ListeningReport? _snapshot;
    private Func<CancellationToken, Task<SoftwareBitmap>>? _capture;
    private Func<string, CancellationToken, Task<string?>>? _pickPath;
    private ImageSource? _cover;

    internal ListeningReportViewModel(ListeningReportQuery query, ILogger<ListeningReportViewModel> logger, ShutdownCoordinator shutdown)
    {
        _query = query;
        _logger = logger;
        shutdown.RegisterStop(StopAsync);
    }

    public int ModeIndex
    {
        get => _period.IsMonthly ? 1 : 0;
        set
        {
            if (!_settingPeriod && _active && !IsPreview && value is (0 or 1) && value != ModeIndex)
                Select(value == 0 ? _annual : _monthly);
        }
    }

    public double SelectedYear
    {
        get => _period.Year;
        set
        {
            if (!_settingPeriod && _active && !IsPreview && value >= MinimumYear && value <= MaximumYear)
                Select(new((int)value, _period.Month));
        }
    }

    public double SelectedMonth
    {
        get => _period.Month;
        set
        {
            if (!_settingPeriod && _active && !IsPreview && _period.IsMonthly && value >= MinimumMonth && value <= MaximumMonth)
                Select(new(_period.Year, (int)value));
        }
    }

    public double MinimumYear => _firstRecord is { } first && first <= _today ? first.Year : _today.Year;
    public double MaximumYear => _today.Year;
    public double MinimumMonth => _firstRecord is { } first && first <= _today && first.Year == _period.Year ? first.Month : 1;
    public double MaximumMonth => _period.Year == _today.Year ? _today.Month : 12;
    public Visibility MonthVisibility => Visible(_period.IsMonthly);
    public Visibility ReportVisibility => Visible(Presentation is not null);
    public Visibility StatusVisibility => Visible(StatusText.Length > 0);
    public Visibility RecentVisibility => Visible(CanOpenRecent());
    public Visibility LoadingVisibility => Visible(IsLoading);
    public string BackLabel => GetString("ListeningReportBackLabel");
    public Visibility BodyVisibility => Visible(!IsPreview);
    public bool CanNavigateBack => !IsPreview || ClosePreviewCommand.CanExecute(null);
    internal bool IsCapturing { get; private set; }

    [ObservableProperty]
    public partial ListeningReportPresentation? Presentation { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LoadingVisibility))]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    public partial bool IsPreview { get; private set; }

    [ObservableProperty]
    public partial bool IsSaving { get; private set; }

    [ObservableProperty]
    public partial bool IsPreparingPreview { get; private set; }

    [ObservableProperty]
    public partial string StatusText { get; private set; } = "";

    [ObservableProperty]
    public partial string SaveStatus { get; private set; } = "";

    public ImageSource? PreviewCover
    {
        get => _cover;
        private set
        {
            var previous = _cover;
            if (SetProperty(ref _cover, value)) (previous as IDisposable)?.Dispose();
        }
    }

    internal void Activate(Func<CancellationToken, Task<SoftwareBitmap>> capture,
        Func<string, CancellationToken, Task<string?>> pickPath)
    {
        if (_active || _stopping) return;
        _active = true;
        _capture = capture;
        _pickPath = pickPath;
        UpdateClock();
        _annual = new(_today.Year);
        _monthly = new(_today.Year, _today.Month);
        ApplySelection(_annual);
        RequestLoad();
    }

    internal void Deactivate()
    {
        if (!_active) return;
        _active = false;
        _requestVersion++;
        _loadCancellation?.Cancel();
        SaveCommand.Cancel();
        ClearPreview();
        _snapshot = null;
        Presentation = null;
        OnPropertyChanged(nameof(ReportVisibility));
        _capture = null;
        _pickPath = null;
        NotifyCommands();
    }

    internal async Task StopAsync()
    {
        _stopping = true;
        _loadCancellation?.Cancel();
        OpenPreviewCommand.Cancel();
        SaveCommand.Cancel();
        try
        {
            // 原生捕获不可强制取消。在渲染返回前让海报留在可视树内。
            if (SaveCommand.ExecutionTask is { } save) await save;
        }
        finally
        {
            Deactivate();
            await _loadTask;
            if (OpenPreviewCommand.ExecutionTask is { } preview) await preview;
        }
    }

    private void UpdateClock()
    {
        // Windows 变更时区后重新获取规则，仅在打开报告或手动刷新时丢弃运行时缓存。
        TimeZoneInfo.ClearCachedData();
        _zone = TimeZoneInfo.Local;
        _today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _zone));
    }

    private void Select(ListeningPeriod period)
    {
        period = ClampPeriod(period);
        if (period == _period) return;
        ApplySelection(period);
        RequestLoad();
    }

    private ListeningPeriod ClampPeriod(ListeningPeriod period)
    {
        int year = Math.Clamp(period.Year, (int)MinimumYear, (int)MaximumYear);
        if (!period.IsMonthly) return new(year);
        int minimum = _firstRecord is { } first && first <= _today && year == first.Year ? first.Month : 1;
        int maximum = year == _today.Year ? _today.Month : 12;
        return new(year, Math.Clamp(period.Month, minimum, maximum));
    }

    private void ApplySelection(ListeningPeriod period)
    {
        _period = period;
        if (period.IsMonthly) _monthly = period;
        else _annual = period;

        _settingPeriod = true;
        try
        {
            OnPropertyChanged(nameof(MinimumYear));
            OnPropertyChanged(nameof(MaximumYear));
            OnPropertyChanged(nameof(MinimumMonth));
            OnPropertyChanged(nameof(MaximumMonth));
            OnPropertyChanged(nameof(SelectedYear));
            OnPropertyChanged(nameof(SelectedMonth));
            OnPropertyChanged(nameof(ModeIndex));
            OnPropertyChanged(nameof(MonthVisibility));
        }
        finally { _settingPeriod = false; }
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private void Refresh()
    {
        UpdateClock();
        ApplySelection(ClampPeriod(_period));
        RequestLoad();
    }

    private bool CanRefresh() => _active && !_stopping && !IsPreview;

    [RelayCommand(CanExecute = nameof(CanOpenRecent))]
    private void OpenRecent()
    {
        if (_latestRecord is { } latest)
            Select(new(latest.Year, _period.IsMonthly ? latest.Month : 0));
    }

    private bool CanOpenRecent() => _active && !IsPreview && !IsLoading && _snapshot is { PlayCount: 0 } && _latestRecord.HasValue;

    private void RequestLoad()
    {
        _requestVersion++;
        _loadCancellation?.Cancel();
        if (_active && !_stopping && _loadTask.IsCompleted) _loadTask = LoadAsync();
    }

    private async Task LoadAsync()
    {
        IsLoading = true;
        _snapshot = null;
        Presentation = null;
        StatusText = "";
        OnPropertyChanged(nameof(ReportVisibility));
        OnPropertyChanged(nameof(StatusVisibility));
        OnPropertyChanged(nameof(RecentVisibility));
        NotifyCommands();

        try
        {
            while (_active && !_stopping)
            {
                int version = _requestVersion;
                using var cancellation = new CancellationTokenSource();
                _loadCancellation = cancellation;
                try
                {
                    var report = await _query.ReadAsync(new(_period, DateTime.UtcNow, _zone), cancellation.Token);
                    if (!_active || _stopping || version != _requestVersion) continue;
                    _snapshot = report;
                    _firstRecord = report.FirstRecord;
                    _latestRecord = report.LatestRecord;
                    ApplySelection(_period);
                    if (report.PlayCount > 0)
                        Presentation = new(report, CultureInfo.CurrentUICulture);
                    else
                        StatusText = GetString("ListeningReportEmpty");
                    break;
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    if (!_active || _stopping || version != _requestVersion) continue;
                    _logger.LogError(ex, "读取听歌报告失败");
                    StatusText = GetString("ListeningReportLoadFailed");
                    break;
                }
                finally { _loadCancellation = null; }
            }
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(ReportVisibility));
            OnPropertyChanged(nameof(StatusVisibility));
            OnPropertyChanged(nameof(RecentVisibility));
            NotifyCommands();
        }
    }

    private bool CanOpenPreview() => _active && !_stopping && !IsLoading && !IsPreview && !IsSaving && _snapshot is { PlayCount: > 0 };

    [RelayCommand(CanExecute = nameof(CanOpenPreview))]
    private async Task OpenPreviewAsync(CancellationToken token)
    {
        if (!CanOpenPreview()) return;
        string? hash = _snapshot!.Songs.Length > 0 ? _snapshot.Songs[0].CoverHash : null;
        IsPreparingPreview = true;
        SaveStatus = "";
        IsPreview = true;
        NotifyPreview();
        SoftwareBitmapSource? source = null;
        try
        {
            using var bitmap = await ListeningReportImages.DecodeCoverAsync(hash, token);
            if (bitmap is null || token.IsCancellationRequested || !IsPreview) return;
            source = new();
            await source.SetBitmapAsync(bitmap).AsTask();
            if (token.IsCancellationRequested || !IsPreview) return;
            PreviewCover = source;
            source = null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "无法加载听歌报告封面，使用默认图形");
        }
        finally
        {
            (source as IDisposable)?.Dispose();
            IsPreparingPreview = false;
            NotifyCommands();
        }
    }

    private bool CanClosePreview() => _active && IsPreview && !IsSaving;

    [RelayCommand(CanExecute = nameof(CanClosePreview))]
    private void ClosePreview() => ClearPreview();

    private void ClearPreview()
    {
        OpenPreviewCommand.Cancel();
        IsPreview = false;
        PreviewCover = null;
        SaveStatus = "";
        NotifyPreview();
    }

    private bool CanSave() => _active && !_stopping && IsPreview && !IsPreparingPreview && !IsSaving && _capture is not null && _pickPath is not null;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync(CancellationToken token)
    {
        if (!CanSave()) return;
        var capture = _capture!;
        var pickPath = _pickPath!;
        ListeningPeriod period = _snapshot!.Request.Period;
        SaveStatus = "";
        IsSaving = true;
        NotifyCommands();
        try
        {
            string periodName = period.Start.ToString(period.IsMonthly ? "yyyy-MM" : "yyyy", CultureInfo.InvariantCulture);
            string? path = await pickPath("OriginalSound-Listening-" + periodName, token);
            if (path is null) return;
            using var bitmap = await CapturePosterAsync(capture, token);
            await Task.Run(() => ListeningReportImages.SaveAsync(path, bitmap, token), token);
            if (!token.IsCancellationRequested && IsPreview)
                SaveStatus = string.Format(CultureInfo.CurrentUICulture, GetString("ListeningReportSaved"), path);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "无法保存听歌报告图片");
            if (!token.IsCancellationRequested && IsPreview)
                SaveStatus = GetString("ListeningReportSaveFailed");
        }
        finally
        {
            IsSaving = false;
            NotifyCommands();
        }
    }

    private async Task<SoftwareBitmap> CapturePosterAsync(Func<CancellationToken, Task<SoftwareBitmap>> capture, CancellationToken token)
    {
        IsCapturing = true;
        try { return await capture(token); }
        finally { IsCapturing = false; }
    }

    private void NotifyPreview()
    {
        OnPropertyChanged(nameof(BodyVisibility));
        NotifyCommands();
    }

    private void NotifyCommands()
    {
        RefreshCommand.NotifyCanExecuteChanged();
        OpenRecentCommand.NotifyCanExecuteChanged();
        OpenPreviewCommand.NotifyCanExecuteChanged();
        ClosePreviewCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanNavigateBack));
    }

    private static Visibility Visible(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
}
