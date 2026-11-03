using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Services;

internal sealed partial class HomePolishWindow
{
    private async Task CheckSecondaryLifetimes()
    {
        var canvas = new Canvas { Width = 600, Height = 240 };
        var source = new Border { Width = 100, Height = 100, Background = new SolidColorBrush(Microsoft.UI.Colors.CornflowerBlue) };
        var destination = new Border { Width = 150, Height = 150, Background = new SolidColorBrush(Microsoft.UI.Colors.CornflowerBlue) };
        Canvas.SetLeft(destination, 300);
        canvas.Children.Add(source); canvas.Children.Add(destination);
        Grid.SetRow(canvas, 1); _root.Children.Add(canvas);
        await Wait(() => source.IsLoaded && source.ActualWidth > 0 && destination.ActualWidth > 0);
        using var animation = new ConnectedAnimationScope(canvas, "Batch2LifecycleProbe");
        int completions = 0;
        Assert(animation.Prepare(source), "real connected-animation scope accepts a realized source");
        animation.StartDeferred(() => destination, () => completions++);
        animation.Cancel();
        await Task.Delay(100);
        Assert(completions == 0, "cancelled queued animation does not invoke stale completion");
        Assert(animation.Prepare(source), "new connected animation can be prepared after cancellation");
        animation.StartDeferred(() => destination, () => completions++);
        await Wait(() => completions == 1);
        await Task.Delay(100);
        Assert(completions == 1, "native completion or fallback finishes exactly once");
        Assert(typeof(ConnectedAnimationScope).GetField("_animation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(animation) is null,
            "completed connected animation drops its owned snapshot reference");
        Assert(animation.Prepare(source), "connected animation can be prepared before owner removal");
        animation.StartDeferred(() => destination, () => completions++);
        _root.Children.Remove(canvas);
        await Task.Delay(350);
        Assert(completions == 1, "owner unload cancels pending callback without refreshing an absent page");
        _root.Children.Add(canvas);
        await Wait(() => canvas.IsLoaded && source.IsLoaded && destination.IsLoaded && source.ActualWidth > 0 && source.XamlRoot == canvas.XamlRoot);
        canvas.UpdateLayout();
        await Task.Delay(50);
        Assert(animation.Prepare(source), "cached owner can prepare again after reload");
        animation.StartDeferred(() => null, () => completions++);
        await Wait(() => completions == 2);
        Assert(completions == 2, "missing destination safely takes the completion fallback");
        animation.Dispose();
        animation.Dispose();
        Assert(!animation.Prepare(source), "disposed animation scope rejects new snapshots");
        _root.Children.Remove(canvas);
    }
}
