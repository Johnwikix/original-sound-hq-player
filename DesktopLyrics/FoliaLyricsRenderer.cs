using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.Text;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.DesktopLyrics;

/// <summary>
/// Fume-inspired local wallpaper renderer. The scene is an article of lyric blocks,
/// not a centered line over a full-screen shader: the camera follows the active block,
/// while old and upcoming blocks remain in the world and fade at different rates.
/// </summary>
internal sealed class FoliaLyricsRenderer : IDesktopLyricsRenderer
{
    private const double SyncThresholdMs = 500;
    private const double PassedHoldMs = 12000;
    private const int StarCount = 150;
    private const int GeometryCount = 20;
    private static readonly Color FumeGold = Color.FromArgb(0xFF, 0xE7, 0xB7, 0x6A);

    private readonly CanvasAnimatedControl _canvas = new() { ClearColor = Colors.Transparent };
    private readonly List<LineVisual> _lines = [];
    private readonly List<StarVisual> _stars = [];
    private readonly List<GeometryVisual> _geometry = [];

    private CanvasSolidColorBrush? _textBrush;
    private CanvasSolidColorBrush? _accentBrush;
    private CanvasSolidColorBrush? _glowBrush;
    private CanvasTextLayout? _auxiliaryLayout;
    private string _auxiliaryText = string.Empty;

    private List<LyricLine>? _lyrics;
    private DesktopLyricsTheme _theme = DesktopLyricsTheme.Default;
    private double _fontSize = 36;
    private string _fontFamily = "Segoe UI";
    private int _fontWeight = 400;
    private bool _showTranslation = true;
    private bool _showPronunciation = true;
    private bool _glow = true;
    private double _glowAmount = 5;
    private DesktopLyricsVisualMode _visualMode = DesktopLyricsVisualMode.Fume;
    private bool _isPlaying;
    private bool _suspended;
    private bool _disposed;
    private bool _lyricsChanged = true;
    private bool _layoutDirty = true;
    private bool _resourcesReady;
    private double _lastLayoutWidth;
    private double _lastLayoutHeight;
    private double _internalTimeMs;
    private double _lastTotalMs;
    private double _offsetMs;
    private bool _timeSyncValid;
    private int _currentIndex = -1;
    private double _phase;
    private float _cameraX;
    private float _cameraY;
    private float _cameraScale = 1.0f;
    private float _cameraTargetX;
    private float _cameraTargetY;
    private float _cameraTargetScale = 1.0f;
    private volatile TextBoundsBox? _lastTextBounds;

    private sealed class TextBoundsBox(Rect value)
    {
        public Rect Value { get; } = value;
    }

    private sealed class LineVisual : IDisposable
    {
        public required CanvasTextLayout Layout { get; init; }
        public required string Text { get; init; }
        public required double StartMs { get; init; }
        public required double EndMs { get; init; }
        public required int SourceIndex { get; init; }
        public required Vector2 Center { get; init; }
        public required Vector2 LayoutCenter { get; init; }
        public required float Rotation { get; init; }
        public required bool IsHero { get; init; }
        public required List<GlyphVisual> Glyphs { get; init; }

        public void Dispose()
        {
            Layout.Dispose();
            foreach (GlyphVisual glyph in Glyphs)
                glyph.Dispose();
            Glyphs.Clear();
        }
    }

    private sealed class GlyphVisual : IDisposable
    {
        public required CanvasTextLayout Layout { get; init; }
        public required string Text { get; init; }
        public required double StartMs { get; init; }
        public required double EndMs { get; init; }
        public required Vector2 LocalPosition { get; init; }
        public required Vector2 LayoutCenter { get; init; }
        public required Color AccentColor { get; init; }

        public void Dispose() => Layout.Dispose();
    }

    private readonly record struct StarVisual(Vector2 Position, float Radius, float Phase, bool Accent);
    private readonly record struct GeometryVisual(Vector2 Position, float Size, float Rotation, float Speed, byte Kind);

    public FoliaLyricsRenderer()
    {
        _canvas.TargetElapsedTime = TimeSpan.FromMilliseconds(1000.0 / 60);
        _canvas.CreateResources += OnCreateResources;
        _canvas.Update += OnUpdate;
        _canvas.Draw += OnDraw;
    }

    public UIElement Content => _canvas;
    public Rect? LastTextBounds => _lastTextBounds?.Value;

    public void SetStyle(DesktopLyricsStyle style)
    {
        _fontSize = Math.Clamp(style.FontSize, 8, 300);
        _fontFamily = string.IsNullOrWhiteSpace(style.FontFamily) ? "Segoe UI" : style.FontFamily;
        _fontWeight = Math.Clamp(style.FontWeight, 100, 900);
        _showTranslation = style.ShowTranslation;
        _showPronunciation = style.ShowPronunciation;
        _glow = style.Glow;
        _glowAmount = Math.Clamp(style.GlowAmount, 0, 10);
        if (style.Theme is { } theme)
        {
            _theme = theme;
            _visualMode = theme.VisualMode;
        }
        _layoutDirty = true;
    }

    public void SetLyrics(IList<LyricLine>? lyrics)
    {
        _lyrics = lyrics is null ? null : [.. lyrics];
        _lyricsChanged = true;
        _layoutDirty = true;
    }

    public void SetPlaybackTime(long totalMs) => _lastTotalMs = totalMs;
    public void SetOffset(double offsetMs) => _offsetMs = offsetMs;

