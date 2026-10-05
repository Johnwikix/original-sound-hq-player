#nullable enable

using AnimatedWin2dControls.Shaders.Background;
using ComputeSharp;
using ComputeSharp.D2D1.WinUI;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI.Xaml;
using System;
using System.Threading;
using Windows.UI;

namespace AnimatedWin2dControls.Renderer.Background;

/// <summary>
/// Folia 壁纸模式的 ComputeSharp D2D1 渲染器。
/// 生命周期与 NowPlayingCanvas 的背景管线一致：资源重建在 CreateResources，
/// 时间只在 Update 推进，Draw 只使用已缓存的常量缓冲，设备切换时丢弃旧 effect。
/// </summary>
public sealed class FoliaWallpaperBackgroundRenderer : IDisposable
{
    private readonly object _gate = new();
    private PixelShaderEffect<FoliaWallpaperEffect>? _effect;
    private Color _background = Color.FromArgb(0xFF, 0x0B, 0x12, 0x25);
    private Color _secondary = Color.FromArgb(0xFF, 0x18, 0x27, 0x4A);
    private Color _primary = Color.FromArgb(0xFF, 0xF4, 0xF8, 0xFF);
    private Color _accent = Color.FromArgb(0xFF, 0x75, 0xB9, 0xFF);
    private int _mode;
    private float _time;
    private float _intensity = 1f;
    private bool _disposed;

    public void Configure(Color background, Color secondary, Color primary, Color accent, int mode, float intensity = 1f)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _background = background;
            _secondary = secondary;
            _primary = primary;
            _accent = accent;
            // Mode is retained as a future background selector; the current build uses
            // the shared common shader background for every lyric composition.
            _mode = Math.Clamp(mode, 0, 10);
            _intensity = Math.Clamp(intensity, 0.25f, 2f);
        }
    }

    public void LoadResources()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _effect?.Dispose();
            _effect = new PixelShaderEffect<FoliaWallpaperEffect>();
        }
    }

    public void Update(TimeSpan elapsed)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _time += (float)Math.Clamp(elapsed.TotalSeconds, 0, 0.1);
        }
    }

    public void Draw(ICanvasAnimatedControl control, CanvasDrawingSession drawingSession)
    {
        float width = control.ConvertDipsToPixels((float)control.Size.Width, CanvasDpiRounding.Round);
        float height = control.ConvertDipsToPixels((float)control.Size.Height, CanvasDpiRounding.Round);
        Draw(drawingSession, width, height);
    }

    /// <summary>Draws the same shader into an offscreen target used by the deterministic verifier.</summary>
    public void Draw(CanvasDrawingSession drawingSession, float width, float height)
    {
        if (!Monitor.TryEnter(_gate, 0)) return;
        try
        {
            if (_disposed || _effect is null || width <= 0 || height <= 0) return;

            _effect.ConstantBuffer = new FoliaWallpaperEffect(
                _time,
                new float2(width, height),
                ToRgb(_background),
                ToRgb(_secondary),
                ToRgb(_primary),
                ToRgb(_accent),
                _mode,
                _intensity);

            drawingSession.DrawImage(_effect);
        }
        finally
        {
            Monitor.Exit(_gate);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _effect?.Dispose();
            _effect = null;
        }
    }

    private static float3 ToRgb(Color color)
        => new(color.R / 255f, color.G / 255f, color.B / 255f);
}
