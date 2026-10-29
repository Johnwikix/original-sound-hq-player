using Microsoft.Extensions.Logging;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using System;
using System.Numerics;
using Windows.UI.ViewManagement;

namespace WinUIMusicPlayer.View.Controls;

/// <summary>为轮播管理环境光与可在悬停、按压之间过渡的聚光，不拥有 XAML Visual。</summary>
internal sealed class CarouselLighting : IDisposable
{
    private readonly FrameworkElement _target;
    private readonly UISettings _settings = new();
    private readonly PointerEventHandler _movedHandler;
    private readonly PointerEventHandler _pressedHandler;
    private AmbientLight? _ambient;
    private SpotLight? _light;
    private ExpressionAnimation? _pointerExpression;
    private ScalarKeyFrameAnimation? _hoverFade;
    private Vector3KeyFrameAnimation? _rippleOffset;
    private ScalarKeyFrameAnimation? _rippleFade;
    private bool _pointerInside, _trackingPointer;

    /// <summary>获取是否持有当前轮播的光源。</summary>
    internal bool IsAttached => _ambient is not null;

    /// <summary>保存轮播和事件委托；光源延至 Loaded 后显式创建。</summary>
    internal CarouselLighting(FrameworkElement target)
    {
        _target = target;
        _movedHandler = PointerMoved;
        _pressedHandler = PointerPressed;
    }

