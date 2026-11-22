using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.Advance;
using Microsoft.Graphics.Canvas.Effects;
using System;
using System.Numerics;
using Windows.Foundation;

namespace AnimatedWin2dControls.Controls.AnimatedLyricsLineControl;

public sealed partial class LyricsRenderCoordinator
{
    private Transform3DEffect? _lyricsPerspectiveEffect;
    private Matrix4x4 _lyricsProjection = Matrix4x4.Identity;
    private Matrix4x4 _inverseLyricsProjection = Matrix4x4.Identity;
    private bool _hasInverseLyricsProjection = true;
    private Vector3 _automaticTilt;
    private bool _hasExternalParallaxInput;
    private Vector2? _externalParallaxPointer;
    private int _fanAnimationVersion;

    internal void SetLyricsParallaxPointer(Vector2? normalizedPosition)
    {
        lock (_pendingGate)
        {
            _hasExternalParallaxInput = true;
            _externalParallaxPointer = normalizedPosition;
        }
    }

    private Vector2? GetLyricsParallaxPointer()
    {
        lock (_pendingGate)
        {
            if (_hasExternalParallaxInput) return _externalParallaxPointer;
        }

        if (!_isPointerOnSurface || !IsPointerInLyricsRegion(_lastMousePos)) return null;

        return new Vector2((float)((_lastMousePos.X - RegionX) / RegionW * 2 - 1),
            (float)((_lastMousePos.Y - RegionY) / RegionH * 2 - 1));
    }

    private bool HasTransformedLyrics => _cachedIsFanLyricsEnabled || _cachedIs3DLyricsEnabled;

    private Rect GetLyricsPaintBounds(Rect layout)
    {
        if (!HasTransformedLyrics) return layout;
        if (LyricsViewport.Width > 0 && LyricsViewport.Height > 0) return LyricsViewport;

        var host = HostSize;
        return host.Width > 0 && host.Height > 0
            ? new Rect(0, 0, host.Width, host.Height)
            : layout;
    }

    private Rect GetLyricsSourceBounds(Rect layout, Rect viewport)
    {
        // A combined tilt can bring glyphs rotated beyond the window's side
        // back into view. Keep that source overhang in the command list only.
        double overhang = !_lyricsProjection.IsIdentity && _cachedIsFanLyricsEnabled
            ? Math.Max(layout.Width, layout.Height)
            : 0;
        return new Rect(viewport.X - overhang, layout.Y, viewport.Width + 2 * overhang, layout.Height);
    }

    private static bool Contains(Rect bounds, Point point)
        => point.X >= bounds.Left && point.X < bounds.Right && point.Y >= bounds.Top && point.Y < bounds.Bottom;

    private bool UpdateLyricsProjection(TimeSpan elapsedTime)
    {
        var projection = Matrix4x4.Identity;
        if (_cachedIs3DLyricsEnabled && RegionW > 0 && RegionH > 0)
        {
            Vector3 angles;
            float depth;
            if (_cachedIsAuto3DLyricsEnabled)
            {
                var pointer = GetLyricsParallaxPointer();
                var target = pointer is { } position
                    ? new Vector3(position.Y * 10, -position.X * 10, 0)
                    : Vector3.Zero;
                float amount = 1 - MathF.Pow(0.85f, (float)elapsedTime.TotalSeconds * 60);
                _automaticTilt = Vector3.DistanceSquared(_automaticTilt, target) < 0.0001f
                    ? target
                    : Vector3.Lerp(_automaticTilt, target, amount);
                angles = _automaticTilt;
                depth = 800;
            }
            else
            {
                _automaticTilt = Vector3.Zero;
                angles = new Vector3(_cachedLyrics3DXAngle, _cachedLyrics3DYAngle, _cachedLyrics3DZAngle);
                depth = _cachedLyrics3DDepth;
            }

            var pivot = new Vector2((float)(RegionX + RegionW / 2),
                (float)(RegionY + RegionH * _cachedPlayingLineTopOffset));
            projection = LyricsViewportTransform.CreatePerspective(pivot, angles, depth);
        }
        else
        {
            _automaticTilt = Vector3.Zero;
        }

        if (projection == _lyricsProjection) return false;
        _lyricsProjection = projection;
        _hasInverseLyricsProjection = LyricsViewportTransform.TryInvertPerspective(projection, out _inverseLyricsProjection);
        return true;
    }

    private void ReleaseLyricsPerspective()
    {
        _lyricsPerspectiveEffect?.Dispose();
        _lyricsPerspectiveEffect = null;
    }