    public void SetSuspended(bool suspended)
    {
        _suspended = suspended;
        if (!suspended) _timeSyncValid = false;
        _canvas.Paused = suspended;
    }

    public void SetIsPlaying(bool isPlaying)
    {
        if (_isPlaying == isPlaying) return;
        _isPlaying = isPlaying;
        _timeSyncValid = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _canvas.Paused = true;
        _canvas.CreateResources -= OnCreateResources;
        _canvas.Update -= OnUpdate;
        _canvas.Draw -= OnDraw;
        DisposeScene();
        DisposeBrushes();
    }

    private void OnCreateResources(CanvasAnimatedControl sender, Microsoft.Graphics.Canvas.UI.CanvasCreateResourcesEventArgs args)
    {
        try
        {
            DisposeScene();
            DisposeBrushes();
            _textBrush = new CanvasSolidColorBrush(sender, _theme.Primary);
            _accentBrush = new CanvasSolidColorBrush(sender, _theme.Accent);
            _glowBrush = new CanvasSolidColorBrush(sender, _theme.Accent);
            _resourcesReady = true;
            _layoutDirty = true;
        }
        catch
        {
            _resourcesReady = false;
        }
    }

    private void OnUpdate(ICanvasAnimatedControl sender, CanvasAnimatedUpdateEventArgs args)
    {
        if (_disposed || _suspended) return;
        try
        {
            if (_lyricsChanged)
            {
                _lyricsChanged = false;
                _layoutDirty = true;
            }

            double externalTime = _lastTotalMs;
            if (_isPlaying)
            {
                if (!_timeSyncValid || Math.Abs(externalTime - _internalTimeMs) > SyncThresholdMs)
                    _internalTimeMs = externalTime;
                else
                    _internalTimeMs += args.Timing.ElapsedTime.TotalMilliseconds;
                _timeSyncValid = true;
            }
            else
            {
                _internalTimeMs = externalTime;
                _timeSyncValid = true;
            }

            double currentTime = _internalTimeMs - _offsetMs;
            int nextIndex = FindCurrentLineIndex(currentTime);
            bool lineChanged = nextIndex != _currentIndex;
            _currentIndex = nextIndex;
            double width = _canvas.Size.Width;
            double height = _canvas.Size.Height;
            if (_layoutDirty || lineChanged || Math.Abs(width - _lastLayoutWidth) > 0.5 || Math.Abs(height - _lastLayoutHeight) > 0.5)
                RebuildScene(sender, nextIndex, width, height);

            ResolveCameraTarget(nextIndex, width, height);
            float dt = (float)Math.Clamp(args.Timing.ElapsedTime.TotalSeconds, 1.0 / 240, 0.05);
            _cameraX += (_cameraTargetX - _cameraX) * (1f - MathF.Exp(-dt * 8.5f));
            _cameraY += (_cameraTargetY - _cameraY) * (1f - MathF.Exp(-dt * 7.5f));
            _cameraScale += (_cameraTargetScale - _cameraScale) * (1f - MathF.Exp(-dt * 5.5f));
            _phase += dt * (_isPlaying ? 0.8 : 0.12);
        }
        catch
        {
            _layoutDirty = true;
        }
    }

    private void OnDraw(ICanvasAnimatedControl sender, CanvasAnimatedDrawEventArgs args)
    {
        if (_disposed || _suspended) return;
        try
        {
            CanvasDrawingSession ds = args.DrawingSession;
            ds.Clear(_theme.Background);
            if (!_resourcesReady || _textBrush is null || _accentBrush is null || _glowBrush is null)
                return;

            double currentTime = _internalTimeMs - _offsetMs;
            DrawFumeBackground(ds);
            DrawSceneLines(ds, currentTime);
            DrawModeOverlay(ds);
            DrawAuxiliaryText(ds);
        }
        catch
        {
            _layoutDirty = true;
        }
    }

