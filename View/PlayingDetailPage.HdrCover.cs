using AnimatedWin2dControls.Controls;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.View;

public sealed partial class PlayingDetailPage
{
    private NowPlayingCanvas? _sceneCoverCanvas;
    private string? _sceneCoverHash;
    private CancellationTokenSource? _sceneCoverRequest;
    private readonly PlaybackSceneCoverPresenter _sceneCoverPresenter = new();
    private Rect _sceneCoverSlot;
    private bool _coverRequested;
    private bool _coverReady;
    private bool _useDarkDefaultCover;
    private bool _showingDefaultCover;
    private bool CanPresentHdrCover => _coverReady && _sceneCoverSlot.Width > 0 && _sceneCoverSlot.Height > 0;
    private bool _coverPreviewHidden;
    private bool _coverAttached;

    private void InitializeSceneCover()
    {
        Loaded += OnSceneCoverPageLoaded;
        Unloaded += OnSceneCoverPageUnloaded;
    }

    private void OnSceneCoverPageLoaded(object sender, RoutedEventArgs args)
    {
        if (_coverAttached) return;
        _coverAttached = true;
        DetailCoverSwitcher.LayoutUpdated += OnSceneCoverLayoutUpdated;
        ViewModel.AppViewModel.PropertyChanged += OnSceneCoverChanged;
        RefreshSceneCover();
    }

    private void OnSceneCoverPageUnloaded(object sender, RoutedEventArgs args)
    {
        DetachSceneCover();
    }

    private void DetachSceneCover()
    {
        if (!_coverAttached) return;
        _coverAttached = false;
        DetailCoverSwitcher.LayoutUpdated -= OnSceneCoverLayoutUpdated;
        ViewModel.AppViewModel.PropertyChanged -= OnSceneCoverChanged;
        ClearSceneCover();
        DetachSceneCoverCanvas();
    }

    private void AttachSceneCoverCanvas(NowPlayingCanvas canvas)
    {
        if (ReferenceEquals(canvas, _sceneCoverCanvas)) return;
        DetachSceneCoverCanvas();
        _sceneCoverCanvas = canvas;
        canvas.SceneCoverPresented += CommitCoverFrame;
        if (_coverReady)
            canvas.SetSceneCover(_sceneCoverPresenter);
        UpdateSceneCoverBounds();
    }

