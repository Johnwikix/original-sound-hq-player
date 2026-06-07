using CommunityToolkit.WinUI;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.Xaml.Interactivity;
using System;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.Behaviors;

public class AlbumCoverBehavior : Behavior<Image>
{
    private static readonly ILogger<AlbumCoverBehavior> _logger =
        WinUIMusicPlayer.App.GetLogger<AlbumCoverBehavior>();

    public static readonly DependencyProperty MusicProperty =
        DependencyProperty.Register(nameof(Music), typeof(Music), typeof(AlbumCoverBehavior),
            new PropertyMetadata(null, OnMusicChanged));

    public Music Music
    {
        get => (Music)GetValue(MusicProperty);
        set => SetValue(MusicProperty, value);
    }

    private static void OnMusicChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is AlbumCoverBehavior b)
            b.Load(e.NewValue as Music);
    }

    private CancellationTokenSource? _cts;

    protected override void OnAttached()
    {
        base.OnAttached();
        if (Music != null) Load(Music);
    }

    protected override void OnDetaching()
    {
        CancelLoad();
        if (AssociatedObject != null)
        {
            AssociatedObject.Source = null;
            AssociatedObject.Opacity = 0;
        }
        base.OnDetaching();
    }

    public void CancelLoad()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    private void Load(Music? music)
    {
        CancelLoad();

        if (music == null || AssociatedObject == null) return;

        AssociatedObject.Opacity = 0;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        var task = CoverLoadQueue.EnqueueAsync(music, token);
        _ = WaitAndApplyAsync(task, token);
    }

    private async Task WaitAndApplyAsync(Task<Microsoft.UI.Xaml.Media.ImageSource> task, CancellationToken token)
    {
        try
        {
            var source = await task.WaitAsync(token);
            if (token.IsCancellationRequested || AssociatedObject == null || source == null) return;
            AssociatedObject.Source = source;
            FadeIn();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogError(ex, "WaitAndApplyAsync 操作失败"); }
    }

    private void FadeIn()
    {
        if (AssociatedObject == null) return;
        var ani = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(300),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var sb = new Storyboard();
        sb.Children.Add(ani);
        Storyboard.SetTarget(ani, AssociatedObject);
        Storyboard.SetTargetProperty(ani, "Opacity");
        sb.Begin();
    }

    public static void ClearImagesInContainer(DependencyObject parent)
        => CoverLoadQueue.ClearImagesInContainer(parent);
}
