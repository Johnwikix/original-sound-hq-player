#nullable enable
using AnimatedWin2dControls.Interop;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using SceneTitle = AnimatedWin2dControls.Controls.AnimatedTextBlock.AnimatedTextBlock;

namespace AnimatedWin2dControls.Controls;

public sealed partial class NowPlayingCanvas
{
    private const string HdrPanelPart = "PART_HdrPanel";
    private CanvasSwapChainPanel? _hdrPanel;
    private CanvasSwapChain? _hdrSwapChain;
    private CanvasDevice? _hdrDevice;
    private CanvasSolidColorBrush? _hdrClear;
    private Size _hdrTargetSize;
    private float _hdrTargetDpi;
    private PlaybackHdrColor _hdrColor;
    private PlaybackHdrColor _bgCacheHdr;
    private ulong _hdrDisplayId;
    private PresentationFrameClock _hdrFrameClock;
    private bool _sdrAllowed = true;
    private volatile bool _sdrFrameEnabled;
    private FrameEventSubscription? _hdrClock;
    private FrameEventSubscription? _sdrHandoffClock;
    private TaskCompletionSource? _sdrHandoffCompletion;
    private long _sdrHandoffStarted;
    private volatile bool _sdrFrameSubmitted;
    private bool _sdrFrameObserved;
    private bool _releaseHdrAfterHandoff;
    private bool _navigationTransitionActive;
    private bool _hdrEntranceAnimating;
    private long _hdrEntranceMotionGeneration;
    private volatile bool _hdrOwnsFrame;
    private bool _hdrPresented;
    private bool _hdrFailed;
    private bool _hdrDeviceLostRetried;
    private bool _hdrShuttingDown;
    private SceneTitle? _sceneTitle;
    private Rect _sceneTitleRegion;
    private HdrTitleLayer? _hdrTitleLayer;
    private bool _hdrTitleLayerUnsupported;

    public event Action<bool, bool>? HdrPresentationChanged;

    public bool HasHdrPresentationSurface => _hdrPanel is not null;
    public bool HasHdrPresentationFailed => _hdrFailed;
    public bool IsHdrFramePresented => _hdrPresented;

    /// <summary>Commits XAML foreground ownership after a successful HDR Present on the UI thread.</summary>
    public event Action? HdrFramePresented;

    /// <summary>Draws the cover above lyrics and below the title in the same HDR session.</summary>
    public event Action<CanvasDrawingSession, float>? DrawHdrForeground;

    /// <summary>Updates the display configuration on the UI thread.</summary>
    public void SetHdrPresentation(double whiteNits, double peakNits, bool sdrAllowed)
        => SetHdrPresentation(whiteNits, peakNits, sdrAllowed, _hdrDisplayId);

    /// <summary>Updates the display configuration and retires the previous output's swap chain.</summary>
    public void SetHdrPresentation(double whiteNits, double peakNits, bool sdrAllowed, ulong displayId)
    {
        var color = PlaybackHdrColor.FromNits(whiteNits, peakNits);
        bool displayChanged = _hdrDisplayId != displayId;
        if (displayChanged || color != _hdrColor)
        {
            _hdrFailed = false;
            _hdrDeviceLostRetried = false;
        }
        _hdrDisplayId = displayId;
        _hdrColor = color;
        _sdrAllowed = sdrAllowed;
        UpdateCanvasPaused(replaceHdr: displayChanged);
    }

    /// <summary>Uses the SDR scene while the containing page is animating.</summary>
    public Task SetHdrNavigationTransitionAsync(bool active)
    {
        if (_navigationTransitionActive != active)
        {
            _navigationTransitionActive = active;
            UpdateCanvasPaused();
        }
        return _sdrHandoffCompletion?.Task ?? Task.CompletedTask;
    }

    /// <summary>Primes a current SDR frame while the UI prepares to reveal the page.</summary>
    public Task PrepareSdrNavigationFrameAsync()
    {
        _ = SetHdrNavigationTransitionAsync(true);
        if (CanRenderScene && _sdrAllowed && _canvas is { } canvas)
            BeginSdrHandoff(canvas, releaseDevice: false);
        return _sdrHandoffCompletion?.Task ?? Task.CompletedTask;
    }

