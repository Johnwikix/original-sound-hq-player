#nullable enable
using System;
using System.Numerics;
using System.Threading;
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
    private const long NoSpectrumPointer = long.MaxValue;
    private long _spectrumPointerBits = NoSpectrumPointer;

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

    /// <summary>Updates the pointer position in canvas DIPs for the spectrum's optional tilt.</summary>
    public void SetSpectrumPointer(Point? position)
    {
        if (!_spectrumOptions.Parallax) return;
        long bits = position is { } point
            ? (long)BitConverter.SingleToInt32Bits((float)point.X) << 32 | (uint)BitConverter.SingleToInt32Bits((float)point.Y)
            : NoSpectrumPointer;
        Volatile.Write(ref _spectrumPointerBits, bits);
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
            Volatile.Write(ref _spectrumPointerBits, NoSpectrumPointer);
            source?.SetActive(true);
        }
    }

    private void UpdateSpectrum(TimeSpan elapsed)
    {
        if (_activeSpectrumSource is not { } source) return;
        source.TryRead(out var latest);
        (_spectrum ??= new()).Update(latest, Environment.TickCount64, elapsed, _spectrumOptions);
    }

    private void DrawSpectrum(PlaybackCanvasFrame frame, CanvasDrawContext session)
    {
        if (_spectrum is not { } spectrum) return;
        Rect artwork = default;
        float radius = 0f;
        if (_spectrumOptions.Placement == SpectrumPlacement.AroundArtwork)
        {
            if (_sceneCover?.TryGetArtworkBounds(out artwork, out radius) != true) return;
        }
        long bits = Volatile.Read(ref _spectrumPointerBits);
        Vector2? pointer = bits == NoSpectrumPointer ? null
            : new Vector2(BitConverter.Int32BitsToSingle((int)(bits >> 32)), BitConverter.Int32BitsToSingle((int)bits));
        spectrum.Draw(frame, session, _spectrumOptions, artwork, radius, _lastPalette, _isDark, pointer);
    }
}