    /// <summary>创建有界的区域照明和缓存动画，不改变其他页面的光照。</summary>
    internal void Attach()
    {
        if (IsAttached || !_target.IsLoaded) return;
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(_target);
            var compositor = visual.Compositor;
            _ambient = compositor.CreateAmbientLight();
            _ambient.Color = Colors.White;
            _ambient.Intensity = 1;
            _ambient.Targets.Add(visual);
            _light = compositor.CreateSpotLight();
            _light.CoordinateSpace = visual;
            _light.InnerConeColor = Colors.FloralWhite;
            _light.OuterConeColor = Colors.FloralWhite;
            _light.InnerConeAngleInDegrees = 35;
            _light.OuterConeAngleInDegrees = 65;
            _light.InnerConeIntensity = 0;
            _light.OuterConeIntensity = 0;
            _light.ConstantAttenuation = 1;
            _light.LinearAttenuation = .25f;
            _light.QuadraticAttenuation = .6f;
            _light.Targets.Add(visual);

            // 所有封面共用一盏聚光，避免多卡片占满非环境光槽位。
            // 指针位置由合成线程提供，移动时不重建动画或委托。
            _pointerExpression = compositor.CreateExpressionAnimation("Vector3(pointer.Position.X, pointer.Position.Y, 120)");
            _pointerExpression.SetReferenceParameter("pointer", ElementCompositionPreview.GetPointerPositionPropertySet(_target));
            _hoverFade = compositor.CreateScalarKeyFrameAnimation();
            _hoverFade.Duration = TimeSpan.FromMilliseconds(160);
            _hoverFade.StopBehavior = AnimationStopBehavior.LeaveCurrentValue;
            _rippleOffset = compositor.CreateVector3KeyFrameAnimation();
            _rippleOffset.Duration = TimeSpan.FromMilliseconds(800);
            _rippleFade = compositor.CreateScalarKeyFrameAnimation();
            _rippleFade.Duration = _rippleOffset.Duration;
            _rippleFade.InsertKeyFrame(0, 0);
            _rippleFade.InsertKeyFrame(.12f, .7f);
            _rippleFade.InsertKeyFrame(.8f, 0);
            _target.PointerEntered += PointerEntered;
            _target.PointerExited += PointerExited;
            _target.AddHandler(UIElement.PointerMovedEvent, _movedHandler, true);
            _target.AddHandler(UIElement.PointerPressedEvent, _pressedHandler, true);
        }
        catch (Exception ex)
        {
            Detach();
            App.GetLogger<CarouselLighting>().LogWarning(ex, "轮播光效不可用，保留封面与标准按钮反馈");
        }
    }

    /// <summary>显示局部悬停光；鼠标和笔可由合成线程持续跟随。</summary>
    internal void ShowHover(Vector2 position, bool trackPointer)
    {
        _pointerInside = true;
        if (_light is null) return;
        if (trackPointer)
        {
            if (!_trackingPointer) _light.StartAnimation(nameof(SpotLight.Offset), _pointerExpression);
        }
        else
        {
            _light.StopAnimation(nameof(SpotLight.Offset));
            _light.Offset = new Vector3(position, 120);
        }
        if (!_trackingPointer || !trackPointer) FadeHover(.6f);
        _trackingPointer = trackPointer;
    }

    /// <summary>离开区域时停止位置动画并淡出光照。</summary>
    internal void HideHover()
    {
        _pointerInside = false;
        _trackingPointer = false;
        if (_light is null) return;
        _light.StopAnimation(nameof(SpotLight.Offset));
        FadeHover(0);
    }

    private void FadeHover(float intensity)
    {
        if (_light is null || _hoverFade is null) return;
        _light.StopAnimation(nameof(SpotLight.InnerConeIntensity));
        if (!_settings.AnimationsEnabled)
        {
            _light.InnerConeIntensity = intensity;
            return;
        }
        _hoverFade.InsertKeyFrame(1, intensity);
        _light.StartAnimation(nameof(SpotLight.InnerConeIntensity), _hoverFade);
    }

    /// <summary>从按压点扩散并回到悬停亮度；返回状态编码在合成关键帧中，无完成回调。</summary>
    internal void Pulse(Vector2 position)
    {
        if (_light is null || _rippleOffset is null || _rippleFade is null || !_settings.AnimationsEnabled) return;
        _trackingPointer = false;
        _light.StopAnimation(nameof(SpotLight.Offset));
        _light.StopAnimation(nameof(SpotLight.InnerConeIntensity));
        _light.Offset = new Vector3(position, 12);
        _rippleOffset.InsertKeyFrame(0, new Vector3(position, 12));
        _rippleOffset.InsertKeyFrame(.8f, new Vector3(position, (float)Math.Max(_target.ActualWidth, _target.ActualHeight) * 1.8f));
        _rippleOffset.InsertKeyFrame(1, new Vector3(position, 120));
        _rippleFade.InsertKeyFrame(1, _pointerInside ? .6f : 0);
        _light.StartAnimation(nameof(SpotLight.Offset), _rippleOffset);
        _light.StartAnimation(nameof(SpotLight.InnerConeIntensity), _rippleFade);
    }

    private void PointerEntered(object sender, PointerRoutedEventArgs args)
    {
        var position = args.GetCurrentPoint(_target).Position;
        ShowHover(new Vector2((float)position.X, (float)position.Y), args.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Touch);
    }
    private void PointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (_trackingPointer) return;
        PointerEntered(sender, args);
    }
    private void PointerExited(object sender, PointerRoutedEventArgs args) => HideHover();
    private void PointerPressed(object sender, PointerRoutedEventArgs args)
    {
        var point = args.GetCurrentPoint(_target);
        if (!point.Properties.IsLeftButtonPressed && args.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Touch) return;
        Pulse(new Vector2((float)point.Position.X, (float)point.Position.Y));
    }

    /// <summary>解除目标和事件，释放全部自有合成资源；允许重新 Attach。</summary>
    internal void Detach()
    {
        _target.PointerEntered -= PointerEntered;
        _target.PointerExited -= PointerExited;
        _target.RemoveHandler(UIElement.PointerMovedEvent, _movedHandler);
        _target.RemoveHandler(UIElement.PointerPressedEvent, _pressedHandler);
        _pointerInside = false;
        _trackingPointer = false;
        ReleaseLight(_light);
        ReleaseLight(_ambient);
        _light = null;
        _ambient = null;
        Release(_pointerExpression);
        Release(_hoverFade);
        Release(_rippleOffset);
        Release(_rippleFade);
        _pointerExpression = null;
        _hoverFade = null;
        _rippleOffset = null;
        _rippleFade = null;
    }
    private static void ReleaseLight(CompositionLight? light)
    {
        if (light is null) return;
        try { light.Targets.RemoveAll(); }
        catch (Exception ex) { App.GetLogger<CarouselLighting>().LogDebug(ex, "解除轮播光源目标失败"); }
        Release(light);
    }
    private static void Release(IDisposable? resource)
    {
        try { resource?.Dispose(); }
        catch (Exception ex) { App.GetLogger<CarouselLighting>().LogDebug(ex, "释放轮播光效资源失败"); }
    }
    /// <inheritdoc />
    public void Dispose() => Detach();
}
