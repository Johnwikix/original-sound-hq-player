using Microsoft.Extensions.Logging;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using System;
using System.Numerics;

namespace WinUIMusicPlayer.View.Controls;

/// <summary>移植 DevWinUI 10.8.0 ImageFrame 的 SlideLeft/SlideRight 和旧图淡出，不参与图像加载。</summary>
internal sealed class StoreCarouselImageMotion : IDisposable
{
    /// <summary>沿用 ImageFrame.DefaultTransitionDuration，也供 StoreCarousel 文字区使用。</summary>
    internal static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(700);
    private readonly FrameworkElement _current;
    private readonly FrameworkElement _previous;
    private Visual? _currentVisual;
    private Visual? _previousVisual;
    private Vector3KeyFrameAnimation? _offsetAnimation;
    private ScalarKeyFrameAnimation? _fadeOutAnimation;

    /// <summary>保存两层展示区域；原生动画延至显式 Attach 时创建。</summary>
    internal StoreCarouselImageMotion(FrameworkElement current, FrameworkElement previous)
    {
        _current = current;
        _previous = previous;
    }
    /// <summary>创建与原 ImageFrame 相同的默认插值及目标关键帧。</summary>
    internal void Attach()
    {
        if (_currentVisual is not null) return;
        _currentVisual = ElementCompositionPreview.GetElementVisual(_current);
        _previousVisual = ElementCompositionPreview.GetElementVisual(_previous);
        var compositor = _currentVisual.Compositor;
        _offsetAnimation = compositor.CreateVector3KeyFrameAnimation();
        _offsetAnimation.Target = "Offset";
        _offsetAnimation.Duration = Duration;
        _offsetAnimation.InsertKeyFrame(1, Vector3.Zero);
        _fadeOutAnimation = compositor.CreateScalarKeyFrameAnimation();
        _fadeOutAnimation.InsertKeyFrame(1, 0);
        _fadeOutAnimation.Duration = Duration;
        Stop();
    }
    /// <summary>新图从完整画幅之外进入，旧图保持位置并淡出；初次加载同样执行滑入。</summary>
    internal void Start(bool fromRight, double width)
    {
        if (_currentVisual is null || _previousVisual is null) return;
        Stop();
        _currentVisual.Offset = new Vector3((float)(fromRight ? width : -width), 0, 0);
        _currentVisual.Opacity = 1;
        _previousVisual.Opacity = 1;
        _currentVisual.StartAnimation("Offset", _offsetAnimation);
        _previousVisual.StartAnimation("Opacity", _fadeOutAnimation);
    }
    /// <summary>停止在途动画并呈现当前图，供快速切换、缩放、禁用动画和卸载使用。</summary>
    internal void Stop()
    {
        if (_currentVisual is not null)
        {
            try
            {
                _currentVisual.StopAnimation("Offset");
                _currentVisual.Offset = Vector3.Zero;
                _currentVisual.Opacity = 1;
            }
            catch (Exception ex) { App.GetLogger<StoreCarouselImageMotion>().LogDebug(ex, "停止轮播进入动画失败"); }
        }
        if (_previousVisual is not null)
        {
            try
            {
                _previousVisual.StopAnimation("Opacity");
                _previousVisual.Opacity = 0;
            }
            catch (Exception ex) { App.GetLogger<StoreCarouselImageMotion>().LogDebug(ex, "停止轮播旧图淡出失败"); }
        }
    }
    /// <summary>释放自有动画；XAML 提供的 Visual 仅解除引用，不由本对象释放。</summary>
    public void Dispose()
    {
        Stop();
        Release(_offsetAnimation);
        Release(_fadeOutAnimation);
        _offsetAnimation = null;
        _fadeOutAnimation = null;
        _currentVisual = null;
        _previousVisual = null;
    }
    private static void Release(IDisposable? resource)
    {
        try { resource?.Dispose(); }
        catch (Exception ex) { App.GetLogger<StoreCarouselImageMotion>().LogDebug(ex, "释放轮播图片动画失败"); }
    }
}