    private void DrawFumeBackground(CanvasDrawingSession ds)
    {
        float width = (float)_lastLayoutWidth;
        float height = (float)_lastLayoutHeight;
        Vector2 screenCenter = new(width * 0.5f, height * 0.5f);

        foreach (StarVisual star in _stars)
        {
            Vector2 point = Project(star.Position, 0.72f, screenCenter);
            float twinkle = 0.55f + 0.45f * MathF.Sin((float)_phase * 1.6f + star.Phase);
            byte alpha = (byte)Math.Clamp((star.Accent ? 175 : 125) * twinkle, 24, 180);
            Color color = WithAlpha(star.Accent ? MixColor(_theme.Accent, FumeGold, 0.4) : _theme.Secondary, alpha);
            ds.DrawEllipse(point, star.Radius, star.Radius, color, star.Accent ? 1.4f : 1f);
        }

        foreach (GeometryVisual shape in _geometry)
        {
            Vector2 point = Project(shape.Position, 0.78f, screenCenter);
            float rotation = shape.Rotation + (float)_phase * shape.Speed;
            bool accent = shape.Kind is 1 or 3;
            byte alpha = (byte)Math.Clamp(shape.Kind == 3 ? 155 : 118, 22, 165);
            Color color = WithAlpha(accent ? MixColor(_theme.Accent, FumeGold, 0.35) : _theme.Secondary, alpha);
            Vector2 axis = new(MathF.Cos(rotation), MathF.Sin(rotation));
            Vector2 perpendicular = new(-axis.Y, axis.X);
            if (shape.Kind == 0)
            {
                ds.DrawEllipse(point, shape.Size, shape.Size, WithAlpha(color, (byte)(alpha * 0.52f)), 0.7f);
                ds.DrawEllipse(point, shape.Size * 0.96f, shape.Size * 0.96f, color, 1.15f);
            }
            else if (shape.Kind == 1)
            {
                Vector2 a = point + axis * shape.Size;
                Vector2 b = point + perpendicular * shape.Size * 0.72f;
                Vector2 c = point - axis * shape.Size;
                Vector2 d = point - perpendicular * shape.Size * 0.72f;
                ds.DrawLine(a, b, color, 1f);
                ds.DrawLine(b, c, color, 1f);
                ds.DrawLine(c, d, color, 1f);
                ds.DrawLine(d, a, color, 1f);
            }
            else if (shape.Kind == 2)
            {
                float arm = shape.Size * 0.30f;
                ds.DrawLine(point - perpendicular * shape.Size, point + perpendicular * shape.Size, color, 1f);
                ds.DrawLine(point - axis * shape.Size, point + axis * shape.Size, color, 1f);
                ds.DrawLine(point - perpendicular * arm - axis * arm, point + perpendicular * arm - axis * arm, WithAlpha(color, (byte)(alpha * 0.65f)), 0.8f);
            }
            else
            {
                ds.DrawLine(point - axis * shape.Size, point + axis * shape.Size, color, 1.1f);
                ds.DrawLine(point - perpendicular * shape.Size, point + perpendicular * shape.Size, color, 1.1f);
                ds.DrawLine(point - (axis + perpendicular) * shape.Size * 0.68f, point + (axis + perpendicular) * shape.Size * 0.68f, WithAlpha(color, (byte)(alpha * 0.68f)), 0.8f);
                ds.DrawLine(point - (axis - perpendicular) * shape.Size * 0.68f, point + (axis - perpendicular) * shape.Size * 0.68f, WithAlpha(color, (byte)(alpha * 0.68f)), 0.8f);
            }
        }
    }

    private void DrawSceneLines(CanvasDrawingSession ds, double currentTime)
    {
        if (_lines.Count == 0) return;

        float width = (float)_lastLayoutWidth;
        float height = (float)_lastLayoutHeight;
        Vector2 screenCenter = new(width * 0.5f, height * 0.5f);
        Matrix3x2 worldToScreen = Matrix3x2.CreateTranslation(-_cameraX, -_cameraY)
            * Matrix3x2.CreateScale(_cameraScale)
            * Matrix3x2.CreateTranslation(width * 0.5f, height * 0.5f);

        foreach (LineVisual line in _lines)
        {
            Vector2 projectedCenter = Project(line.Center, 1f, screenCenter);
            float projectedExtent = MathF.Max((float)line.Layout.LayoutBounds.Width, (float)line.Layout.LayoutBounds.Height)
                * _cameraScale * 0.62f + 64f;
            if (projectedCenter.X < -projectedExtent || projectedCenter.X > width + projectedExtent
                || projectedCenter.Y < -projectedExtent || projectedCenter.Y > height + projectedExtent)
                continue;

            double progress = ResolveLineProgress(line, currentTime);
            float alpha = progress < 0
                ? 0.18f
                : progress <= 1
                    ? 0.28f + (float)Math.Sin(progress * Math.PI) * 0.22f
                    : 0.32f * (float)Math.Exp(-Math.Min((currentTime - line.EndMs) / PassedHoldMs, 7));
            if (alpha <= 0.01f) continue;

            _textBrush!.Color = WithAlpha(_theme.Primary, (byte)Math.Clamp(alpha * 255, 0, 255));
            bool hasLiveGlyphs = line.Glyphs.Count > 0;
            if (!hasLiveGlyphs || currentTime > line.EndMs)
                DrawLayout(ds, line.Layout, line.LayoutCenter, line.Center, line.Rotation, worldToScreen, _textBrush);

            if (line.StartMs <= currentTime && currentTime <= line.EndMs && line.Glyphs.Count > 0)
                DrawLiveGlyphs(ds, line, currentTime, worldToScreen);
        }

        _lastTextBounds = new TextBoundsBox(new Rect(0, 0, width, height));
    }

    private void DrawLiveGlyphs(CanvasDrawingSession ds, LineVisual line, double currentTime, Matrix3x2 worldToScreen)
    {
        foreach (GlyphVisual glyph in line.Glyphs)
        {
            double progress = (currentTime - glyph.StartMs) / Math.Max(glyph.EndMs - glyph.StartMs, 1);
            bool active = progress >= 0 && progress <= 1;
            bool passed = progress > 1;
            float envelope = active ? (float)Math.Sin(Math.Clamp(progress, 0, 1) * Math.PI) : 0;
            float alpha = active ? 0.78f + envelope * 0.22f : passed ? 0.24f : 0.10f;
            float scale = active ? 1f + envelope * 0.06f : 1f;
            Vector2 position = glyph.LocalPosition;
            if (active)
                position.Y -= envelope * Math.Max(8f, (float)_fontSize * 0.22f);

            Color color = active
                ? MixColor(glyph.AccentColor, FumeGold, 0.72)
                : passed ? MixColor(glyph.AccentColor, FumeGold, 0.36) : _theme.Primary;
            if (_glow && active)
            {
                byte glowAlpha = (byte)Math.Clamp(alpha * Math.Min(48, 8 + _glowAmount * 5), 0, 255);
                _glowBrush!.Color = WithAlpha(color, glowAlpha);
                DrawLayout(ds, glyph.Layout, glyph.LayoutCenter, line.Center + position, line.Rotation, worldToScreen, _glowBrush, scale * 1.045f);
            }

            _accentBrush!.Color = WithAlpha(color, (byte)Math.Clamp(alpha * 255, 0, 255));
            DrawLayout(ds, glyph.Layout, glyph.LayoutCenter, line.Center + position, line.Rotation, worldToScreen, _accentBrush, scale);
        }
    }

