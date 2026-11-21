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
    private const uint CoverDecodeStep = 128;
    private NowPlayingCanvas? _sceneCoverCanvas;
    private string? _sceneCoverHash;
    private CancellationTokenSource? _sceneCoverRequest;
    private readonly PlaybackSceneCoverPresenter _sceneCoverPresenter = new();
    private Rect _sceneCoverSlot;
    private uint _sceneCoverLayoutLimit;
    private uint _sceneCoverRequestedLimit;
    private object? _sceneCoverTrack;
    private object? _sceneCoverDecodedTrack;
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
        UpdateSceneCoverBounds();
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
        // The navigation state switches first. Keep the current cover and HDR mode
        // until the outgoing page has slid out and its canvas stops rendering.
        if (!vm.IsPlayingDetailVisible) return;
        if (!vm.IsFluidBackgroundEnabled && !ViewModel.UseHdrPresentation)
        {
            StopSceneCoverRendering();
            return;
        }

        uint limit = _sceneCoverLayoutLimit;
        if (limit == 0) return;

        string? hash = GetSceneCoverHash();
        bool sameTrack = _coverRequested && _sceneCoverHash == hash
            && (_useDarkDefaultCover == vm.IsDarkMode || (_coverReady && !_showingDefaultCover && _sceneCoverRequest is null));
        // Upgrade when needed, but keep the current size through small layout oscillations.
        if (sameTrack && limit <= _sceneCoverRequestedLimit && limit * 2 > _sceneCoverRequestedLimit)
            return;

        _sceneCoverRequest?.Cancel();
        _coverRequested = true;
        _sceneCoverHash = hash;
        _useDarkDefaultCover = vm.IsDarkMode;
        _sceneCoverRequestedLimit = limit;
        if (!sameTrack)
            _sceneCoverTrack = new object();
        _sceneCoverRequest = new CancellationTokenSource();
        _ = LoadSceneCoverAsync(hash, _useDarkDefaultCover, limit, _sceneCoverTrack!, _sceneCoverRequest);
    }

    private string? GetSceneCoverHash()
    {
        var hash = ViewModel.AppViewModel.LyricPageBackgroundHash;
        return string.IsNullOrWhiteSpace(hash) ? null : hash;
    }

    private async Task LoadSceneCoverAsync(string? hash, bool isDark, uint limit, object track, CancellationTokenSource request)
    {
        try
        {
            var token = request.Token;
            var (art, useDefault) = await Task.Run(async () =>
            {
                if (hash is not null)
                {
                    string rawPath = ToolUtils.FindRawCachePath(hash);
                    var cover = await PlaybackSceneCover.ReadPlaybackAsync(rawPath, token, limit);
                    if (cover is not null) return (cover, false);
                }
                return (await PlaybackSceneCover.ReadDefaultAsync(isDark, token, limit), true);
            }, token);
            if (request.IsCancellationRequested || !ReferenceEquals(request, _sceneCoverRequest)) return;
            bool resizing = ReferenceEquals(_sceneCoverDecodedTrack, track);
            if (useDefault && resizing && !_showingDefaultCover) return;
            if (art is null)
            {
                if (!resizing) ReportCoverUnavailable();
                return;
            }
            bool animate = _coverPreviewHidden && !resizing && (!_showingDefaultCover || !useDefault) && IsCoverMotionEnabled;
            _sceneCoverPresenter.SetArtwork(art, DetailCoverSwitcher.SwitchType, animate, track);
            _sceneCoverDecodedTrack = track;
            _showingDefaultCover = useDefault;
            if (!_coverReady)
            {
                _coverReady = true;
                _sceneCoverPresenter.SetLayout(_sceneCoverSlot, DetailCoverSwitcher.CornerRadiusAmount, DetailCoverSwitcher.ShadowAmount);
                _sceneCoverCanvas?.SetSceneCover(_sceneCoverPresenter);
                UpdateHdrPresentation();
            }
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to prepare the playback scene cover");
            if (!request.IsCancellationRequested && ReferenceEquals(request, _sceneCoverRequest) &&
                !ReferenceEquals(_sceneCoverDecodedTrack, track))
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
        _sceneCoverDecodedTrack = null;
        UpdateHdrPresentation();
        if (ViewModel.UseHdrPresentation)
            ViewModel.AppViewModel.ReportPlaybackHdrPresentation(false, true);
        RejectHdrEntrance();
    }

    private void OnSceneCoverLayoutUpdated(object? sender, object e) => UpdateSceneCoverBounds();

    private void UpdateSceneCoverBounds()
    {
        if (_sceneCoverCanvas is not { IsLoaded: true } canvas || !_coverAttached || !DetailCoverSwitcher.IsLoaded)
            return;
        bool wasReady = CanPresentHdrCover;
        var slot = DetailCoverSwitcher.TransformToVisual(canvas).TransformBounds(
            new Rect(0, 0, DetailCoverSwitcher.ActualWidth, DetailCoverSwitcher.ActualHeight));
        if (_sceneCoverSlot != slot)
        {
            _sceneCoverSlot = slot;
            _sceneCoverPresenter.SetLayout(slot, DetailCoverSwitcher.CornerRadiusAmount, DetailCoverSwitcher.ShadowAmount);
        }
        if (wasReady != CanPresentHdrCover) UpdateHdrPresentation();

        uint limit = GetSceneCoverDecodeLimit(slot, canvas.XamlRoot?.RasterizationScale ?? 1);
        if (_sceneCoverLayoutLimit != limit)
        {
            _sceneCoverLayoutLimit = limit;
            RefreshSceneCover();
        }
    }

    private static uint GetSceneCoverDecodeLimit(Rect slot, double scale)
    {
        if (slot.Width <= 0 || slot.Height <= 0) return 0;
        double pixels = Math.Max(slot.Width, slot.Height) * scale;
        return (uint)Math.Min(PlaybackCoverImage.MaxPixelSize, Math.Ceiling(pixels / CoverDecodeStep) * CoverDecodeStep);
    }

    private void PrepareSceneCoverFallback()
    {
        if (_coverAttached && _coverPreviewHidden && ViewModel.AppViewModel.IsPlayingDetailVisible &&
            _sceneCoverHash == GetSceneCoverHash() && ReferenceEquals(_sceneCoverTrack, _sceneCoverDecodedTrack) &&
            (!_showingDefaultCover || _useDarkDefaultCover == ViewModel.AppViewModel.IsDarkMode))
            DetailCoverSwitcher.PrepareSceneFallback(_sceneCoverPresenter.RequestedArtwork);
    }

    private void CommitCoverFrame(bool drawn)
    {
        if (_coverPreviewHidden == drawn) return;
        if (!drawn) PrepareSceneCoverFallback();
        _coverPreviewHidden = drawn;
        DetailCoverSwitcher.SetScenePreviewSuppressed(drawn);
    }

    private void ClearSceneCover()
    {
        PrepareSceneCoverFallback();
        _sceneCoverRequest?.Cancel();
        _sceneCoverRequest = null;
        _coverRequested = false;
        _coverReady = false;
        _showingDefaultCover = false;
        _sceneCoverHash = null;
        _sceneCoverTrack = null;
        _sceneCoverDecodedTrack = null;
        _sceneCoverRequestedLimit = 0;
        _sceneCoverCanvas?.SetSceneCover(null);
        _sceneCoverPresenter.Clear();
    }

    private void StopSceneCoverRendering()
    {
        if (!_coverRequested) return;
        ClearSceneCover();
        UpdateHdrPresentation();
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
        _sceneCoverSlot = default;
        _sceneCoverLayoutLimit = 0;
        CommitCoverFrame(drawn: false);
    }
}
