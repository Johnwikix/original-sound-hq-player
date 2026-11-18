using AnimatedWin2dControls.Controls;
using Microsoft.Extensions.Logging;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
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
    private PlaybackSceneCover? _hdrArtwork;
    private bool _includeSdrCovers;
    private PlaybackSceneCoverDrawing? _hdrDrawing;
    private Rect _hdrCoverBounds;
    private long _coverBlendStarted;
    private double _previewOpacity = 1;
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
        ViewModel.AppViewModel.PropertyChanged -= OnHdrCoverChanged;
        ClearHdrCover();
    }

    private void AttachHdrCoverCanvas(NowPlayingCanvas canvas)
    {
        if (ReferenceEquals(canvas, _hdrCoverCanvas)) return;
        if (_hdrCoverCanvas is not null) _hdrCoverCanvas.DrawHdrForeground -= DrawHdrCover;
        _hdrCoverCanvas = canvas;
        canvas.DrawHdrForeground += DrawHdrCover;
        UpdateHdrCoverBounds();
    }

    private void OnHdrCoverChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(AppViewModel.LyricPageBackgroundHash) or nameof(AppViewModel.PlaybackHdrDisplayAvailable)
            or nameof(AppViewModel.PlaybackHdrEnabled) or nameof(AppViewModel.IsPlayingDetailVisible)
            or nameof(AppViewModel.IsFluidBackgroundEnabled) or nameof(AppViewModel.WindowBackgroundImagePath))
            RefreshHdrCover();
    }

    private void RefreshHdrCover()
    {
        var vm = ViewModel.AppViewModel;
        string? hash = vm.PlaybackHdrEnabled && vm.PlaybackHdrDisplayAvailable && vm.IsPlayingDetailVisible &&
            (vm.IsFluidBackgroundEnabled || !string.IsNullOrWhiteSpace(vm.WindowBackgroundImagePath))
            ? vm.LyricPageBackgroundHash : null;
        if (string.IsNullOrWhiteSpace(hash)) hash = null;
        bool includeSdr = vm.IsFluidBackgroundEnabled;
        if (_hdrCoverHash == hash && (_includeSdrCovers == includeSdr || hash is null)) return;
        ClearHdrCover();
        if (hash is null) return;
        _hdrCoverHash = hash;
        _includeSdrCovers = includeSdr;
        _hdrCoverRequest = new CancellationTokenSource();
        _ = LoadHdrCoverAsync(hash, includeSdr, _hdrCoverRequest);
    }

    private async Task LoadHdrCoverAsync(string hash, bool includeSdr, CancellationTokenSource request)
    {
        try
        {
            var artwork = await Task.Run(() => PlaybackSceneCover.ReadAsync(ToolUtils.FindRawCachePath(hash),
                includeSdr, request.Token), request.Token);
            if (artwork is null || request.IsCancellationRequested || !ReferenceEquals(_hdrCoverRequest, request)) return;
            _hdrArtwork = artwork;
            DetailCoverSwitcher.LayoutUpdated += OnHdrCoverLayoutUpdated;
            UpdateHdrCoverBounds();
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (Exception ex) { _logger.LogDebug(ex, "Unable to decode HDR album artwork"); }
        finally
        {
            if (ReferenceEquals(_hdrCoverRequest, request)) _hdrCoverRequest = null;
            request.Dispose();
        }
    }

    private void OnHdrCoverLayoutUpdated(object? sender, object e) => UpdateHdrCoverBounds();

    private void UpdateHdrCoverBounds()
    {
        if (_hdrArtwork is not { } art || _hdrCoverCanvas is null || !_coverAttached ||
            !DetailCoverSwitcher.IsLoaded || !_hdrCoverCanvas.IsLoaded) return;
        var slot = DetailCoverSwitcher.GetArtworkBounds(_hdrCoverCanvas);
        double scale = Math.Min(slot.Width / art.Width, slot.Height / art.Height);
        double w = art.Width * scale, h = art.Height * scale;
        var rect = new Rect(slot.X + (slot.Width - w) * .5, slot.Y + (slot.Height - h) * .5, w, h);
        _hdrCoverBounds = rect;
    }

    private void DrawHdrCover(CanvasDrawingSession session, float peakNits)
    {
        var rect = _hdrCoverBounds;
        if (_hdrArtwork is not { } art || _hdrCoverHash is null || rect.Width <= 0 || rect.Height <= 0 ||
            !DetailCoverSwitcher.IsCoverSettled(_hdrCoverHash)) return;
        if (_hdrDrawing is null || !ReferenceEquals(_hdrDrawing.Device, session.Device))
        {
            ReleaseHdrCoverTexture();
            _hdrDrawing = new PlaybackSceneCoverDrawing(art, session.Device);
        }
        float radius = (float)(Math.Min(rect.Width, rect.Height) * DetailCoverSwitcher.CornerRadiusAmount / 200);
        _hdrDrawing.Draw(session, rect, radius, (float)ViewModel.AppViewModel.PlaybackHdrWhiteNits, peakNits);

        // Keep the settled SDR copy in layout. It provides the original shadow and immediate fallback.
        if (_previewOpacity > 0 && ViewModel.AppViewModel.PlaybackHdrActive)
        {
            if (_coverBlendStarted == 0) _coverBlendStarted = Stopwatch.GetTimestamp();
            double elapsed = Stopwatch.GetElapsedTime(_coverBlendStarted).TotalMilliseconds;
            _previewOpacity = Math.Max(0, 1 - elapsed / Constants.Time.AnimationDuration.TotalMilliseconds);
            DetailCoverSwitcher.SetArtworkOpacity(_previewOpacity);
        }
    }

    private void RestoreCoverPreview()
    {
        if (_previewOpacity != 1) DetailCoverSwitcher.SetArtworkOpacity(1);
        _previewOpacity = 1;
        _coverBlendStarted = 0;
    }

    private void ReleaseHdrCoverTexture()
    {
        _hdrDrawing?.Dispose();
        _hdrDrawing = null;
    }

    private void ClearHdrCover()
    {
        _hdrCoverRequest?.Cancel();
        _hdrCoverRequest = null;
        _hdrCoverHash = null;
        if (_hdrArtwork is not null) DetailCoverSwitcher.LayoutUpdated -= OnHdrCoverLayoutUpdated;
        _hdrArtwork = null;
        _hdrCoverBounds = default;
        RestoreCoverPreview();
        ReleaseHdrCoverTexture();
    }

    private void DisposeHdrCover()
    {
        Loaded -= OnHdrCoverPageLoaded;
        Unloaded -= OnHdrCoverPageUnloaded;
        DetachHdrCover();
        if (_hdrCoverCanvas is not null) _hdrCoverCanvas.DrawHdrForeground -= DrawHdrCover;
        _hdrCoverCanvas = null;
    }
}