    private void DrawModeOverlay(CanvasDrawingSession ds)
    {
        float width = (float)_lastLayoutWidth;
        float height = (float)_lastLayoutHeight;
        Color accent = WithAlpha(MixColor(_theme.Accent, FumeGold, 0.32), 70);
        Color secondary = WithAlpha(_theme.Secondary, 52);
        Vector2 center = new(width * 0.5f, height * 0.5f);

        switch (_visualMode)
        {
            case DesktopLyricsVisualMode.Pendolo:
                Vector2 clock = new(width * 0.18f, height * 0.28f);
                float radius = Math.Clamp(Math.Min(width, height) * 0.095f, 54f, 118f);
                ds.DrawEllipse(clock, radius, radius, accent, 1.4f);
                ds.DrawEllipse(clock, radius * 0.16f, radius * 0.16f, accent, 1.2f);
                float hand = (float)_phase * 0.55f;
                Vector2 handDirection = new(MathF.Cos(hand), MathF.Sin(hand));
                ds.DrawLine(clock, clock + handDirection * radius * 0.78f, accent, 1.6f);
                ds.DrawLine(clock, clock + new Vector2(-handDirection.Y, handDirection.X) * radius * 0.52f, secondary, 1.1f);
                break;
            case DesktopLyricsVisualMode.Claddagh:
                ds.DrawEllipse(center, width * 0.34f, height * 0.18f, secondary, 1.2f);
                ds.DrawEllipse(center, width * 0.25f, height * 0.11f, accent, 1f);
                break;
            case DesktopLyricsVisualMode.Monet:
                ds.DrawLine(new Vector2(0, height), new Vector2(width, 0), secondary, 1.2f);
                ds.DrawLine(new Vector2(width * 0.34f, height), new Vector2(width * 0.82f, 0), accent, 0.8f);
                break;
            case DesktopLyricsVisualMode.Sonnet:
                float margin = Math.Min(width, height) * 0.10f;
                ds.DrawLine(new Vector2(margin, margin), new Vector2(margin + 90, margin), accent, 1f);
                ds.DrawLine(new Vector2(margin, margin), new Vector2(margin, margin + 90), accent, 1f);
                ds.DrawLine(new Vector2(width - margin, height - margin), new Vector2(width - margin - 90, height - margin), accent, 1f);
                ds.DrawLine(new Vector2(width - margin, height - margin), new Vector2(width - margin, height - margin - 90), accent, 1f);
                break;
            case DesktopLyricsVisualMode.Tempera:
                ds.FillRectangle(0, height * 0.08f, width, height * 0.18f, WithAlpha(_theme.Accent, 18));
                ds.FillRectangle(0, height * 0.74f, width, height * 0.18f, WithAlpha(_theme.Secondary, 16));
                ds.DrawLine(new Vector2(0, height * 0.52f), new Vector2(width, height * 0.52f), secondary, 1f);
                break;
            case DesktopLyricsVisualMode.Lumiere:
                ds.DrawLine(new Vector2(width * 0.43f, 0), new Vector2(width * 0.30f, height * 0.68f), accent, 1f);
                ds.DrawLine(new Vector2(width * 0.57f, 0), new Vector2(width * 0.70f, height * 0.68f), accent, 1f);
                ds.DrawLine(new Vector2(width * 0.27f, height * 0.80f), new Vector2(width * 0.73f, height * 0.80f), accent, 1.2f);
                break;
        }
    }

    private void DrawAuxiliaryText(CanvasDrawingSession ds)
    {
        if (_currentIndex < 0 || _lyrics is null || _currentIndex >= _lyrics.Count) return;
        LyricLine line = _lyrics[_currentIndex];
        string text = _showTranslation && !string.IsNullOrWhiteSpace(line.TransLateText)
            ? line.TransLateText
            : _showPronunciation ? line.PronunciationText ?? string.Empty : string.Empty;
        if (string.IsNullOrWhiteSpace(text)) return;

        if (_auxiliaryLayout is null || !string.Equals(text, _auxiliaryText, StringComparison.Ordinal)) return;
        _textBrush!.Color = WithAlpha(_theme.Secondary, 0xA0);
        ds.DrawTextLayout(_auxiliaryLayout, new Vector2(
            (float)((_lastLayoutWidth - _auxiliaryLayout.LayoutBounds.Width) * 0.5),
            (float)(_lastLayoutHeight * 0.79)), _textBrush);
    }

