using Microsoft.Graphics.Canvas;
using System.Numerics;
#nullable enable
using System;
using AnimatedWin2dControls.Interop;
using AnimatedWin2dControls.Renderer;
using Windows.Foundation;

namespace AnimatedWin2dControls.Controls;

public sealed partial class NowPlayingCanvas
{
    private PlaybackSpectrumOptions _spectrumOptions = new();
    private IPlaybackSpectrumSource? _spectrumSource;
    private IPlaybackSpectrumSource? _activeSpectrumSource;
    private PlaybackSpectrumRenderer? _spectrum;

    public PlaybackSpectrumOptions SpectrumOptions
    {
        get => _spectrumOptions;
        set
        {
            lock (_cacheGate)
            {
                _spectrumOptions = value.Normalize();
                SyncSpectrumSource();
            }
        }
    }

    public IPlaybackSpectrumSource? SpectrumSource
    {
        get => _spectrumSource;
        set
        {
            if (ReferenceEquals(_spectrumSource, value)) return;
            _spectrumSource = value;
            SyncSpectrumSource();
        }
    }

    private void SyncSpectrumSource()
    {
        if (_spectrumSource is null && _activeSpectrumSource is null) return;
        bool enabled = _spectrumOptions.Enabled && _spectrumOptions.Opacity > 0 && CanRenderScene && !_useImageBackground;
        SetActiveSpectrumSource(enabled ? _spectrumSource : null);
    }

    private void SetActiveSpectrumSource(IPlaybackSpectrumSource? source)
    {
        if (ReferenceEquals(_activeSpectrumSource, source)) return;
        lock (_cacheGate)
        {
            _activeSpectrumSource?.SetActive(false);
            _activeSpectrumSource = source;
            _spectrum?.Dispose();
            _spectrum = null;
            source?.SetActive(true);
        }
    }

    private void UpdateSpectrum(TimeSpan elapsed)
    {
        if (_activeSpectrumSource is not { } source) return;
        if (!source.TryRead(out var latest)) latest = default;
        (_spectrum ??= new()).Update(latest, Environment.TickCount64, elapsed, _spectrumOptions);
    }

    private void DrawProjectedSpectrum(PlaybackCanvasFrame frame, CanvasDrawContext destination, Matrix4x4 projection)
    {
        if (_spectrum is not { } spectrum) return;
        Rect artwork = default;
        float radius = 0f;
        if (_spectrumOptions.Placement == SpectrumPlacement.AroundArtwork)
        {
            if (_sceneCover?.TryGetArtworkBounds(out artwork, out radius) != true) return;
        }
        if (projection.IsIdentity)
        {
            spectrum.Draw(frame, destination, _spectrumOptions, artwork, radius, _lastPalette, _isDark);
            return;
        }

        using var commands = new CanvasCommandList(frame.Device);
        using (var drawing = commands.CreateDrawingSession())
        {
            if (frame.Hdr.IsEnabled)
                drawing.EffectBufferPrecision = CanvasBufferPrecision.Precision16Float;
            using var source = CanvasDrawingSessionScope.Borrow(drawing);
            spectrum.Draw(frame, source.Context, _spectrumOptions, artwork, radius, _lastPalette, _isDark);
        }

        DrawProjectedImage(destination, commands, projection, frame.Hdr.IsEnabled, ref _foregroundProjectionEffect);
    }
}