    /// <summary>Keeps the completed HDR frame during the page's compositor animation.</summary>
    public long BeginHdrEntranceMotion()
    {
        _hdrEntranceAnimating = true;
        return ++_hdrEntranceMotionGeneration;
    }

    public void EndHdrEntranceMotion(long generation)
    {
        if (_hdrEntranceMotionGeneration == generation)
            _hdrEntranceAnimating = false;
    }

    public void RetryHdrPresentation()
    {
        _hdrFailed = false;
        _hdrDeviceLostRetried = false;
        UpdateCanvasPaused();
    }

    public void SetHdrTitle(SceneTitle? title, Rect region)
    {
        if (!ReferenceEquals(_sceneTitle, title))
            _sceneTitle?.SetScenePresentation(false);
        _sceneTitle = title;
        _sceneTitleRegion = region;
    }

    private bool CanRenderScene => !_hdrShuttingDown && IsLoaded && _isActive
        && !_pausedByParent && !_pausedByWindow && !_pausedByVisibility;

    private bool CanPresentHdr => CanRenderScene && !_navigationTransitionActive
        && _hdrColor.IsEnabled && !_hdrFailed && _hdrPanel is not null;

    private void RenderHdrFrame()
    {
        var panel = _hdrPanel;
        if (panel is null || !CanPresentHdr)
            return;

        var size = new Size(ActualWidth, ActualHeight);
        float dpi = (float)((XamlRoot?.RasterizationScale ?? 1) * 96d);
        if (size.Width * dpi < 96 || size.Height * dpi < 96)
            return;

        bool replace = _hdrSwapChain is null || _hdrTargetSize != size || _hdrTargetDpi != dpi;
        // Let a new size or DPI submit a complete replacement while the
        // compositor moves the existing frame.
        bool holdScene = !replace && _hdrPresented && _hdrEntranceAnimating;
        if ((holdScene && _hdrTitleLayer is null) || !TryTakeHdrFrame() ||
            !_hdrFrameClock.TryAdvance(Stopwatch.GetTimestamp(), _coordinator.TargetFrameRate, replace, out var elapsed))
            return;

        CanvasSwapChain? replacement = null;
        try
        {
            // Paused CanvasAnimatedControl can still finish or redraw a native SDR frame.
            // Its Present holds the Direct2D device lock after the managed callback returns.
            // Keep HDR rendering on its own device so the UI thread never needs that lock.
            var device = _hdrDevice ??= new CanvasDevice();
            var title = _sceneTitle;
            elapsed = elapsed > TimeSpan.FromMilliseconds(100) ? TimeSpan.FromMilliseconds(100) : elapsed;
            if (holdScene)
            {
                bool drewTitleLayer = DrawHdrTitleLayer(new PlaybackCanvasFrame(device, size, dpi, _hdrColor), title, elapsed);
                _hdrTitleLayer?.SetVisible(drewTitleLayer);
                title?.SetScenePresentation(drewTitleLayer);
                return;
            }

            if (replace)
                replacement = new CanvasSwapChain(device, (float)size.Width, (float)size.Height, dpi,
                    DirectXPixelFormat.R16G16B16A16Float, 2, CanvasAlphaMode.Ignore);

            var swapChain = replacement ?? _hdrSwapChain!;
            var frame = new PlaybackCanvasFrame(device, swapChain.Size, dpi, _hdrColor);
            bool drewLayer = DrawHdrTitleLayer(frame, title, elapsed);
            bool drewTitle, drewCover;
            using (var session = swapChain.CreateDrawingSession(Colors.Black))
            {
                // Direct2D may rasterize scaled effects into intermediate surfaces.
                session.EffectBufferPrecision = CanvasBufferPrecision.Precision16Float;
                if (!_isDark)
                {
                    _hdrClear ??= CanvasSolidColorBrush.CreateHdr(device, frame.Hdr.GetColor(Colors.White));
                    _hdrClear.ColorHdr = frame.Hdr.GetColor(Colors.White);
                    session.FillRectangle(new Rect(0, 0, frame.Size.Width, frame.Size.Height), _hdrClear);
                }

                drewCover = DrawHdrScene(frame, session, elapsed);

                DrawHdrForeground?.Invoke(session, frame.Hdr.Peak * 80f);
                drewTitle = drewLayer || title?.DrawInScene(session, frame, _sceneTitleRegion, elapsed) == true;
            }

            swapChain.Present(0);
            if (replacement is not null)
            {
                panel.SwapChain = replacement;
                var previous = _hdrSwapChain;
                _hdrSwapChain = replacement;
                replacement = null;
                _hdrTargetDpi = dpi;
                _hdrTargetSize = size;
                previous?.Dispose();
            }

            _hdrDeviceLostRetried = false;
            bool firstFrame = !_hdrPresented;
            if (firstFrame)
            {
                if (_canvas is { } canvas)
                    canvas.Opacity = 0;
                _hdrPresented = true;
                panel.Visibility = Visibility.Visible;
            }
            if (ReferenceEquals(title, _sceneTitle))
            {
                _hdrTitleLayer?.SetVisible(drewLayer);
                title?.SetScenePresentation(drewTitle);
            }
            CommitSceneCover(drewCover);
            HdrFramePresented?.Invoke();
            if (firstFrame)
                HdrPresentationChanged?.Invoke(true, false);
        }
        catch (Exception ex)
        {
            bool retry = !_hdrDeviceLostRetried && _hdrDevice is { } device && device.IsDeviceLost(ex.HResult);
            _hdrDeviceLostRetried = retry;
            _hdrFailed = !retry;
            replacement?.Dispose();
            replacement = null;
            StopHdrPresentation(releaseDevice: true);
            UpdateCanvasPaused();
            RaiseException(ex);
            HdrPresentationChanged?.Invoke(false, !retry);
            return;
        }
        finally
        {
            replacement?.Dispose();
        }
    }