    private void RebuildScene(ICanvasResourceCreator creator, int index, double width, double height)
    {
        bool preserveCamera = _lines.Count > 0
            && Math.Abs(width - _lastLayoutWidth) <= 0.5
            && Math.Abs(height - _lastLayoutHeight) <= 0.5;
        DisposeScene();
        _lastLayoutWidth = width;
        _lastLayoutHeight = height;
        _layoutDirty = false;
        if (_lyrics is null || _lyrics.Count == 0 || width < 100 || height < 50)
        {
            _layoutDirty = true;
            return;
        }

        float viewportWidth = (float)width;
        float viewportHeight = (float)height;
        float paperWidth = Math.Max(viewportWidth * 1.95f, 1800f);
        float paperLeft = viewportWidth * 0.14f;
        int columnCount = viewportWidth >= 1200 ? 3 : 2;
        float columnGap = Math.Clamp(viewportWidth * 0.025f, 20f, 48f);
        float columnWidth = (paperWidth - columnGap * (columnCount - 1)) / columnCount;
        float verticalMargin = Math.Max(viewportHeight * 0.52f, 220f);
        var columnHeights = new float[columnCount];
        Array.Fill(columnHeights, verticalMargin);
        float worldWidth = paperLeft * 2f + paperWidth;
        int lineCount = _lyrics.Count;
        for (int lineIndex = 0; lineIndex < lineCount; lineIndex++)
        {
            LyricLine line = _lyrics[lineIndex];
            string text = BuildLineText(line);
            if (string.IsNullOrWhiteSpace(text)) continue;

            List<string> graphemes = SplitGraphemes(text);
            bool hero = lineIndex == index || lineIndex % 6 == 0 || graphemes.Count <= 12;
            float fontSize = ResolveLineFontSize(graphemes.Count, hero, viewportWidth, viewportHeight);
            using var format = CreateTextFormat(fontSize, _fontWeight);
            using var measuredLayout = new CanvasTextLayout(creator, text, format, worldWidth * 2f, viewportHeight * 2f);
            Vector2 layoutCenter = new((float)measuredLayout.LayoutBounds.Width * 0.5f, (float)measuredLayout.LayoutBounds.Height * 0.5f);
            int seed = StableHash($"{line.StartMs:F0}:{lineIndex}:{text}");
            int preferredColumn = seed % columnCount;
            int column = preferredColumn;
            for (int candidate = 0; candidate < columnCount; candidate++)
            {
                int probe = (preferredColumn + candidate) % columnCount;
                if (columnHeights[probe] < columnHeights[column]) column = probe;
            }

            bool spanColumns = hero && columnCount > 1 && seed % 3 == 0;
            int span = spanColumns ? Math.Min(2, columnCount - column) : 1;
            float blockWidth = columnWidth * span + columnGap * (span - 1);
            float x = paperLeft + column * (columnWidth + columnGap) + blockWidth * 0.5f;
            float y = columnHeights[column] + (float)measuredLayout.LayoutBounds.Height * 0.5f;
            float jitterX = (seed % 1000 / 1000f - 0.5f) * Math.Min(columnWidth * 0.22f, viewportWidth * 0.12f);
            float jitterY = (seed / 1000 % 1000 / 1000f - 0.5f) * Math.Min(viewportHeight * 0.12f, 100f);
            x += jitterX;
            y += jitterY;
            float rotation = (seed / 1000 % 1000 / 1000f - 0.5f) * (hero ? 0.085f : 0.05f);

            if (_visualMode is DesktopLyricsVisualMode.Still or DesktopLyricsVisualMode.Classic)
            {
                x = viewportWidth * 0.5f;
                y = viewportHeight * 0.5f + (lineIndex - index) * viewportHeight * 1.15f;
                rotation = 0;
            }
            else if (_visualMode == DesktopLyricsVisualMode.Partita)
            {
                x = viewportWidth * 0.5f + (lineIndex - index) * viewportWidth * 0.13f;
                y = viewportHeight * 0.32f + (lineIndex - index) * Math.Max(viewportHeight * 0.25f, 170f);
                rotation = 0;
            }
            else if (_visualMode == DesktopLyricsVisualMode.Cappella)
            {
                x = lineIndex % 2 == 0 ? viewportWidth * 0.30f : viewportWidth * 0.70f;
                y = viewportHeight * 0.30f + (lineIndex - index) * Math.Max(viewportHeight * 0.28f, 190f);
                rotation = lineIndex % 2 == 0 ? -0.025f : 0.025f;
            }
            else if (_visualMode == DesktopLyricsVisualMode.Tilt)
            {
                rotation += lineIndex % 2 == 0 ? -0.12f : 0.12f;
            }
            else if (_visualMode == DesktopLyricsVisualMode.Diorama)
            {
                x = viewportWidth * (0.22f + (lineIndex % 3) * 0.28f);
                y = viewportHeight * 0.26f + (lineIndex - index) * Math.Max(viewportHeight * 0.30f, 210f);
                rotation = (lineIndex % 3 - 1) * 0.045f;
            }

            var visual = new LineVisual
            {
                Layout = new CanvasTextLayout(creator, text, format, worldWidth * 2f, viewportHeight * 2f),
                Text = text,
                StartMs = line.StartMs,
                EndMs = Math.Max(line.EndMs, line.StartMs + 100),
                SourceIndex = lineIndex,
                Center = new Vector2(x, y),
                LayoutCenter = layoutCenter,
                Rotation = rotation,
                IsHero = hero,
                Glyphs = [],
            };
            float blockGap = Math.Max((float)measuredLayout.LayoutBounds.Height * (hero ? 0.28f : 0.16f), hero ? 18f : 10f);
            for (int spanColumn = column; spanColumn < column + span; spanColumn++)
                columnHeights[spanColumn] = y + (float)measuredLayout.LayoutBounds.Height * 0.5f + blockGap;
            // Per-grapheme layouts are only needed for the camera's active block. Other blocks use
            // one cached layout and stay cheap while they form the article's ghosted context.
            if (lineIndex == index && _visualMode != DesktopLyricsVisualMode.Still)
                BuildGlyphs(creator, visual, line, graphemes, fontSize);
            _lines.Add(visual);
        }

        float worldHeight = Math.Max(viewportHeight * 2f, 0f);
        foreach (float columnHeight in columnHeights)
            worldHeight = Math.Max(worldHeight, columnHeight + viewportHeight * 0.58f);
        BuildBackground(worldWidth, worldHeight, StableHash($"{_theme.Name}:{lineCount}"));
        BuildAuxiliaryLayout(creator, index, viewportWidth, viewportHeight);
        if (_lines.Count > 0 && !preserveCamera)
        {
            LineVisual targetLine = ResolveTargetLine(index) ?? _lines[0];
            _cameraX = targetLine.Center.X;
            _cameraY = targetLine.Center.Y;
            _cameraTargetX = _cameraX;
            _cameraTargetY = _cameraY;
        }
        else if (_lines.Count == 0)
        {
            _cameraX = worldWidth * 0.5f;
            _cameraY = viewportHeight * 0.5f;
            _cameraTargetX = _cameraX;
            _cameraTargetY = _cameraY;
        }
        if (!preserveCamera)
            _cameraScale = _cameraTargetScale = 1.0f;
    }

