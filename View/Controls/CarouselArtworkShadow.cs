using Microsoft.Extensions.Logging;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using System;
using System.Numerics;
using Windows.UI.ViewManagement;

namespace WinUIMusicPlayer.View.Controls;

/// <summary>在独立宿主上绘制封面的模糊投影，不复制图片缓冲或接管封面源。</summary>
internal sealed class CarouselArtworkShadow : IDisposable
{
    private const float Padding = 12;
    private readonly FrameworkElement _artwork;
    private readonly FrameworkElement _host;
    private readonly UISettings _settings = new();
    private readonly AccessibilitySettings _accessibility = new();
    private CompositionVisualSurface? _surface;
    private CompositionSurfaceBrush? _source;
    private CompositionEffectFactory? _factory;
    private CompositionEffectBrush? _brush;
    private CompositionLinearGradientBrush? _horizontalMask;
    private CompositionLinearGradientBrush? _verticalMask;
    private SpriteVisual? _visual;

    /// <summary>获取图片阴影是否已经挂接。</summary>
    internal bool IsAttached => _visual is not null;

    /// <summary>保存封面与投影宿主；实际资源跟随视图加载和卸载。</summary>
    internal CarouselArtworkShadow(FrameworkElement artwork, FrameworkElement host)
    {
        _artwork = artwork;
        _host = host;
    }

    /// <summary>创建合成线程的实时图片投影，封面换项时无需重新解码或截图。</summary>
    internal void Attach()
    {
        if (IsAttached || !_artwork.IsLoaded || !_settings.AdvancedEffectsEnabled) return;
        try
        {
            var sourceVisual = ElementCompositionPreview.GetElementVisual(_artwork);
            var compositor = sourceVisual.Compositor;
            _surface = compositor.CreateVisualSurface();
            _surface.SourceVisual = sourceVisual;
            _source = compositor.CreateSurfaceBrush(_surface);
            _source.Stretch = CompositionStretch.None;
            _source.HorizontalAlignmentRatio = 0;
            _source.VerticalAlignmentRatio = 0;
            _source.Offset = new Vector2(Padding);
            // 图片内容提供各边的颜色，避免用一份平均色替代 Store 的图片阴影。
            using var blur = new GaussianBlurEffect
            {
                BlurAmount = 8,
                BorderMode = EffectBorderMode.Soft,
                Source = new CompositionEffectSourceParameter("artwork")
            };
            _horizontalMask = CreateEdgeMask(compositor, Vector2.UnitX);
            _verticalMask = CreateEdgeMask(compositor, Vector2.UnitY);
            using var horizontalFade = new AlphaMaskEffect { Source = blur, AlphaMask = new CompositionEffectSourceParameter("horizontalMask") };
            using var verticalFade = new AlphaMaskEffect { Source = horizontalFade, AlphaMask = new CompositionEffectSourceParameter("verticalMask") };
            _factory = compositor.CreateEffectFactory(verticalFade);
            _brush = _factory.CreateBrush();
            _brush.SetSourceParameter("artwork", _source);
            _brush.SetSourceParameter("horizontalMask", _horizontalMask);
            _brush.SetSourceParameter("verticalMask", _verticalMask);
            _visual = compositor.CreateSpriteVisual();
            _visual.Brush = _brush;
            _visual.Offset = new Vector3(-Padding, 4 - Padding, 0);
            ElementCompositionPreview.SetElementChildVisual(_host, _visual);
            _artwork.SizeChanged += SizeChanged;
            _host.ActualThemeChanged += ThemeChanged;
            Update();
        }
        catch (Exception ex)
        {
            Dispose();
            App.GetLogger<CarouselArtworkShadow>().LogWarning(ex, "图片投影不可用");
        }
    }

    private void SizeChanged(object sender, SizeChangedEventArgs args) => Update();
    private void ThemeChanged(FrameworkElement sender, object args) => Update();
    private void Update()
    {
        if (_surface is null || _visual is null) return;
        var size = new Vector2((float)_artwork.ActualWidth, (float)_artwork.ActualHeight);
        _surface.SourceSize = size;
        _visual.Size = size + new Vector2(Padding * 2);
        UpdateEdgeMask(_horizontalMask!, _visual.Size.X);
        UpdateEdgeMask(_verticalMask!, _visual.Size.Y);
        _visual.Opacity = _accessibility.HighContrast ? 0 : _host.ActualTheme == ElementTheme.Dark ? .32f : .18f;
    }
    private static CompositionLinearGradientBrush CreateEdgeMask(Compositor compositor, Vector2 direction)
    {
        var brush = compositor.CreateLinearGradientBrush();
        brush.StartPoint = Vector2.Zero;
        brush.EndPoint = direction;
        brush.ColorStops.Add(compositor.CreateColorGradientStop(0, Colors.Transparent));
        brush.ColorStops.Add(compositor.CreateColorGradientStop(.1f, Colors.White));
        brush.ColorStops.Add(compositor.CreateColorGradientStop(.9f, Colors.White));
        brush.ColorStops.Add(compositor.CreateColorGradientStop(1, Colors.Transparent));
        return brush;
    }
    private static void UpdateEdgeMask(CompositionLinearGradientBrush brush, float size)
    {
        // 外缘在 12px 内归零，避免模糊尾部需要靠增加页面 margin 才能隐藏硬裁切。
        float inset = Math.Min(.5f, Padding / Math.Max(1, size));
        brush.ColorStops[1].Offset = inset;
        brush.ColorStops[2].Offset = 1 - inset;
    }

    /// <summary>解除引用并释放所有自有合成对象，允许再次挂接。</summary>
    public void Dispose()
    {
        _artwork.SizeChanged -= SizeChanged;
        _host.ActualThemeChanged -= ThemeChanged;
        try
        {
            if (_visual is not null) ElementCompositionPreview.SetElementChildVisual(_host, null);
            if (_surface is not null) _surface.SourceVisual = null;
        }
        catch (Exception ex) { App.GetLogger<CarouselArtworkShadow>().LogDebug(ex, "解除图片投影目标失败"); }
        Release(_visual);
        Release(_brush);
        Release(_factory);
        ReleaseMask(_horizontalMask);
        ReleaseMask(_verticalMask);
        Release(_source);
        Release(_surface);
        _visual = null;
        _brush = null;
        _factory = null;
        _horizontalMask = null;
        _verticalMask = null;
        _source = null;
        _surface = null;
    }
    private static void Release(IDisposable? resource)
    {
        try { resource?.Dispose(); }
        catch (Exception ex) { App.GetLogger<CarouselArtworkShadow>().LogDebug(ex, "释放图片投影资源失败"); }
    }
    private static void ReleaseMask(CompositionLinearGradientBrush? brush)
    {
        if (brush is null) return;
        try
        {
            while (brush.ColorStops.Count > 0)
            {
                int index = brush.ColorStops.Count - 1;
                var stop = brush.ColorStops[index];
                brush.ColorStops.RemoveAt(index);
                Release(stop);
            }
        }
        catch (Exception ex) { App.GetLogger<CarouselArtworkShadow>().LogDebug(ex, "释放投影渐隐蒙版失败"); }
        Release(brush);
    }
}
