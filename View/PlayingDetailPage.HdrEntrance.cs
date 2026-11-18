using AnimatedWin2dControls.Controls;
using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;

namespace WinUIMusicPlayer.View;

public sealed partial class PlayingDetailPage
{
    private bool _canvasNavigationTransition;
    private bool _hdrEntranceWindowPaused;
    private TaskCompletionSource<bool>? _hdrEntranceFrame;
    private NowPlayingCanvas? _hdrEntranceCanvas;

    internal bool ShouldPrepareHdrEntrance => ViewModel.UseHdrPresentation;
    internal bool IsHdrEntrancePresented => ShouldPrepareHdrEntrance && NowPlaying?.IsHdrFramePresented == true;

    internal async Task<bool> PrepareHdrEntranceAsync(CancellationToken cancellationToken)
    {
        if (_hdrEntranceWindowPaused)
            throw new OperationCanceledException();
        ClearHdrEntranceObservation();
        var frame = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _hdrEntranceFrame = frame;
        ViewModel.PropertyChanged += OnHdrEntranceModeChanged;
        try
        {
            if (NowPlaying is { IsLoaded: true } canvas)
                ObserveHdrEntrance(canvas);
            _ = SetCanvasNavigationTransitionAsync(false);
            if (!ShouldPrepareHdrEntrance || (_coverRequested && !_coverReady && _hdrCoverRequest is null))
                RejectHdrEntrance();
            return await frame.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            if (ReferenceEquals(_hdrEntranceFrame, frame))
                ClearHdrEntranceObservation();
        }
    }

    private void ObserveHdrEntrance(NowPlayingCanvas canvas)
    {
        if (_hdrEntranceFrame is not { } frame || ReferenceEquals(_hdrEntranceCanvas, canvas))
            return;
        if (_hdrEntranceCanvas is { } previous)
            previous.HdrPresentationChanged -= OnHdrEntranceFrame;
        _hdrEntranceCanvas = canvas;
        canvas.HdrPresentationChanged += OnHdrEntranceFrame;
        if (!canvas.HasHdrPresentationSurface || canvas.HasHdrPresentationFailed)
            RejectHdrEntrance();
        else if (canvas.IsHdrFramePresented)
            frame.TrySetResult(true);
    }

    private void ClearHdrEntranceObservation()
    {
        ViewModel.PropertyChanged -= OnHdrEntranceModeChanged;
        if (_hdrEntranceCanvas is { } canvas)
            canvas.HdrPresentationChanged -= OnHdrEntranceFrame;
        _hdrEntranceCanvas = null;
        _hdrEntranceFrame = null;
    }

    private void OnHdrEntranceModeChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (!ShouldPrepareHdrEntrance)
            RejectHdrEntrance();
    }

    private void OnHdrEntranceFrame(bool active, bool failed)
    {
        if (active || failed)
            _hdrEntranceFrame?.TrySetResult(active);
    }

    private void RejectHdrEntrance() => _hdrEntranceFrame?.TrySetResult(false);

    private void CancelHdrEntrance() => _hdrEntranceFrame?.TrySetCanceled();

    private void UpdateHdrEntranceWindowPaused(bool paused)
    {
        _hdrEntranceWindowPaused = paused;
        if (paused)
            CancelHdrEntrance();
    }

    internal Task PrepareSdrEntranceAsync()
    {
        _ = SetCanvasNavigationTransitionAsync(true);
        return NowPlaying?.PrepareSdrNavigationFrameAsync() ?? Task.CompletedTask;
    }

    public Task SetCanvasNavigationTransitionAsync(bool active)
    {
        _canvasNavigationTransition = active;
        if (!active)
        {
            UpdateSceneRegions();
            UpdateHdrCoverBounds();
        }
        return NowPlaying?.SetHdrNavigationTransitionAsync(active) ?? Task.CompletedTask;
    }
}