    private void BuildGlyphs(ICanvasResourceCreator creator, LineVisual visual, LyricLine line, List<string> graphemes, float fontSize)
    {
        List<(string Text, double Start, double End)> timedGraphemes = BuildTimedGraphemes(line, graphemes);
        float totalWidth = 0;
        var measured = new List<(string Text, double Start, double End, Color Color, float Width, CanvasTextLayout Layout)>();
        using var format = CreateTextFormat(fontSize, _fontWeight);
        foreach (var item in timedGraphemes)
        {
            var layout = new CanvasTextLayout(creator, item.Text, format, fontSize * 2.5f, fontSize * 2.5f);
            float glyphWidth = Math.Max(1, (float)layout.LayoutBounds.Width);
            measured.Add((item.Text, item.Start, item.End, ResolveWordColor(item.Text), glyphWidth, layout));
            totalWidth += glyphWidth;
        }

        float cursor = -totalWidth * 0.5f;
        int scatterSeed = StableHash($"cadenza:{visual.SourceIndex}:{visual.Text}");
        bool scatter = _visualMode == DesktopLyricsVisualMode.Cadenza;
        foreach (var item in measured)
        {
            Vector2 center;
            if (scatter)
            {
                scatterSeed = unchecked(scatterSeed * 1664525 + 1013904223);
                float normalizedX = ((scatterSeed >>> 8) & 0xFFFF) / 65535f;
                scatterSeed = unchecked(scatterSeed * 1664525 + 1013904223);
                float normalizedY = ((scatterSeed >>> 8) & 0xFFFF) / 65535f;
                center = new Vector2(
                    (normalizedX - 0.5f) * Math.Max(180f, (float)_lastLayoutWidth * 0.82f),
                    (normalizedY - 0.5f) * Math.Max(100f, (float)_lastLayoutHeight * 0.42f));
            }
            else
            {
                center = new Vector2(cursor + item.Width * 0.5f, 0);
            }
            visual.Glyphs.Add(new GlyphVisual
            {
                Layout = item.Layout,
                Text = item.Text,
                StartMs = item.Start,
                EndMs = item.End,
                LocalPosition = center,
                LayoutCenter = new Vector2((float)item.Layout.LayoutBounds.Width * 0.5f, (float)item.Layout.LayoutBounds.Height * 0.5f),
                AccentColor = item.Color,
            });
            cursor += item.Width;
        }
    }

    private void BuildAuxiliaryLayout(ICanvasResourceCreator creator, int index, float width, float height)
    {
        _auxiliaryLayout?.Dispose();
        _auxiliaryLayout = null;
        _auxiliaryText = string.Empty;
        if (_lyrics is null || index < 0 || index >= _lyrics.Count) return;

        LyricLine line = _lyrics[index];
        string text = _showTranslation && !string.IsNullOrWhiteSpace(line.TransLateText)
            ? line.TransLateText
            : _showPronunciation ? line.PronunciationText ?? string.Empty : string.Empty;
        if (string.IsNullOrWhiteSpace(text)) return;

        using var format = new CanvasTextFormat
        {
            FontFamily = _fontFamily,
            FontSize = (float)Math.Clamp(_fontSize * 0.42, 12, 34),
            FontWeight = new FontWeight(400),
            WordWrapping = CanvasWordWrapping.NoWrap,
            HorizontalAlignment = CanvasHorizontalAlignment.Left,
            VerticalAlignment = CanvasVerticalAlignment.Top,
        };
        _auxiliaryLayout = new CanvasTextLayout(creator, text, format, width, height);
        _auxiliaryText = text;
    }

