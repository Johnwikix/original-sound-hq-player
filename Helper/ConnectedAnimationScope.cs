using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using System.Runtime.InteropServices;

namespace WinUIMusicPlayer.Helper;

/// <summary>页面拥有的连接动画。新操作或离页取消旧快照，过期的排队回调不能更新页面。</summary>
public sealed class ConnectedAnimationScope : IDisposable
{
    private readonly FrameworkElement _owner;
    private readonly string _key;
    private ConnectedAnimation? _animation;
    private Action? _completed;
    private int _generation;
    private bool _disposed;

    public ConnectedAnimationScope(FrameworkElement owner, string key)
    {
        _owner = owner;
        _key = key;
        owner.Unloaded += OwnerUnloaded;
    }

    public bool Prepare(FrameworkElement? source)
    {
        if (_disposed) return false;
        Cancel();
        if (!_owner.IsLoaded || source is not { IsLoaded: true, ActualWidth: > 0, ActualHeight: > 0 }
            || _owner.XamlRoot is null || source.XamlRoot != _owner.XamlRoot) return false;
        try
        {
            _animation = ConnectedAnimationService.GetForCurrentView().PrepareToAnimate(_key, source);
            return true;
        }
        catch (COMException exception)
        {
            App.GetLogger<ConnectedAnimationScope>().LogDebug(exception, "连接动画源已失效");
            return false;
        }
    }

    public void StartDeferred(Func<FrameworkElement?> target, Action? completed = null)
    {
        if (_disposed) return;
        var animation = _animation;
        if (animation is null) { if (_owner.IsLoaded) completed?.Invoke(); return; }
        int generation = _generation;
        _completed = completed;
        if (_owner.DispatcherQueue.TryEnqueue(() =>
        {
            if (generation != _generation || !ReferenceEquals(animation, _animation)) return;
            if (!_owner.IsLoaded) { Cancel(); return; }
            _owner.UpdateLayout();
            var destination = target();
            if (destination is not { IsLoaded: true, ActualWidth: > 0, ActualHeight: > 0 }
                || destination.XamlRoot != _owner.XamlRoot) { Complete(animation); return; }
            animation.Completed += AnimationCompleted;
            try { if (!animation.TryStart(destination)) Complete(animation); }
            catch (COMException exception)
            {
                App.GetLogger<ConnectedAnimationScope>().LogDebug(exception, "连接动画目的元素已失效");
                Complete(animation);
            }
        })) return;
        Complete(animation);
    }

    private void AnimationCompleted(ConnectedAnimation sender, object args) => Complete(sender);

    private void Complete(ConnectedAnimation animation)
    {
        if (!ReferenceEquals(animation, _animation)) return;
        var completed = _completed;
        Cancel();
        if (_owner.IsLoaded) completed?.Invoke();
    }

    private void OwnerUnloaded(object sender, RoutedEventArgs args) => Cancel();

    public void Cancel()
    {
        _generation++;
        _completed = null;
        if (_animation is not { } animation) return;
        _animation = null;
        animation.Completed -= AnimationCompleted;
        try { animation.Cancel(); }
        catch (COMException exception) { App.GetLogger<ConnectedAnimationScope>().LogDebug(exception, "取消连接动画失败"); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Cancel();
        _owner.Unloaded -= OwnerUnloaded;
    }
}