    private void OnSceneCoverChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(AppViewModel.LyricPageBackgroundHash) or nameof(AppViewModel.PlaybackHdrDisplayAvailable)
            or nameof(AppViewModel.PlaybackHdrEnabled) or nameof(AppViewModel.IsPlayingDetailVisible)
            or nameof(AppViewModel.IsFluidBackgroundEnabled) or nameof(AppViewModel.WindowBackgroundImagePath)
            or nameof(AppViewModel.IsDarkMode))
        {
            if (DispatcherQueue.HasThreadAccess)
                RefreshSceneCover();
            else
                DispatcherQueue.TryEnqueue(RefreshSceneCover);
        }
    }

    private void PlaybackHdrPresentationChanged(bool active, bool failed)
        => ViewModel.AppViewModel.ReportPlaybackHdrPresentation(active, failed);

    private void UpdateHdrPresentation()
    {
        if (!_isLoaded || _disposed) return;
        var app = ViewModel.AppViewModel;
        bool enabled = ViewModel.UseHdrPresentation && CanPresentHdrCover;
        NowPlaying?.SetHdrPresentation(enabled ? app.PlaybackHdrWhiteNits : 0,
            enabled ? app.PlaybackHdrEffectivePeakNits : 0, ViewModel.IsBackgroundActive, app.PlaybackHdrDisplayId);
    }

    private void RefreshSceneCover()
    {
        if (!_coverAttached) return;

        var vm = ViewModel.AppViewModel;
        if (!vm.IsPlayingDetailVisible || (!vm.IsFluidBackgroundEnabled && !ViewModel.UseHdrPresentation))
        {
            if (_coverRequested)
            {
                ClearSceneCover();
                UpdateHdrPresentation();
            }
            return;
        }

        string? hash = string.IsNullOrWhiteSpace(vm.LyricPageBackgroundHash) ? null : vm.LyricPageBackgroundHash;
        if (_coverRequested && _sceneCoverHash == hash
            && (_useDarkDefaultCover == vm.IsDarkMode || (_coverReady && !_showingDefaultCover && _sceneCoverRequest is null)))
            return;

        _sceneCoverRequest?.Cancel();
        _coverRequested = true;
        _sceneCoverHash = hash;
        _useDarkDefaultCover = vm.IsDarkMode;
        _sceneCoverRequest = new CancellationTokenSource();
        _ = LoadSceneCoverAsync(hash, _useDarkDefaultCover, _sceneCoverRequest);
    }

    private async Task LoadSceneCoverAsync(string? hash, bool isDark, CancellationTokenSource request)
    {
        try
        {
            var token = request.Token;
            var (art, useDefault) = await Task.Run(async () =>
            {
                if (hash is not null)
                {
                    string rawPath = ToolUtils.FindRawCachePath(hash);
                    var cover = await PlaybackSceneCover.ReadPlaybackAsync(rawPath, token);
                    if (cover is not null) return (cover, false);
                }
                return (await PlaybackSceneCover.ReadDefaultAsync(isDark, token), true);
            }, token);
            if (request.IsCancellationRequested || !ReferenceEquals(request, _sceneCoverRequest)) return;
            if (art is null)
            {
                ReportCoverUnavailable();
                return;
            }
            bool animate = _coverPreviewHidden && !_showingDefaultCover && !useDefault && IsCoverMotionEnabled;
            _sceneCoverPresenter.SetArtwork(art, DetailCoverSwitcher.SwitchType, animate);
            _showingDefaultCover = useDefault;
            _coverReady = true;
            UpdateSceneCoverBounds();
            _sceneCoverCanvas?.SetSceneCover(_sceneCoverPresenter);
            UpdateHdrPresentation();
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to prepare the playback scene cover");
            if (!request.IsCancellationRequested && ReferenceEquals(request, _sceneCoverRequest))
                ReportCoverUnavailable();
        }
        finally
        {
            if (ReferenceEquals(_sceneCoverRequest, request)) _sceneCoverRequest = null;
            request.Dispose();
        }
    }

    private void ReportCoverUnavailable()
    {
        _coverReady = false;
        _sceneCoverCanvas?.SetSceneCover(null);
        _sceneCoverPresenter.Clear();
        _sceneCoverSlot = default;
        UpdateHdrPresentation();
        if (ViewModel.UseHdrPresentation)
            ViewModel.AppViewModel.ReportPlaybackHdrPresentation(false, true);
        RejectHdrEntrance();
    }

    private void OnSceneCoverLayoutUpdated(object? sender, object e) => UpdateSceneCoverBounds();

    private void UpdateSceneCoverBounds()
    {
        if (!_coverRequested || _sceneCoverCanvas is not { IsLoaded: true } canvas || !_coverAttached
            || !DetailCoverSwitcher.IsLoaded) return;
        bool wasReady = CanPresentHdrCover;
        var slot = DetailCoverSwitcher.TransformToVisual(canvas).TransformBounds(
            new Rect(0, 0, DetailCoverSwitcher.ActualWidth, DetailCoverSwitcher.ActualHeight));
        if (_sceneCoverSlot != slot)
        {
            _sceneCoverSlot = slot;
            _sceneCoverPresenter.SetLayout(slot, DetailCoverSwitcher.CornerRadiusAmount, DetailCoverSwitcher.ShadowAmount);
        }
        if (wasReady != CanPresentHdrCover) UpdateHdrPresentation();
    }

    private void CommitCoverFrame(bool drawn)
    {
        if (_coverPreviewHidden == drawn) return;
        _coverPreviewHidden = drawn;
        DetailCoverSwitcher.SetScenePreviewSuppressed(drawn);
    }

    private void ClearSceneCover()
    {
        _sceneCoverRequest?.Cancel();
        _sceneCoverRequest = null;
        _coverRequested = false;
        _coverReady = false;
        _showingDefaultCover = false;
        _sceneCoverHash = null;
        _sceneCoverSlot = default;
        _sceneCoverCanvas?.SetSceneCover(null);
        _sceneCoverPresenter.Clear();
    }

    private void DisposeSceneCover()
    {
        Loaded -= OnSceneCoverPageLoaded;
        Unloaded -= OnSceneCoverPageUnloaded;
        DetachSceneCover();
        DetachSceneCoverCanvas();
    }

    private void DetachSceneCoverCanvas()
    {
        if (_sceneCoverCanvas is not { } canvas) return;
        canvas.SceneCoverPresented -= CommitCoverFrame;
        canvas.SetSceneCover(null);
        _sceneCoverCanvas = null;
        CommitCoverFrame(drawn: false);
    }
}