    private void BuildBackground(float worldWidth, float worldHeight, int seed)
    {
        var random = new DeterministicRandom(seed);
        for (int i = 0; i < StarCount; i++)
        {
            _stars.Add(new StarVisual(
                new Vector2(random.Next(0.04f, 0.96f) * worldWidth, random.Next(0.02f, 0.98f) * worldHeight),
                random.Next(0.5f, 2.4f), random.Next(0, MathF.PI * 2), i % 7 == 0));
        }
        float baseUnit = Math.Clamp(Math.Min((float)_lastLayoutWidth, (float)_lastLayoutHeight) * 0.72f, 320f, 760f);
        for (int i = 0; i < GeometryCount; i++)
        {
            bool spark = i >= 8;
            float yRatio = (i + 0.5f) / GeometryCount + random.Next(-0.08f, 0.08f);
            _geometry.Add(new GeometryVisual(
                new Vector2(random.Next(0.12f, 0.88f) * worldWidth, Math.Clamp(yRatio, 0.05f, 0.95f) * worldHeight),
                spark ? random.Next(baseUnit * 0.10f, baseUnit * 0.24f) : random.Next(baseUnit * 0.82f, baseUnit * 1.36f),
                random.Next(-MathF.PI, MathF.PI),
                random.Next(-0.045f, 0.045f),
                (byte)(spark ? 3 : i % 3)));
        }
    }

    private void ResolveCameraTarget(int index, double width, double height)
    {
        if (_lines.Count == 0)
        {
            _cameraTargetX = (float)width * 0.5f;
            _cameraTargetY = (float)height * 0.5f;
            _cameraTargetScale = 1.0f;
            return;
        }

        LineVisual line = ResolveTargetLine(index) ?? _lines[0];
        int seed = StableHash($"camera:{line.SourceIndex}:{line.Text}");
        float framingX = (seed % 1000 / 1000f - 0.5f) * (float)width * 0.26f;
        float framingY = (seed / 1000 % 1000 / 1000f - 0.5f) * (float)height * 0.12f;
        float floatX = MathF.Sin((float)_phase * 0.6f) * 28f;
        float floatY = MathF.Sin((float)_phase * 0.42f + 1.2f) * 18f;
        _cameraTargetX = line.Center.X + framingX + floatX;
        _cameraTargetY = line.Center.Y + framingY + floatY;
        _cameraTargetScale = _visualMode switch
        {
            DesktopLyricsVisualMode.Still => 1.0f,
            DesktopLyricsVisualMode.Classic => 1.32f,
            DesktopLyricsVisualMode.Cadenza => 1.0f,
            DesktopLyricsVisualMode.Partita => 1.28f,
            DesktopLyricsVisualMode.Cappella => 1.2f,
            DesktopLyricsVisualMode.Tilt => 1.45f,
            DesktopLyricsVisualMode.Diorama => 1.35f,
            _ => line.IsHero ? 1.9f : 1.45f,
        };
        if (_visualMode is DesktopLyricsVisualMode.Still or DesktopLyricsVisualMode.Classic or DesktopLyricsVisualMode.Cadenza)
        {
            _cameraTargetX = line.Center.X + floatX * 0.35f;
            _cameraTargetY = line.Center.Y + floatY * 0.35f;
        }
    }

    private LineVisual? ResolveTargetLine(int sourceIndex)
    {
        if (_lines.Count == 0) return null;
        if (sourceIndex >= 0)
        {
            foreach (LineVisual line in _lines)
                if (line.SourceIndex == sourceIndex) return line;
        }
        return _lines[0];
    }

    private static Matrix3x2 CreateTextTransform(Vector2 layoutCenter, Vector2 center, float rotation, Matrix3x2 worldToScreen, float scale)
        => Matrix3x2.CreateTranslation(-layoutCenter)
            * Matrix3x2.CreateScale(scale)
            * Matrix3x2.CreateRotation(rotation)
            * Matrix3x2.CreateTranslation(center)
            * worldToScreen;

    private static void DrawLayout(CanvasDrawingSession ds, CanvasTextLayout layout, Vector2 layoutCenter, Vector2 center,
        float rotation, Matrix3x2 worldToScreen, CanvasSolidColorBrush brush, float scale = 1f, float offsetX = 0)
    {
        Matrix3x2 previous = ds.Transform;
        ds.Transform = CreateTextTransform(layoutCenter, center + new Vector2(offsetX, 0), rotation, worldToScreen, scale);
        ds.DrawTextLayout(layout, Vector2.Zero, brush);
        ds.Transform = previous;
    }

    private void DisposeScene()
    {
        foreach (LineVisual line in _lines)
            line.Dispose();
        _lines.Clear();
        _auxiliaryLayout?.Dispose();
        _auxiliaryLayout = null;
        _auxiliaryText = string.Empty;
        _stars.Clear();
        _geometry.Clear();
        _lastTextBounds = null;
    }

    private void DisposeBrushes()
    {
        _textBrush?.Dispose();
        _accentBrush?.Dispose();
        _glowBrush?.Dispose();
        _textBrush = null;
        _accentBrush = null;
        _glowBrush = null;
    }

    private static string BuildLineText(LyricLine line)
    {
        var builder = new StringBuilder();
        foreach (LyricWord word in line.Words)
            builder.Append(word.Word);
        return builder.ToString();
    }

    private static List<string> SplitGraphemes(string text)
    {
        var result = new List<string>();
        int[] starts = StringInfo.ParseCombiningCharacters(text);
        for (int i = 0; i < starts.Length; i++)
        {
            int end = i + 1 < starts.Length ? starts[i + 1] : text.Length;
            result.Add(text[starts[i]..end]);
        }
        return result;
    }

