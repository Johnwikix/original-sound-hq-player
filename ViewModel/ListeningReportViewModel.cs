using AnimatedWin2dControls;
using AnimatedWin2dControls.Impressionist;
using AnimatedWin2dControls.Renderer.Background;
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
    private readonly Func<string, CancellationToken, Task<string?>> _pickPath;
    private ImageSource? _cover;
    private ImageSource? _background;
    private readonly Func<(BackgroundShaderMode Shader, PaletteAlgorithm Palette)> _getShaderSettings;
    private PreviewSession? _preview;
    private PreviewSession? _renderingSession;
    private Task _previewTask = Task.CompletedTask;
    private CancellationTokenSource? _shaderCancellation;
    private int _shaderRevision;
    private int _selectedShaderIndex;

    private sealed class PreviewSession(string? coverHash, PaletteAlgorithm palette) : IDisposable
    {
        internal readonly CancellationTokenSource Lifetime = new();
        internal readonly string? CoverHash = coverHash;
        internal readonly PaletteAlgorithm Palette = palette;
        internal SoftwareBitmap? Cover;
        internal SoftwareBitmap? Background;
        internal bool CoverLoaded;

        public void Dispose()
        {
            Cover?.Dispose();
            Cover = null;
            Background?.Dispose();
            Background = null;
            Lifetime.Dispose();
        }
    }

    internal ListeningReportViewModel(ListeningReportQuery query, ILogger<ListeningReportViewModel> logger,
        ShutdownCoordinator shutdown, Func<(BackgroundShaderMode Shader, PaletteAlgorithm Palette)> getShaderSettings,
        Func<string, CancellationToken, Task<string?>> pickPath)
    {
        _query = query;
        _logger = logger;
        _getShaderSettings = getShaderSettings;
        _pickPath = pickPath;
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
    public bool IsPreview => _preview is not null;
    public bool CanPreview => _active && !_stopping && !IsLoading && !IsPreview && !IsSaving && _snapshot is { PlayCount: > 0 };
    public bool CanSelectShader => IsPreview && !_stopping && !IsSaving;
    public bool CanSavePreview => _active && !_stopping && IsPreview && !IsPreviewBusy && _capture is not null;
    public bool IsPreviewBusy => IsPreparingPreview || IsSaving;
    public Visibility SaveStatusVisibility => Visible(SaveStatus.Length > 0);

    public int SelectedShaderIndex
    {
        get => _selectedShaderIndex;
        set
        {
            if (!CanSelectShader || value is < 0 or > (int)BackgroundShaderMode.ChromaticResonance ||
                !SetProperty(ref _selectedShaderIndex, value)) return;
            SaveStatus = "";
            RequestPreviewRender();
        }
    }

    [ObservableProperty]
    public partial ListeningReportPresentation? Presentation { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LoadingVisibility))]
    public partial bool IsLoading { get; private set; }


    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPreviewBusy))]
    public partial bool IsSaving { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPreviewBusy))]
    public partial bool IsPreparingPreview { get; private set; }

    [ObservableProperty]
    public partial string StatusText { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveStatusVisibility))]
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

    public ImageSource? PreviewBackground
    {
        get => _background;
        private set
        {
            var previous = _background;
            if (SetProperty(ref _background, value)) (previous as IDisposable)?.Dispose();
        }
    }

    internal void Activate()
    {
        if (_active || _stopping) return;
        _active = true;
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
        EndPreview();
        _snapshot = null;
        Presentation = null;
        OnPropertyChanged(nameof(ReportVisibility));
        NotifyCommands();
    }

    internal async Task StopAsync()
    {
        _stopping = true;
        _loadCancellation?.Cancel();
        CancelPreviewWork();
        SaveCommand.Cancel();
        try
        {
            // 软件绘制的回读与 WIC 编码有不可中断的原生步骤，先等待在途保存退出。
            if (SaveCommand.ExecutionTask is { } save) await save;
        }
        finally
        {
            Deactivate();
            await _loadTask;
            await _previewTask;
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

    internal void BeginPreview(Func<CancellationToken, Task<SoftwareBitmap>> capture)
    {
        if (!CanPreview || _snapshot is not { } report) return;
        var (shader, palette) = _getShaderSettings();
        _selectedShaderIndex = Math.Clamp((int)shader, 0, (int)BackgroundShaderMode.ChromaticResonance);
        string? hash = report.Songs.Length > 0 ? report.Songs[0].CoverHash : null;
        _capture = capture;
        _preview = new(hash, palette);
        OnPropertyChanged(nameof(SelectedShaderIndex));
        RequestPreviewRender();
    }

    internal void EndPreview()
    {
        if (_preview is not { } session) return;
        CancelPreviewWork();
        _preview = null;
        _capture = null;
        if (!ReferenceEquals(session, _renderingSession)) session.Dispose();
        PreviewCover = null;
        PreviewBackground = null;
        IsPreparingPreview = false;
        SaveStatus = "";
        NotifyCommands();
    }

    internal (SoftwareBitmap? Cover, SoftwareBitmap? Background) CopyPosterImages()
    {
        if (_preview is not { } session)
            throw new InvalidOperationException("There is no active listening report preview.");

        var cover = session.Cover is { } source ? SoftwareBitmap.Copy(source) : null;
        try
        {
            return (cover, session.Background is { } background ? SoftwareBitmap.Copy(background) : null);
        }
        catch
        {
            cover?.Dispose();
            throw;
        }
    }

    private void CancelPreviewWork()
    {
        _preview?.Lifetime.Cancel();
        _shaderCancellation?.Cancel();
    }

    private void RequestPreviewRender()
    {
        _shaderRevision++;
        _shaderCancellation?.Cancel();
        IsPreparingPreview = true;
        NotifyCommands();
        if (_previewTask.IsCompleted) _previewTask = RenderPreviewAsync();
    }

    private async Task RenderPreviewAsync()
    {
        try
        {
            while (!_stopping && _preview is { } session)
            {
                _renderingSession = session;
                try
                {
                    if (!session.CoverLoaded) await LoadPreviewCoverAsync(session);
                    if (_stopping || !ReferenceEquals(_preview, session)) continue;

                    int revision = _shaderRevision;
                    using var cancellation = new CancellationTokenSource();
                    _shaderCancellation = cancellation;
                    await RenderBackgroundAsync(session, revision, (BackgroundShaderMode)_selectedShaderIndex, cancellation.Token);
                    if (ReferenceEquals(_preview, session) && revision == _shaderRevision) break;
                }
                finally
                {
                    _shaderCancellation = null;
                    _renderingSession = null;
                    if (!ReferenceEquals(_preview, session)) session.Dispose();
                }
            }
        }
        finally
        {
            IsPreparingPreview = false;
            NotifyCommands();
        }
    }

    private async Task LoadPreviewCoverAsync(PreviewSession session)
    {
        SoftwareBitmapSource? source = null;
        try
        {
            session.Cover = await ListeningReportImages.DecodeCoverAsync(session.CoverHash, session.Lifetime.Token);
            if (session.Cover is not { } cover || session.Lifetime.IsCancellationRequested) return;
            source = new();
            await source.SetBitmapAsync(cover).AsTask();
            if (session.Lifetime.IsCancellationRequested) return;
            PreviewCover = source;
            source = null;
        }
        catch (OperationCanceledException) when (session.Lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "听歌报告封面未就绪，使用默认图形");
            session.Cover?.Dispose();
            session.Cover = null;
        }
        finally
        {
            (source as IDisposable)?.Dispose();
            session.CoverLoaded = true;
        }
    }

    private async Task RenderBackgroundAsync(PreviewSession session, int revision, BackgroundShaderMode mode, CancellationToken token)
    {
        SoftwareBitmapSource? source = null;
        SoftwareBitmap? bitmap = null;
        try
        {
            if (session.Cover is { } cover)
            {
                bitmap = await Task.Run(() => BackgroundShaderRenderer.RenderCoverStillAsync(cover, mode, session.Palette, token), token);
                token.ThrowIfCancellationRequested();
                source = new();
                await source.SetBitmapAsync(bitmap).AsTask();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            (source as IDisposable)?.Dispose();
            source = null;
            bitmap?.Dispose();
            bitmap = null;
            _logger.LogDebug(ex, "听歌报告着色器未就绪，使用默认底色");
        }
        finally
        {
            if (!_stopping && ReferenceEquals(_preview, session) && revision == _shaderRevision && !token.IsCancellationRequested)
            {
                session.Background?.Dispose();
                session.Background = bitmap;
                bitmap = null;
                PreviewBackground = source;
                source = null;
            }
            (source as IDisposable)?.Dispose();
            bitmap?.Dispose();
        }
    }

    [RelayCommand(CanExecute = nameof(CanSavePreview))]
    private async Task SaveAsync(CancellationToken token)
    {
        if (!CanSavePreview) return;
        var capture = _capture!;
        ListeningPeriod period = _snapshot!.Request.Period;
        SaveStatus = "";
        IsSaving = true;
        NotifyCommands();
        try
        {
            string periodName = period.Start.ToString(period.IsMonthly ? "yyyy-MM" : "yyyy", CultureInfo.InvariantCulture);
            string? path = await _pickPath("OriginalSound-Listening-" + periodName, token);
            if (path is null) return;
            using var bitmap = await capture(token);
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

    private void NotifyCommands()
    {
        RefreshCommand.NotifyCanExecuteChanged();
        OpenRecentCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanSavePreview));
        OnPropertyChanged(nameof(CanSelectShader));
        OnPropertyChanged(nameof(CanPreview));
    }

    private static Visibility Visible(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
}
