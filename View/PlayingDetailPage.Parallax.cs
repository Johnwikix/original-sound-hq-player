using System;
using System.Diagnostics;
using System.Numerics;
using AnimatedWin2dControls.Controls;
using AnimatedWin2dControls.Interop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Media3D;

namespace WinUIMusicPlayer.View;

public sealed partial class PlayingDetailPage
{
    private const float AutomaticTiltAngle = 10;
    private const float AutomaticTiltDepth = 800;
    private FrameEventSubscription? _spatialClock;
    private long _spatialTimestamp;
    private Vector2? _spatialPointer;
    private Vector3 _spatialAutomaticAngles;
    private Matrix4x4 _spatialProjection = Matrix4x4.Identity;
    private bool _spatialWindowPaused;
    private bool _spatialPagePaused;
    private bool _trackingSpatialLayout;
    private FrameworkElement? _spatialTitleAnchor;
    private FrameworkElement? _spatialTitleContent;
    private Matrix3DProjection? _spatialCoverProjection;
    private Matrix3DProjection? _spatialTitleProjection;
    private bool _spatialCoverProjected;
    private bool _spatialTitleProjected;
    private Vector2 _spatialSceneOrigin;
    private Vector2 _spatialLyricsOrigin;
    private Vector2 _spatialCoverOrigin;
    private Vector2 _spatialTitleOrigin;
    private Vector2 _spatialContentCenter;
    private Vector2 _spatialContentHalfExtents;

    private void PlaybackParallaxPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var settings = ViewModel.AppViewModel;
        if (!settings.Is3DLyricsEnabled || !settings.IsAuto3DLyricsEnabled || ActualWidth <= 0 || ActualHeight <= 0)
            return;