    private static List<(string Text, double Start, double End)> BuildTimedGraphemes(LyricLine line, List<string> graphemes)
    {
        var result = new List<(string Text, double Start, double End)>(graphemes.Count);
        if (line.Words.Count == 0)
        {
            double span = Math.Max(100, line.EndMs - line.StartMs);
            for (int i = 0; i < graphemes.Count; i++)
            {
                double start = line.StartMs + span * i / Math.Max(1, graphemes.Count);
                double end = line.StartMs + span * (i + 1) / Math.Max(1, graphemes.Count);
                result.Add((graphemes[i], start, end));
            }
            return result;
        }

        int cursor = 0;
        foreach (LyricWord word in line.Words)
        {
            List<string> wordGraphemes = SplitGraphemes(word.Word ?? string.Empty);
            if (wordGraphemes.Count == 0) continue;
            double start = NormalizeWordStart(line, word);
            double duration = Math.Max(80, word.DurationMs > 0 ? word.DurationMs : (line.EndMs - line.StartMs) / Math.Max(1, line.Words.Count));
            for (int i = 0; i < wordGraphemes.Count && cursor < graphemes.Count; i++, cursor++)
            {
                double glyphStart = start + duration * i / wordGraphemes.Count;
                double glyphEnd = start + duration * (i + 1) / wordGraphemes.Count;
                result.Add((graphemes[cursor], glyphStart, glyphEnd));
            }
        }
        while (cursor < graphemes.Count)
        {
            double start = line.StartMs + Math.Max(0, line.EndMs - line.StartMs) * cursor / graphemes.Count;
            result.Add((graphemes[cursor], start, start + 100));
            cursor++;
        }
        return result;
    }

    private CanvasTextFormat CreateTextFormat(float fontSize, int fontWeight)
        => new()
        {
            FontFamily = _fontFamily,
            FontSize = fontSize,
            FontWeight = new FontWeight((ushort)Math.Clamp(fontWeight, 100, 900)),
            WordWrapping = CanvasWordWrapping.NoWrap,
            HorizontalAlignment = CanvasHorizontalAlignment.Left,
            VerticalAlignment = CanvasVerticalAlignment.Top,
        };

    private float ResolveLineFontSize(int graphemeCount, bool hero, float width, float height)
    {
        float heroRatio = _visualMode switch
        {
            DesktopLyricsVisualMode.Classic => 0.075f,
            DesktopLyricsVisualMode.Cadenza => 0.055f,
            DesktopLyricsVisualMode.Still => 0.06f,
            DesktopLyricsVisualMode.Diorama => 0.052f,
            _ => 0.065f,
        };
        float bodyRatio = _visualMode == DesktopLyricsVisualMode.Still ? 0.025f : 0.028f;
        float baseSize = hero ? height * heroRatio : height * bodyRatio;
        float maxWidth = width * (hero ? 2.65f : 1.9f);
        float estimatedWidth = Math.Max(1, graphemeCount) * baseSize * 0.82f;
        if (estimatedWidth > maxWidth)
            baseSize *= maxWidth / estimatedWidth;
        return Math.Clamp(baseSize, hero ? 24 : 14, hero ? 84 : 44);
    }

    private Color ResolveWordColor(string word)
    {
        foreach (var pair in _theme.WordColors)
            if (word.Contains(pair.Key, StringComparison.OrdinalIgnoreCase)) return pair.Value;
        return _theme.Accent;
    }

    private static double ResolveLineProgress(LineVisual line, double time)
    {
        if (time < line.StartMs) return -1;
        if (time <= line.EndMs) return Math.Clamp((time - line.StartMs) / Math.Max(1, line.EndMs - line.StartMs), 0, 1);
        return 2;
    }

    private static double NormalizeWordStart(LyricLine line, LyricWord word)
        => word.StartMs >= line.StartMs - 1 ? word.StartMs : line.StartMs + word.StartMs;

    private int FindCurrentLineIndex(double time)
    {
        if (_lyrics is null || _lyrics.Count == 0) return -1;
        int low = 0, high = _lyrics.Count - 1, match = -1;
        while (low <= high)
        {
            int middle = (low + high) >>> 1;
            if (_lyrics[middle].StartMs <= time) { match = middle; low = middle + 1; }
            else high = middle - 1;
        }
        return match;
    }

    private Vector2 Project(Vector2 world, float parallax, Vector2 screenCenter)
        => screenCenter + (world - new Vector2(_cameraX, _cameraY)) * (_cameraScale * parallax);

    private static int StableHash(string value)
    {
        unchecked
        {
            int hash = 17;
            foreach (char c in value) hash = hash * 31 + c;
            return hash & int.MaxValue;
        }
    }

    private static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    private static Color MixColor(Color first, Color second, double secondWeight)
    {
        secondWeight = Math.Clamp(secondWeight, 0, 1);
        double firstWeight = 1 - secondWeight;
        return Color.FromArgb(
            0xFF,
            (byte)Math.Clamp(Math.Round(first.R * firstWeight + second.R * secondWeight), 0, 255),
            (byte)Math.Clamp(Math.Round(first.G * firstWeight + second.G * secondWeight), 0, 255),
            (byte)Math.Clamp(Math.Round(first.B * firstWeight + second.B * secondWeight), 0, 255));
    }

    private sealed class DeterministicRandom
    {
        private uint _state;

        public DeterministicRandom(int seed) => _state = unchecked((uint)seed);

        public float Next(float min, float max)
        {
            _state = _state * 1664525u + 1013904223u;
            float normalized = (_state & 0x00FFFFFF) / 16777215f;
            return min + (max - min) * normalized;
        }
    }
}