    private bool DrawHdrTitleLayer(PlaybackCanvasFrame frame, SceneTitle? title, TimeSpan elapsed)
    {
        if (_hdrTitleLayerUnsupported || title?.CanDrawInScene(frame, _sceneTitleRegion) != true)
            return false;

        try
        {
            _hdrTitleLayer ??= new HdrTitleLayer(this, frame);
            return _hdrTitleLayer.Draw(title, frame, _sceneTitleRegion, elapsed);
        }
        catch (ArgumentException)
        {
            // The compositor can reject FP16 surfaces on some outputs. Retain the in-scene title there.
            _hdrTitleLayerUnsupported = true;
            _hdrTitleLayer?.Dispose();
            _hdrTitleLayer = null;
            return false;
        }
    }

    private bool TryTakeHdrFrame()
    {
        if (_hdrOwnsFrame)
            return true;
        if (!Monitor.TryEnter(_cacheGate))
            return false;
        _hdrOwnsFrame = true;
        Monitor.Exit(_cacheGate);
        return true;
    }

    private bool DrawHdrScene(PlaybackCanvasFrame frame, CanvasDrawingSession session, TimeSpan elapsed)
    {
        lock (_cacheGate)
        {
            if (!_rendererResourcesLoaded)
                LoadActiveResources();
            PrepareSceneResources(frame.Device);
            UpdateScene(frame.Device, elapsed);
            using var scope = CanvasDrawingSessionScope.Borrow(session);
            DrawScene(frame, scope.Context);
            return DrawSceneCover(frame, scope.Context);
        }
    }