    private (int Start, int End) CalculateVisibleLyricsRange(double scroll, double height, double anchor)
    {
        double top = 0;
        double bottom = height;
        if (_cachedIsFanLyricsEnabled)
        {
            // The lyric plane keeps its original height. Include rows whose
            // rotated bounds can reach it from above or below.
            double fanOverhang = RegionW * 1.5;
            top -= fanOverhang;
            bottom += fanOverhang;
        }

        return LyricsLayoutManager.CalculateVisibleRange(_renderLines, scroll, height * anchor - top, 0,
            bottom - top, 0, UsesLineScroll, _mouseYScrollTransition.Value);
    }

    private void UpdateFanAngles(int first, int last, double height, double anchor, LyricScrollTiming? timing,
        TimeSpan elapsedTime, bool jump)
    {
        _fanAnimationVersion++;
        if ((uint)_currentLineIndex >= (uint)_renderLines.Count) return;

        var playing = _renderLines[_currentLineIndex];
        for (int index = first; index <= last; index++)
        {
            var line = _renderLines[index];
            if (line.PrimaryTextLayout is null) continue;
            double distance = line.TopLeftPosition.Y - playing.TopLeftPosition.Y;
            bool isFocused = index == _currentLineIndex;
            double target = isFocused || _userScrolling ? 0
                : LyricsViewportTransform.GetFanAngle(_cachedFanLyricsAngle, distance,
                    height * (index < _currentLineIndex ? anchor : 1 - anchor));
            var transition = line.FanAngleTransition ??= new(0, _canvasYScrollTransition.Interpolator);
            // Focus changes must put the new line flat immediately. Scroll
            // motion and the optional whole-plane tilt keep their own timing.
            if (isFocused || jump || line.FanAngleAnimationVersion != _fanAnimationVersion - 1)
            {
                transition.JumpTo(target);
            }
            else if (target != line.FanAngleTarget)
            {
                // The active keyframe can still be a hold at the starting angle.
                // Retarget only when the requested destination has changed.
                var (duration, delay) = timing?.GetLineTiming(index)
                    ?? (_canvasYScrollTransition.DurationSeconds, 0);
                transition.SetDuration(duration);
                transition.SetDelay(delay);
                transition.SetInterpolator(_canvasYScrollTransition.Interpolator);
                if (target == transition.Value)
                    transition.JumpTo(target);
                else
                    transition.Start(target);
            }

            line.FanAngleTarget = target;
            transition.Update(elapsedTime);
            line.FanAngleAnimationVersion = _fanAnimationVersion;
        }
    }

    private Matrix3x2 CreateLineTransform(RenderLyricsLine line, double regionX, double lineY, double regionWidth)
    {
        float angle = _cachedIsFanLyricsEnabled ? (float)(line.FanAngleTransition?.Value ?? 0) : 0;
        return LyricsViewportTransform.CreateLine(line.CenterPosition, (float)line.ScaleTransition.Value,
            angle, (float)regionWidth, _cachedFanLyricsAngle < 0, new Vector2((float)regionX, (float)lineY));
    }

    private int FindHoveredLyricsLine(Point pointer, double combinedScroll, double height, double anchor)
    {
        if (!HasTransformedLyrics)
        {
            if (!IsPointerInLyricsRegion(pointer)) return -1;
            var local = new Point(pointer.X - RegionX, pointer.Y - RegionY);
            return LyricsLayoutManager.FindMouseHoverLineIndex(_renderLines, true, local, combinedScroll,
                height, anchor, UsesLineScroll, _mouseYScrollTransition.Value);
        }

        if (!_hasInverseLyricsProjection || _cachedVisibleStart < 0) return -1;

        var layout = new Rect(RegionX, RegionY, RegionW, height);
        var viewport = GetLyricsPaintBounds(layout);
        if (!Contains(viewport, pointer)) return -1;

        var location = new Vector2((float)pointer.X, (float)pointer.Y);
        if (!LyricsViewportTransform.TryProject(location, _inverseLyricsProjection, out var source)
            || !Contains(GetLyricsSourceBounds(layout, viewport), new Point(source.X, source.Y)))
            return -1;

        int first = Math.Max(0, _cachedVisibleStart - 3);
        int last = Math.Min(_renderLines.Count - 1, _cachedVisibleEnd + 3);
        double lineOrigin = RegionY + height * anchor;
        for (int index = last; index >= first; index--)
        {
            var line = _renderLines[index];
            if (line.PrimaryTextLayout?.LayoutBounds.Width is not > 0) continue;
            double lineY = lineOrigin + (UsesLineScroll
                ? line.ScrollMotion.Value + _mouseYScrollTransition.Value
                : combinedScroll);
            if (!Matrix3x2.Invert(CreateLineTransform(line, RegionX, lineY, RegionW), out var inverse)) continue;
            var point = Vector2.Transform(source, inverse);
            if (point.X >= line.TopLeftPosition.X && point.X <= line.BottomRightPosition.X
                && point.Y >= line.TopLeftPosition.Y && point.Y <= line.BottomRightPosition.Y)
                return index;
        }
        return -1;
    }
}
