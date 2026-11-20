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
    private Action? _interruptHdrEntranceMotion;

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
            if (!ShouldPrepareHdrEntrance || (_coverRequested && !_coverReady && _sceneCoverRequest is null))
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
        // x:Load 首次触发 Loaded 时模板可能还没展开，先创建面板再判断能否呈现 HDR。
        if (!canvas.HasHdrPresentationSurface)
            canvas.ApplyTemplate();
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

    private void CancelHdrEntrance()
    {
        _hdrEntranceFrame?.TrySetCanceled();
        _interruptHdrEntranceMotion?.Invoke();
    }

    internal async Task<bool> RunHdrEntranceMotionAsync(Func<CancellationToken, Task> move, Action hide,
        CancellationToken cancellationToken)
    {
        if (NowPlaying is not { } canvas || !IsHdrEntrancePresented)
            return false;

        using var motion = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _interruptHdrEntranceMotion = Interrupt;
        long generation = canvas.BeginHdrNavigationMotion();
        canvas.HdrPresentationChanged += OnPresentationChanged;
        ViewModel.PropertyChanged += OnModeChanged;
        try
        {
            await move(motion.Token);
            return !motion.IsCancellationRequested;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            if (_interruptHdrEntranceMotion == (Action)Interrupt)
                _interruptHdrEntranceMotion = null;
            canvas.EndHdrNavigationMotion(generation);
            canvas.HdrPresentationChanged -= OnPresentationChanged;
            ViewModel.PropertyChanged -= OnModeChanged;
        }

        void Interrupt()
        {
            if (motion.IsCancellationRequested) return;
            hide();
            motion.Cancel();
        }

        void OnPresentationChanged(bool active, bool failed)
        {
            if (!active) Interrupt();
        }

        void OnModeChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (!ShouldPrepareHdrEntrance) Interrupt();
        }
    }

    internal bool TryBeginHdrDismissMotion()
    {
        if (NowPlaying is not { IsHdrFramePresented: true } canvas || !ShouldPrepareHdrEntrance)
            return false;
        canvas.BeginHdrNavigationMotion();
        // PauseCanvasRendering ends HDR presentation once the outgoing frame is hidden.
        return true;
    }

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
        if (active)
            SetTitleScenePreparation(false);
        else
        {
            UpdateSceneRegions();
            UpdateSceneCoverBounds();
        }
        return NowPlaying?.SetHdrNavigationTransitionAsync(active) ?? Task.CompletedTask;
    }
}
