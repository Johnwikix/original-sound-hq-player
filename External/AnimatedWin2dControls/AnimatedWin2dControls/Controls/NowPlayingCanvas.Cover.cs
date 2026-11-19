#nullable enable
using AnimatedWin2dControls.Interop;
using AnimatedWin2dControls.Renderer.Background;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Dispatching;
using System;
using System.Threading;
using Windows.Foundation;

namespace AnimatedWin2dControls.Controls;

/// <summary>Draws the playback cover inside the scene. Calls are serialized by the canvas.</summary>
public interface IPlaybackSceneCover
{
    /// <summary>Returns whether the current frame contains the cover.</summary>
    bool Draw(PlaybackCanvasFrame frame, CanvasDrawContext session);

    /// <summary>Supplies the fitted artwork bounds and corner radius to the spectrum layer.</summary>
    bool TryGetArtworkBounds(out Rect bounds, out float radius)
    {
        bounds = default;
        radius = 0;
        return false;
    }

    /// <summary>Releases the matching device resources, or all resources when device is null.</summary>
    void ReleaseDeviceResources(CanvasDevice? device);
}

public sealed partial class NowPlayingCanvas
{
    private IPlaybackSceneCover? _sceneCover;
    private bool _sceneCoverPresented;
    private volatile bool _sdrCoverVisible;
    private volatile bool _sdrCoverDrawn;
    private volatile bool _sdrCoverPresented;
    private int _sdrCoverObservationQueued;
    private bool? _sdrCoverObserved;
    private FrameEventSubscription? _sdrCoverClock;
    private readonly DispatcherQueue _coverDispatcher;

    /// <summary>Reports cover ownership on the UI thread after the visible scene has a current frame.</summary>
    public event Action<bool>? SceneCoverPresented;

    /// <summary>Attaches the cover renderer on the UI thread and retires the previous renderer.</summary>
    public void SetSceneCover(IPlaybackSceneCover? cover)
    {
        lock (_cacheGate)
        {
            if (ReferenceEquals(_sceneCover, cover)) return;
            var previous = _sceneCover;
            _sceneCover = cover;
            previous?.ReleaseDeviceResources(null);
        }
    }

    private bool DrawSceneCover(PlaybackCanvasFrame frame, CanvasDrawContext session)
        => (frame.Hdr.IsEnabled || _background is not ImageBackgroundRenderer) && _sceneCover?.Draw(frame, session) == true;

    private void CommitSceneCover(bool drawn)
    {
        if (_sceneCoverPresented == drawn) return;
        _sceneCoverPresented = drawn;
        SceneCoverPresented?.Invoke(drawn);
    }

    private void ReportSdrCoverDraw(bool drawn)
    {
        if (_sdrCoverDrawn != drawn)
            _sdrCoverDrawn = drawn;
        if (!_sdrCoverVisible || drawn == _sdrCoverPresented || Volatile.Read(ref _sdrCoverObservationQueued) != 0)
            return;
        if (Interlocked.CompareExchange(ref _sdrCoverObservationQueued, 1, 0) == 0 &&
            !_coverDispatcher.TryEnqueue(ObserveSdrCover))
            Volatile.Write(ref _sdrCoverObservationQueued, 0);
    }

    private void ObserveSdrCover()
    {
        if (!_sdrCoverVisible || _sdrCoverDrawn == _sdrCoverPresented)
        {
            Volatile.Write(ref _sdrCoverObservationQueued, 0);
            return;
        }
        _sdrCoverClock ??= FrameEventSubscription.SubscribeRendering(CommitSdrCover);
    }

    private void CommitSdrCover()
    {
        if (!_sdrCoverVisible)
        {
            StopSdrCoverObservation();
            return;
        }

        // The Win2D Draw callback runs before native Present. Observe the result
        // over a composition interval before switching the XAML preview.
        bool drawn = _sdrCoverDrawn;
        if (_sdrCoverObserved != drawn)
        {
            _sdrCoverObserved = drawn;
            return;
        }
        _sdrCoverPresented = drawn;
        StopSdrCoverObservation();
        CommitSceneCover(drawn);
    }

    private void StopSdrCoverObservation()
    {
        _sdrCoverClock?.Dispose();
        _sdrCoverClock = null;
        _sdrCoverObserved = null;
        Volatile.Write(ref _sdrCoverObservationQueued, 0);
    }

    private void ResetSdrCover()
    {
        _sdrCoverVisible = false;
        StopSdrCoverObservation();
        _sdrCoverDrawn = false;
        _sdrCoverPresented = false;
        CommitSceneCover(false);
    }
}
