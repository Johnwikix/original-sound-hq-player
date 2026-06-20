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

    private Storyboard? _fadeInStoryboard;
    private DoubleAnimation? _fadeInAnimation;
    private CubicEase? _fadeInEase;

    protected override void OnAttached()
    {
        base.OnAttached();
        if (Music != null) Load(Music);
    }

    protected override void OnDetaching()
    {
        CancelLoad();
        if (_fadeInStoryboard != null)
        {
            _fadeInStoryboard.Stop();
            // 注意: WinUI 3 的 Storyboard.Target DP 没有公开的 ClearValue 路径
            // (TargetPropertyProperty 是属性名 DP, 不是 target 对象 DP)
            // 缓存的 Storyboard 持有 AssociatedObject 引用,
            // 但 Behavior 与 AssociatedObject 同生死, 不会泄漏
        }
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

        if (AssociatedObject == null) return;

        if (music == null)
        {
            AssociatedObject.Source = null;
            AssociatedObject.Opacity = 0;
            return;
        }

        AssociatedObject.Opacity = 0;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        _ = WaitAndApplyAsync(music, token);
    }

    private async Task WaitAndApplyAsync(Music music, CancellationToken token)
    {
        const int maxAttempts = 3;
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            if (token.IsCancellationRequested || AssociatedObject == null) return;

            Task<Microsoft.UI.Xaml.Media.ImageSource> task;
            try
            {
                task = CoverLoadQueue.EnqueueAsync(music, token);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "WaitAndApplyAsync EnqueueAsync 失败");
                return;
            }

            try
            {
                var source = await task.WaitAsync(token);
                if (token.IsCancellationRequested || AssociatedObject == null || source == null) return;
                AssociatedObject.Source = source;
                FadeIn();
                return;
            }
            catch (OperationCanceledException)
            {
                if (token.IsCancellationRequested) return;
            }
        }
    }

    private void EnsureFadeInCache()
    {
        if (_fadeInStoryboard != null) return;
        _fadeInEase = new CubicEase { EasingMode = EasingMode.EaseOut };
        _fadeInAnimation = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(300),
            EasingFunction = _fadeInEase
        };
        _fadeInStoryboard = new Storyboard();
        _fadeInStoryboard.Children.Add(_fadeInAnimation);
        Storyboard.SetTargetProperty(_fadeInAnimation, "Opacity");
    }

    private void FadeIn()
    {
        if (AssociatedObject == null) return;
        EnsureFadeInCache();
        _fadeInStoryboard!.Stop();
        Storyboard.SetTarget(_fadeInAnimation!, AssociatedObject);
        _fadeInStoryboard.Begin();
    }

    public static void ClearImagesInContainer(DependencyObject parent) { }
}
