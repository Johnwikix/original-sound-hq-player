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
    private long _lastHdrTick;
    private bool _sdrAllowed = true;
    private volatile bool _sdrFrameEnabled;
    private FrameEventSubscription? _hdrClock;
    private volatile bool _hdrOwnsFrame;
    private bool _hdrPresented;
    private bool _hdrFailed;
    private bool _hdrDeviceLostRetried;
    private bool _hdrShuttingDown;
    private SceneTitle? _sceneTitle;
    private Rect _sceneTitleRegion;

    public event Action<bool, bool>? HdrPresentationChanged;

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
        _sceneTitle?.SetScenePresentation(_hdrPresented);
    }

    private bool CanRenderScene => !_hdrShuttingDown && IsLoaded && _isActive
        && !_pausedByParent && !_pausedByWindow && !_pausedByVisibility;

    private bool CanPresentHdr => CanRenderScene && _hdrColor.IsEnabled && !_hdrFailed && _hdrPanel is not null;

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
        long now = Stopwatch.GetTimestamp();
        TimeSpan elapsed = _lastHdrTick == 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(_lastHdrTick, now);
        double minInterval = 1d / _coordinator.TargetFrameRate;
        if (!replace && elapsed.TotalSeconds < minInterval - 0.002)
            return;

        if (!TryTakeHdrFrame())
            return;

        CanvasSwapChain? replacement = null;
        try
        {
            // Paused CanvasAnimatedControl can still finish or redraw a native SDR frame.
            // Its Present holds the Direct2D device lock after the managed callback returns.
            // Keep HDR rendering on its own device so the UI thread never needs that lock.
            var device = _hdrDevice ??= new CanvasDevice();
            if (replace)
                replacement = new CanvasSwapChain(device, (float)size.Width, (float)size.Height, dpi,
                    DirectXPixelFormat.R16G16B16A16Float, 2, CanvasAlphaMode.Ignore);

            var swapChain = replacement ?? _hdrSwapChain!;
            var frame = new PlaybackCanvasFrame(device, swapChain.Size, dpi, _hdrColor);
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

                elapsed = elapsed > TimeSpan.FromMilliseconds(100) ? TimeSpan.FromMilliseconds(100) : elapsed;
                DrawHdrScene(frame, session, elapsed);

                DrawHdrForeground?.Invoke(session, frame.Hdr.Peak * 80f);
                _sceneTitle?.DrawInScene(session, frame, _sceneTitleRegion, elapsed);
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

            _lastHdrTick = now;
            _hdrDeviceLostRetried = false;
        }
        catch (Exception ex)
        {
            bool retry = !_hdrDeviceLostRetried && _hdrDevice is { } device && device.IsDeviceLost(ex.HResult);
            _hdrDeviceLostRetried = retry;
            _hdrFailed = !retry;
            replacement?.Dispose();
            replacement = null;
            bool wasPresented = StopHdrPresentation(releaseDevice: true);
            UpdateCanvasPaused();
            RaiseException(ex);
            if (wasPresented || !retry)
                HdrPresentationChanged?.Invoke(false, !retry);
            return;
        }
        finally
        {
            replacement?.Dispose();
        }

        if (!_hdrPresented)
        {
            _hdrPresented = true;
            panel.Visibility = Visibility.Visible;
            _sceneTitle?.SetScenePresentation(true);
            HdrPresentationChanged?.Invoke(true, false);
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

    private void DrawHdrScene(PlaybackCanvasFrame frame, CanvasDrawingSession session, TimeSpan elapsed)
    {
        lock (_cacheGate)
        {
            if (!_rendererResourcesLoaded)
                LoadActiveResources();
            PrepareSceneResources(frame.Device);
            UpdateScene(frame.Device, elapsed);
            using var scope = CanvasDrawingSessionScope.Borrow(session);
            DrawScene(frame, scope.Context);
        }
    }

    private bool StopHdrPresentation(bool releaseDevice)
    {
        _hdrClock?.Dispose();
        _hdrClock = null;
        _lastHdrTick = 0;
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
            clear?.Dispose();
            device?.Trim();
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
