using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Metrics = Windows.UI.Core.AnimationMetrics;

namespace WinUIMusicPlayer.View.Controls;

/// <summary>复现系统 AddDelete 的缩放和透明度，缩短入场等待及增删时长；拥有本视图复用的 Composition 资源。</summary>
internal sealed class FolderTreeAnimations : IDisposable
{
    internal const double PlaybackRate = 2;

    // WinUI 的 AddDeleteRepositionHelper 使用 Grid 参数（包括 ListView）。仅调整增删：
    // 入场等待独立缩短，缩放和透明度保持原生曲线；让位及错峰由原生 RepositionThemeTransition 管理。
    // https://github.com/microsoft/microsoft-ui-xaml/blob/main/dxaml/xcp/dxaml/lib/LayoutTransition_partial.cpp
    internal static readonly TimeSpan EnterDelay = TimeSpan.FromMilliseconds(30);
    private readonly Metrics.OpacityAnimation _enterOpacity;
    private readonly Metrics.ScaleAnimation _enterScale;
    private readonly Metrics.OpacityAnimation _exitOpacity;
    private readonly Metrics.ScaleAnimation _exitScale;
    private readonly Compositor _compositor;
    private readonly List<CompositionObject> _resources = [];
    private readonly ScalarKeyFrameAnimation _showOpacity;
    private readonly Vector3KeyFrameAnimation _showScale;
    private readonly CompositionAnimationGroup _show;
    private readonly CompositionAnimationGroup _hide;

    internal FolderTreeAnimations(Compositor compositor)
    {
        _compositor = compositor;
        var add = new Metrics.AnimationDescription(Metrics.AnimationEffect.AddToGrid, Metrics.AnimationEffectTarget.Added);
        var delete = new Metrics.AnimationDescription(Metrics.AnimationEffect.DeleteFromGrid, Metrics.AnimationEffectTarget.Deleted);
        _enterOpacity = Find<Metrics.OpacityAnimation>(add);
        _enterScale = Find<Metrics.ScaleAnimation>(add);
        _exitOpacity = Find<Metrics.OpacityAnimation>(delete);
        _exitScale = Find<Metrics.ScaleAnimation>(delete);
        _showOpacity = Opacity(_enterOpacity, Easing(_enterOpacity.Control1, _enterOpacity.Control2), 0);
        _showScale = Scale(_enterScale, Easing(_enterScale.Control1, _enterScale.Control2));
        _show = Group(_showOpacity, _showScale);
        var hideOpacity = Opacity(_exitOpacity, Easing(_exitOpacity.Control1, _exitOpacity.Control2), 1);
        var hideScale = Scale(_exitScale, Easing(_exitScale.Control1, _exitScale.Control2));
        hideOpacity.DelayTime = _exitOpacity.Delay;
        hideScale.DelayTime = _exitScale.Delay;
        _hide = Group(hideOpacity, hideScale);
    }

    internal TimeSpan Enter(FrameworkElement row)
    {
        _showOpacity.DelayTime = EnterDelay;
        _showScale.DelayTime = EnterDelay;
        var visual = Prepare(row, _enterScale.NormalizedOrigin);
        // StartAnimationGroup 会复制模板；后续行修改延迟不会改变已开始的动画。
        visual.StartAnimationGroup(_show);
        return Latest(_showOpacity.DelayTime + _showOpacity.Duration, _showScale.DelayTime + _showScale.Duration);
    }

    internal CompositionAnimationGroup Exit => _hide;
    internal TimeSpan ExitDuration => Latest(_exitOpacity.Delay + Accelerate(_exitOpacity.Duration), _exitScale.Delay + Accelerate(_exitScale.Duration));

    internal Visual PrepareExit(FrameworkElement row) => Prepare(row, _exitScale.NormalizedOrigin);

    private static Visual Prepare(FrameworkElement row, Windows.Foundation.Point origin)
    {
        var visual = ElementCompositionPreview.GetElementVisual(row);
        visual.CenterPoint = new Vector3((float)(row.ActualWidth * origin.X), (float)(row.ActualHeight * origin.Y), 0);
        return visual;
    }

    private ScalarKeyFrameAnimation Opacity(Metrics.OpacityAnimation profile, CompositionEasingFunction easing, float initial)
    {
        var animation = Own(_compositor.CreateScalarKeyFrameAnimation());
        animation.Target = "Opacity";
        animation.Duration = Accelerate(profile.Duration);
        animation.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        animation.InsertKeyFrame(0, profile.InitialOpacity ?? initial);
        animation.InsertKeyFrame(1, profile.FinalOpacity, easing);
        return animation;
    }

    private Vector3KeyFrameAnimation Scale(Metrics.ScaleAnimation profile, CompositionEasingFunction easing)
    {
        var animation = Own(_compositor.CreateVector3KeyFrameAnimation());
        animation.Target = "Scale";
        animation.Duration = Accelerate(profile.Duration);
        animation.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        animation.InsertKeyFrame(0, new Vector3(profile.InitialScaleX ?? 1, profile.InitialScaleY ?? 1, 1));
        animation.InsertKeyFrame(1, new Vector3(profile.FinalScaleX, profile.FinalScaleY, 1), easing);
        return animation;
    }

    private CompositionAnimationGroup Group(CompositionAnimation opacity, CompositionAnimation scale)
    {
        var group = Own(_compositor.CreateAnimationGroup());
        group.Add(opacity);
        group.Add(scale);
        return group;
    }

    private CompositionEasingFunction Easing(Windows.Foundation.Point first, Windows.Foundation.Point second)
        => Own(_compositor.CreateCubicBezierEasingFunction(new Vector2((float)first.X, (float)first.Y), new Vector2((float)second.X, (float)second.Y)));

    private T Own<T>(T resource) where T : CompositionObject
    {
        _resources.Add(resource);
        return resource;
    }

    private static T Find<T>(Metrics.AnimationDescription description) where T : class
    {
        foreach (var animation in description.Animations)
            if (animation is T value) return value;
        throw new InvalidOperationException("The system animation profile is incomplete.");
    }

    private static TimeSpan Accelerate(TimeSpan value) => value / PlaybackRate;
    private static TimeSpan Latest(TimeSpan first, TimeSpan second) => first > second ? first : second;

    public void Dispose()
    {
        for (int i = _resources.Count - 1; i >= 0; i--) _resources[i].Dispose();
        _resources.Clear();
    }
}