        using var point = PointerPointScope.GetCurrentPoint(e, this);
        var position = point.Position;
        bool inside = position.X >= 0 && position.X < ActualWidth && position.Y >= 0 && position.Y < ActualHeight;
        SetSpatialPointer(inside
            ? new Vector2((float)(2 * position.X / ActualWidth - 1), (float)(2 * position.Y / ActualHeight - 1))
            : null);
    }

    private void PlaybackParallaxPointerExited(object sender, PointerRoutedEventArgs e)
    {
        // An exit from a child does not necessarily leave the page.
        PlaybackParallaxPointerMoved(sender, e);
    }

    private void SetSpatialPointer(Vector2? position)
    {
        if (_spatialPointer == position) return;
        _spatialPointer = position;
        RefreshSpatialProjection();
    }

    private void RefreshSpatialProjection()
    {
        if (!_isLoaded || _disposed) return;

        var settings = ViewModel.AppViewModel;
        SetSpatialLayoutTracking(settings.Is3DLyricsEnabled);
        if (!settings.Is3DLyricsEnabled || !settings.IsAuto3DLyricsEnabled)
        {
            StopSpatialClock();
            _spatialPointer = null;
            _spatialAutomaticAngles = Vector3.Zero;
            UpdateSpatialProjection();
            return;
        }

        if (_spatialWindowPaused || _spatialPagePaused || !settings.IsPlayingDetailVisible)
        {
            StopSpatialClock();
            _spatialPointer = null;
            return;
        }

        if (_spatialAutomaticAngles == GetAutomaticSpatialTarget())
        {
            UpdateSpatialProjection();
            return;
        }
        if (_spatialClock is null)
        {
            _spatialTimestamp = Stopwatch.GetTimestamp();
            _spatialClock = FrameEventSubscription.SubscribeRendering(AdvanceSpatialProjection);
        }
    }

    private Vector3 GetAutomaticSpatialTarget()
        => _spatialPointer is { } pointer
            ? new Vector3(pointer.Y * AutomaticTiltAngle, -pointer.X * AutomaticTiltAngle, 0)
            : Vector3.Zero;

    private void AdvanceSpatialProjection()
    {
        long now = Stopwatch.GetTimestamp();
        double elapsed = Stopwatch.GetElapsedTime(_spatialTimestamp, now).TotalSeconds;
        _spatialTimestamp = now;

        var target = GetAutomaticSpatialTarget();
        float amount = 1 - MathF.Pow(0.85f, (float)elapsed * 60);
        _spatialAutomaticAngles = Vector3.DistanceSquared(_spatialAutomaticAngles, target) < 0.0001f
            ? target
            : Vector3.Lerp(_spatialAutomaticAngles, target, amount);
        UpdateSpatialProjection();

        if (_spatialAutomaticAngles == target) StopSpatialClock();
    }

    private void StopSpatialClock()
    {
        var clock = _spatialClock;
        _spatialClock = null;
        clock?.Dispose();
    }

    private void DetachSpatialProjection()
    {
        StopSpatialClock();
        SetSpatialLayoutTracking(false);
        _spatialPointer = null;
        _spatialAutomaticAngles = Vector3.Zero;
        _spatialProjection = Matrix4x4.Identity;
        PublishSpatialProjection();
    }

    private void SetSpatialLayoutTracking(bool enabled)
    {
        if (_trackingSpatialLayout == enabled) return;
        _trackingSpatialLayout = enabled;
        if (enabled)
        {
            PlayingDetail.LayoutUpdated += SpatialLayoutUpdated;
            UpdateSpatialLayout(force: true);
        }
        else
        {
            PlayingDetail.LayoutUpdated -= SpatialLayoutUpdated;
        }
    }

    private void SpatialLayoutUpdated(object? sender, object e) => UpdateSpatialLayout();

    private void UpdateSpatialLayout(bool force = false)
    {
        if (!_isLoaded || _disposed) return;
        if (!_trackingSpatialLayout)
        {
            // A lazily loaded canvas still needs an explicit flat override.
            if (force) PublishSpatialProjection();
            return;
        }

        var contentOrigin = GetSpatialOrigin(PlayingDetail);
        var sceneOrigin = GetSpatialOrigin(NowPlaying);
        var lyricsOrigin = GetSpatialOrigin(LyricsView);
        var coverOrigin = GetSpatialOrigin(CoverSpatialAnchor);
        var titleOrigin = GetSpatialOrigin(_spatialTitleAnchor);
        var halfExtents = new Vector2((float)PlayingDetail.ActualWidth, (float)PlayingDetail.ActualHeight) / 2;
        var center = contentOrigin + halfExtents;

        if (!force && center == _spatialContentCenter && halfExtents == _spatialContentHalfExtents
            && sceneOrigin == _spatialSceneOrigin
            && lyricsOrigin == _spatialLyricsOrigin && coverOrigin == _spatialCoverOrigin && titleOrigin == _spatialTitleOrigin)
            return;

        _spatialContentCenter = center;
        _spatialContentHalfExtents = halfExtents;
        _spatialSceneOrigin = sceneOrigin;
        _spatialLyricsOrigin = lyricsOrigin;
        _spatialCoverOrigin = coverOrigin;
        _spatialTitleOrigin = titleOrigin;

        UpdateSpatialProjection(force: true);
    }

    private Vector2 GetSpatialOrigin(FrameworkElement? element)
    {
        if (element is not { IsLoaded: true }) return Vector2.Zero;
        var origin = element.TransformToVisual(this).TransformPoint(default);
        return new Vector2((float)origin.X, (float)origin.Y);
    }

    private void UpdateSpatialProjection(bool force = false)
    {
        var settings = ViewModel.AppViewModel;
        var projection = Matrix4x4.Identity;
        if (settings.Is3DLyricsEnabled && _spatialContentHalfExtents.X > 0 && _spatialContentHalfExtents.Y > 0)
        {
            bool automatic = settings.IsAuto3DLyricsEnabled;
            var angles = automatic
                ? _spatialAutomaticAngles
                : new Vector3(settings.Lyrics3DXAngle, settings.Lyrics3DYAngle, settings.Lyrics3DZAngle);
            float depth = automatic ? AutomaticTiltDepth : settings.Lyrics3DDepth;
            var projectionBounds = _spatialContentHalfExtents;
            if (settings.IsFanLyricsEnabled)
            {
                // The lyric source clip can extend by max(layout width, layout height)
                // past each horizontal side before perspective brings glyphs into view.
                float maxLyricsWidth = _isPortraitLayout ? 2 * projectionBounds.X : projectionBounds.X;
                projectionBounds.X += Math.Max(maxLyricsWidth, 2 * projectionBounds.Y);
            }
            projection = PlaybackSpatialProjection.CreatePerspective(_spatialContentCenter, angles, depth, projectionBounds);
        }

        if (!force && projection == _spatialProjection) return;
        _spatialProjection = projection;
        PublishSpatialProjection();
    }

    private void PublishSpatialProjection()
    {
        var projection = _spatialProjection;
        NowPlaying?.SetPlaybackSpatialProjection(PlaybackSpatialProjection.ToLocal(projection, _spatialSceneOrigin));
        LyricsView?.SetPlaybackSpatialProjection(PlaybackSpatialProjection.ToLocal(projection, _spatialLyricsOrigin));

        // The flat anchors stay available for Win2D layout. Only the XAML presentation
        // is projected, and the existing frame handoff controls when it is visible.
        ApplySpatialVisual(DetailCoverSwitcher, ref _spatialCoverProjection, ref _spatialCoverProjected,
            PlaybackSpatialProjection.ToLocal(projection, _spatialCoverOrigin));
        if (_spatialTitleContent is { } title)
        {
            ApplySpatialVisual(title, ref _spatialTitleProjection, ref _spatialTitleProjected,
                PlaybackSpatialProjection.ToLocal(projection, _spatialTitleOrigin));
        }
    }

    private static void ApplySpatialVisual(
        FrameworkElement element, ref Matrix3DProjection? projection, ref bool projected, Matrix4x4 matrix)
    {
        if (matrix.IsIdentity)
        {
            if (projected)
            {
                element.Projection = null;
                projected = false;
            }
            return;
        }

        projection ??= new Matrix3DProjection();
        projection.ProjectionMatrix = new Matrix3D
        {
            M11 = matrix.M11, M12 = matrix.M12, M13 = matrix.M13, M14 = matrix.M14,
            M21 = matrix.M21, M22 = matrix.M22, M23 = matrix.M23, M24 = matrix.M24,
            M31 = matrix.M31, M32 = matrix.M32, M33 = matrix.M33, M34 = matrix.M34,
            OffsetX = matrix.M41, OffsetY = matrix.M42, OffsetZ = matrix.M43, M44 = matrix.M44,
        };
        if (!projected)
        {
            element.Projection = projection;
            projected = true;
        }
    }
}