    private void BeginSdrHandoff(CanvasAnimatedControl canvas, bool releaseDevice)
    {
        _releaseHdrAfterHandoff |= releaseDevice;
        if (_sdrHandoffClock is not null)
            return;

        _sdrCoverVisible = false;
        StopSdrCoverObservation();
        // Leave the last complete HDR image attached while the SDR game loop
        // builds a current frame behind it. This includes current lyric time.
        _hdrClock?.Dispose();
        _hdrClock = null;
        lock (_cacheGate)
        {
            if (_hdrOwnsFrame)
                ReleaseSceneResources();
            if (_canvasResourcesReady && !_rendererResourcesLoaded)
                LoadActiveResources();
            _hdrOwnsFrame = false;
        }

        _sceneTitle?.PrepareStandalonePresentation();
        _sdrFrameSubmitted = false;
        _sdrFrameObserved = false;
        _sdrHandoffStarted = Stopwatch.GetTimestamp();
        _sdrHandoffCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _sdrHandoffClock = FrameEventSubscription.SubscribeRendering(CompleteSdrHandoff);
        _sdrFrameEnabled = true;
        canvas.Paused = false;
    }

    private void CompleteSdrHandoff()
    {
        // A Draw callback precedes the native EndDraw/Present. Give it a
        // composition interval before exposing that swap chain to the user.
        if (!_sdrFrameObserved && Stopwatch.GetElapsedTime(_sdrHandoffStarted) < TimeSpan.FromMilliseconds(250))
        {
            _sdrFrameObserved = _sdrFrameSubmitted && _sceneTitle?.IsStandaloneFramePending != true;
            return;
        }

        if (_sdrFrameObserved)
            _sdrCoverPresented = _sdrCoverDrawn;
        bool stopped = StopHdrPresentation(_releaseHdrAfterHandoff);
        if (_canvas is { } canvas)
        {
            canvas.Opacity = 1;
            _sdrCoverVisible = _sdrFrameEnabled;
        }
        CommitSceneCover(_sdrCoverPresented);
        if (stopped)
            HdrPresentationChanged?.Invoke(false, false);
    }

    private void CancelSdrHandoff()
    {
        _sdrHandoffClock?.Dispose();
        _sdrHandoffClock = null;
        _releaseHdrAfterHandoff = false;
        var completion = _sdrHandoffCompletion;
        _sdrHandoffCompletion = null;
        completion?.TrySetResult();
    }

    private bool StopHdrPresentation(bool releaseDevice)
    {
        CancelSdrHandoff();
        _hdrClock?.Dispose();
        _hdrClock = null;
        _hdrFrameClock = default;
        _hdrEntranceAnimating = false;
        _hdrTitleLayer?.Dispose();
        _hdrTitleLayer = null;
        _hdrTitleLayerUnsupported = false;
        if (_hdrSwapChain is not null)
        {
            if (_hdrPanel is { } panel)
            {
                panel.Visibility = Visibility.Collapsed;
                panel.SwapChain = null;
            }
            _hdrSwapChain.Dispose();
            _hdrSwapChain = null;
        }

        if (_hdrOwnsFrame || (releaseDevice && _hdrDevice is not null))
        {
            lock (_cacheGate)
            {
                if (releaseDevice && (_hdrOwnsFrame || ReferenceEquals(_rendererDevice, _hdrDevice)))
                    ReleaseSceneResources();
                else if (_bgCacheHdr.IsEnabled)
                    ReleaseBackgroundCache();
                _hdrOwnsFrame = false;
            }
        }

        bool wasPresented = _hdrPresented;
        _hdrPresented = false;
        try { _sceneTitle?.SetScenePresentation(false); }
        finally
        {
            if (releaseDevice)
                ReleaseHdrDevice();
        }
        return wasPresented;
    }

    private void ReleaseHdrDevice()
    {
        var clear = _hdrClear;
        var device = _hdrDevice;
        _hdrClear = null;
        _hdrDevice = null;
        try
        {
            try
            {
                if (device is not null)
                {
                    lock (_cacheGate)
                        _sceneCover?.ReleaseDeviceResources(device);
                }
            }
            finally
            {
                clear?.Dispose();
                device?.Trim();
            }
        }
        catch (Exception ex) { RaiseException(ex); }
        finally { device?.Dispose(); }
    }

    private void DetachHdrPanel()
    {
        bool wasPresented = StopHdrPresentation(releaseDevice: true);
        _hdrPanel = null;
        if (wasPresented)
            HdrPresentationChanged?.Invoke(false, false);
    }
}
