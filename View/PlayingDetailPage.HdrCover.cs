using AnimatedWin2dControls.Controls;
using Microsoft.Extensions.Logging;
using Microsoft.Graphics.Canvas;
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
    private NowPlayingCanvas? _hdrCoverCanvas;
    private string? _hdrCoverHash;
    private CancellationTokenSource? _hdrCoverRequest;
    private readonly PlaybackSceneCoverPresenter _hdrCoverPresenter = new();
    private Rect _hdrCoverSlot;
    private bool _coverRequested;
    private bool _coverReady;
    private bool _useDarkDefaultCover;
    private bool _showingDefaultCover;
    private bool CanPresentHdrCover => _coverReady && _hdrCoverSlot.Width > 0 && _hdrCoverSlot.Height > 0;
    private bool _hdrCoverDrawn;
    private bool _coverPreviewHidden;
    private bool _coverAttached;

    private void InitializeHdrCover()
    {
        Loaded += OnHdrCoverPageLoaded;
        Unloaded += OnHdrCoverPageUnloaded;
    }

    private void OnHdrCoverPageLoaded(object sender, RoutedEventArgs args)
    {
        if (_coverAttached) return;
        _coverAttached = true;
        DetailCoverSwitcher.LayoutUpdated += OnHdrCoverLayoutUpdated;
        ViewModel.AppViewModel.PropertyChanged += OnHdrCoverChanged;
        RefreshHdrCover();
    }

    private void OnHdrCoverPageUnloaded(object sender, RoutedEventArgs args)
    {
        DetachHdrCover();
    }

    private void DetachHdrCover()
    {
        if (!_coverAttached) return;
        _coverAttached = false;
        DetailCoverSwitcher.LayoutUpdated -= OnHdrCoverLayoutUpdated;
        ViewModel.AppViewModel.PropertyChanged -= OnHdrCoverChanged;
        ClearHdrCover();
    }

    private void AttachHdrCoverCanvas(NowPlayingCanvas canvas)
    {
        if (ReferenceEquals(canvas, _hdrCoverCanvas)) return;
        DetachHdrCoverCanvas();
        _hdrCoverCanvas = canvas;
        canvas.DrawHdrForeground += DrawHdrCover;
        canvas.HdrFramePresented += CommitHdrCoverFrame;
        UpdateHdrCoverBounds();
    }

    private void OnHdrCoverChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(AppViewModel.LyricPageBackgroundHash) or nameof(AppViewModel.PlaybackHdrDisplayAvailable)
            or nameof(AppViewModel.PlaybackHdrEnabled) or nameof(AppViewModel.IsPlayingDetailVisible)
            or nameof(AppViewModel.IsFluidBackgroundEnabled) or nameof(AppViewModel.WindowBackgroundImagePath)
            or nameof(AppViewModel.IsDarkMode))
            RefreshHdrCover();
    }

    private void PlaybackHdrPresentationChanged(bool active, bool failed)
    {
        // A failed first Present can already own a cover texture while the reported state is still SDR.
        if (!active)
        {
            RestoreCoverPreview();
            ReleaseHdrCoverTexture();
        }
        ViewModel.AppViewModel.ReportPlaybackHdrPresentation(active, failed);
    }

    private void UpdateHdrPresentation()
    {
        if (!_isLoaded || _disposed) return;
        var app = ViewModel.AppViewModel;
        bool enabled = ViewModel.UseHdrPresentation && CanPresentHdrCover;
        NowPlaying?.SetHdrPresentation(enabled ? app.PlaybackHdrWhiteNits : 0,
            enabled ? app.PlaybackHdrEffectivePeakNits : 0, ViewModel.IsBackgroundActive, app.PlaybackHdrDisplayId);
    }

    private void RefreshHdrCover()
    {
        var vm = ViewModel.AppViewModel;
        if (!vm.IsPlayingDetailVisible || !ViewModel.UseHdrPresentation)
        {
            if (_coverRequested)
            {
                ClearHdrCover();
                UpdateHdrPresentation();
            }
            return;
        }

        string? hash = string.IsNullOrWhiteSpace(vm.LyricPageBackgroundHash) ? null : vm.LyricPageBackgroundHash;
        if (_coverRequested && _hdrCoverHash == hash
            && (_useDarkDefaultCover == vm.IsDarkMode || (_coverReady && !_showingDefaultCover && _hdrCoverRequest is null)))
            return;

        _hdrCoverRequest?.Cancel();
        _coverRequested = true;
        _hdrCoverHash = hash;
        _useDarkDefaultCover = vm.IsDarkMode;
        _hdrCoverRequest = new CancellationTokenSource();
        _ = LoadHdrCoverAsync(hash, _useDarkDefaultCover, _hdrCoverRequest);
    }

    private async Task LoadHdrCoverAsync(string? hash, bool isDark, CancellationTokenSource request)
    {
        try
        {
            var token = request.Token;
            var (art, useDefault) = await Task.Run(async () =>
            {
                if (hash is not null)
                {
                    string rawPath = ToolUtils.FindRawCachePath(hash);
                    var cover = await PlaybackSceneCover.ReadAsync(rawPath, true, token);
                    if (cover is null)
                        cover = await PlaybackSceneCover.ReadAsync(PlaybackCoverImage.GetCachePath(rawPath), true, token);
                    if (cover is not null) return (cover, false);
                }
                return (await PlaybackSceneCover.ReadDefaultAsync(isDark, token), true);
            }, token);
            if (request.IsCancellationRequested || !ReferenceEquals(request, _hdrCoverRequest)) return;
            if (art is null)
            {
                ReportCoverUnavailable();
                return;
            }
            bool animate = _coverPreviewHidden && !_showingDefaultCover && !useDefault && IsCoverMotionEnabled;
            _hdrCoverPresenter.SetArtwork(art, DetailCoverSwitcher.SwitchType, animate);
            _showingDefaultCover = useDefault;
            _coverReady = true;
            UpdateHdrCoverBounds();
            UpdateHdrPresentation();
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to prepare the playback cover for HDR");
            if (!request.IsCancellationRequested && ReferenceEquals(request, _hdrCoverRequest))
                ReportCoverUnavailable();
        }
        finally
        {
            if (ReferenceEquals(_hdrCoverRequest, request)) _hdrCoverRequest = null;
            request.Dispose();
        }
    }

    private void ReportCoverUnavailable()
    {
        _coverReady = false;
        _hdrCoverPresenter.Clear();
        UpdateHdrPresentation();
        ViewModel.AppViewModel.ReportPlaybackHdrPresentation(false, true);
        RejectHdrEntrance();
    }

    private void OnHdrCoverLayoutUpdated(object? sender, object e) => UpdateHdrCoverBounds();

    private void UpdateHdrCoverBounds()
    {
        if (!_coverRequested || _hdrCoverCanvas is not { IsLoaded: true } canvas || !_coverAttached
            || !DetailCoverSwitcher.IsLoaded) return;
        bool wasReady = CanPresentHdrCover;
        _hdrCoverSlot = DetailCoverSwitcher.TransformToVisual(canvas).TransformBounds(
            new Rect(0, 0, DetailCoverSwitcher.ActualWidth, DetailCoverSwitcher.ActualHeight));
        if (wasReady != CanPresentHdrCover) UpdateHdrPresentation();
    }

    private void DrawHdrCover(CanvasDrawingSession session, float peakNits)
    {
        _hdrCoverDrawn = CanPresentHdrCover && _hdrCoverPresenter.Draw(session, _hdrCoverSlot,
            DetailCoverSwitcher.CornerRadiusAmount, DetailCoverSwitcher.ShadowAmount,
            (float)ViewModel.AppViewModel.PlaybackHdrWhiteNits, peakNits);
    }

    private void CommitHdrCoverFrame()
    {
        if (_coverPreviewHidden == _hdrCoverDrawn) return;
        _coverPreviewHidden = _hdrCoverDrawn;
        DetailCoverSwitcher.Opacity = _coverPreviewHidden ? 0 : 1;
    }

    private void RestoreCoverPreview()
    {
        _hdrCoverDrawn = false;
        CommitHdrCoverFrame();
    }

    private void ReleaseHdrCoverTexture() => _hdrCoverPresenter.ReleaseGpu();

    private void ClearHdrCover()
    {
        _hdrCoverRequest?.Cancel();
        _hdrCoverRequest = null;
        _coverRequested = false;
        _coverReady = false;
        _showingDefaultCover = false;
        _hdrCoverHash = null;
        _hdrCoverSlot = default;
        _hdrCoverPresenter.Clear();
        if (!ViewModel.AppViewModel.PlaybackHdrActive)
            RestoreCoverPreview();
    }

    private void DisposeHdrCover()
    {
        Loaded -= OnHdrCoverPageLoaded;
        Unloaded -= OnHdrCoverPageUnloaded;
        DetachHdrCover();
        DetachHdrCoverCanvas();
    }

    private void DetachHdrCoverCanvas()
    {
        if (_hdrCoverCanvas is not { } canvas) return;
        canvas.DrawHdrForeground -= DrawHdrCover;
        canvas.HdrFramePresented -= CommitHdrCoverFrame;
        _hdrCoverCanvas = null;
        RestoreCoverPreview();
    }
}
