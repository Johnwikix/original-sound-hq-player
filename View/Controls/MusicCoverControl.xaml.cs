using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Hosting;
using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Windows.UI.ViewManagement;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.View.Controls;

/// <summary>复用封面加载队列，在视图卸载时取消加载并释放本控件拥有的图片。</summary>
public sealed partial class MusicCoverControl : UserControl
{
    /// <summary>标识封面所属的规范曲目。</summary>
    public static readonly DependencyProperty MusicProperty = DependencyProperty.Register(
        nameof(Music), typeof(Music), typeof(MusicCoverControl), new PropertyMetadata(null, MusicChanged));
    /// <summary>标识封面解码尺寸，轮播不使用列表的低分辨率缩略图。</summary>
    public static readonly DependencyProperty DecodePixelSizeProperty = DependencyProperty.Register(
        nameof(DecodePixelSize), typeof(int), typeof(MusicCoverControl), new PropertyMetadata(150, MusicChanged));
    /// <summary>标识是否使用移植的 StoreCarousel 双层图片过渡，普通缩略图默认关闭。</summary>
    public static readonly DependencyProperty EnableCarouselTransitionProperty = DependencyProperty.Register(
        nameof(EnableCarouselTransition), typeof(bool), typeof(MusicCoverControl), new PropertyMetadata(false, TransitionChanged));
    /// <summary>标识图片是否从右侧滑入。</summary>
    public static readonly DependencyProperty SlideFromRightProperty = DependencyProperty.Register(
        nameof(SlideFromRight), typeof(bool), typeof(MusicCoverControl), new PropertyMetadata(true));
    private Music? _observed;
    private CancellationTokenSource? _load;
    private readonly DispatcherQueue _dispatcher;
    private StoreCarouselImageMotion? _motion;
    private UISettings? _motionSettings;
    private Visual? _coverVisual;
    private CompositionRoundedRectangleGeometry? _roundedGeometry;
    private CompositionGeometricClip? _roundedClip;
    /// <summary>获取或设置封面所属曲目。</summary>
    public Music? Music { get => (Music?)GetValue(MusicProperty); set => SetValue(MusicProperty, value); }
    /// <summary>获取或设置解码尺寸。</summary>
    public int DecodePixelSize { get => (int)GetValue(DecodePixelSizeProperty); set => SetValue(DecodePixelSizeProperty, value); }
    /// <summary>获取或设置是否启用主轮播图片过渡。</summary>
    public bool EnableCarouselTransition { get => (bool)GetValue(EnableCarouselTransitionProperty); set => SetValue(EnableCarouselTransitionProperty, value); }
    /// <summary>获取或设置图片进入方向。</summary>
    public bool SlideFromRight { get => (bool)GetValue(SlideFromRightProperty); set => SetValue(SlideFromRightProperty, value); }
    /// <summary>获取最近成功加载封面的边缘色。</summary>
    public Windows.UI.Color ArtworkColor { get; private set; }
    /// <summary>在 UI 线程通知展示层更新与封面一致的遮罩和阴影颜色。</summary>
    public event Action<Windows.UI.Color>? ArtworkColorChanged;
    /// <summary>初始化视图生命周期。</summary>
    public MusicCoverControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        _dispatcher = DispatcherQueue;
    }
    private static void MusicChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) => ((MusicCoverControl)sender).Reload();
    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        UpdateRoundedClip();
        UpdateMotion();
        Reload();
    }
    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        // WinUI 快速移出并重新挂入时，旧 Unloaded 可以晚于新 Loaded 到达。
        // 此时不能释放已经重新加载的图片和裁切；真正离树的控件仍立即清理。
        if (!IsLoaded) ReleaseLoadedResources();
        else if (!_dispatcher.TryEnqueue(() => { if (!IsLoaded) ReleaseLoadedResources(); })) ReleaseLoadedResources();
    }
    private void ReleaseLoadedResources()
    {
        Unsubscribe();
        Cancel();
        SetSource(null);
        ReleaseMotion();
        if (_coverVisual is not null) _coverVisual.Clip = null;
        _roundedClip?.Dispose();
        _roundedGeometry?.Dispose();
        _roundedClip = null;
        _roundedGeometry = null;
        _coverVisual = null;
    }
    private static void TransitionChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) => ((MusicCoverControl)sender).UpdateMotion();
    private void UpdateMotion()
    {
        if (!IsLoaded) return;
        if (EnableCarouselTransition)
        {
            _motionSettings ??= new UISettings();
            if (_motion is null)
            {
                _motion = new StoreCarouselImageMotion(CurrentLayer, PreviousLayer);
                _motion.Completed += OnMotionCompleted;
            }
            try { _motion.Attach(); }
            catch (Exception ex)
            {
                ReleaseMotion();
                App.GetLogger<MusicCoverControl>().LogWarning(ex, "轮播图片动画不可用，保留静态封面");
            }
        }
        else
        {
            ReleaseMotion();
            ClearPrevious();
        }
    }
    private void Cover_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        // 无封面占位音符随卡片等比缩放；固定 44px 在轮播拼贴大图上几乎不可见，像加载失败。
        UpdateRoundedClip();
        CoverPlaceholder.FontSize = PreviousPlaceholder.FontSize = Math.Clamp(Math.Min(args.NewSize.Width, args.NewSize.Height) * .22, 20, 120);
        _motion?.Stop();
        ClearPrevious();
    }
    private void Unsubscribe()
    {
        if (_observed is not null) _observed.PropertyChanged -= MetadataChanged;
        _observed = null;
    }
    private void MetadataChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not (nameof(Music.ImageHash) or nameof(Music.Path))) return;
        // 封面管线（CoverLoadQueue 工作线程）会在此通知里写 ImageHash；在订阅边界切回控件线程，
        // 否则后台线程直接触碰 FrameworkElement 会抛 RPC_E_WRONG_THREAD (0x8001010E) 并中断整条加载链路。
        if (_dispatcher.HasThreadAccess) Reload();
        else _dispatcher.TryEnqueue(Reload);
    }
    private void Reload()
    {
        Cancel();
        Unsubscribe();
        if (!IsLoaded || Music is not { } music)
        {
            SetSource(null);
            return;
        }
        _observed = music;
        music.PropertyChanged += MetadataChanged;
        _load = new CancellationTokenSource();
        _ = LoadAsync(music, _load.Token);
    }
    private async Task LoadAsync(Music music, CancellationToken token)
    {
        ImageSource? source = null;
        try
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    var artwork = await CoverLoadQueue.EnqueueArtworkAsync(music, token, DecodePixelSize);
                    source = artwork.Source;
                    if (token.IsCancellationRequested || !IsLoaded || !ReferenceEquals(music, Music)) return;
                    ArtworkColor = artwork.EdgeColor;
                    ArtworkColorChanged?.Invoke(ArtworkColor);
                    break;
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested && attempt < 2)
                {
                    // 上一个视图的最后一个消费者可能刚取消同封面的共享请求；重新入队一次。
                    await Task.Yield();
                }
            }
            if (token.IsCancellationRequested || !IsLoaded || !ReferenceEquals(music, Music)) return;
            SetSource(source, animate: true);
            source = null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { App.GetLogger<MusicCoverControl>().LogWarning(ex, "首页封面加载失败"); }
        finally { (source as IDisposable)?.Dispose(); }
    }
    private void Cancel()
    {
        _load?.Cancel();
        _load?.Dispose();
        _load = null;
    }
    private void SetSource(ImageSource? source, bool animate = false)
    {
        var previous = CoverImage.Source;
        if (source is not null && ReferenceEquals(previous, source)) return;
        _motion?.Stop();
        ClearPrevious();
        CoverImage.Source = source;
        CoverPlaceholder.Visibility = source is null ? Visibility.Visible : Visibility.Collapsed;
        if (animate && _motion is not null && _motionSettings!.AnimationsEnabled && ActualWidth > 0)
        {
            // 上一张源转交给下层；最多保留当前/上一张两个自有源，换图或卸载时释放。
            PreviousImage.Source = previous;
            PreviousPlaceholder.Visibility = previous is null ? Visibility.Visible : Visibility.Collapsed;
            PreviousLayer.Opacity = 1;
            try { _motion.Start(SlideFromRight, ActualWidth); }
            catch (Exception ex)
            {
                _motion.Stop();
                ClearPrevious();
                App.GetLogger<MusicCoverControl>().LogWarning(ex, "轮播图片过渡失败，直接显示当前封面");
            }
        }
        else DisposeSource(previous);
    }
    private void ClearPrevious()
    {
        var source = PreviousImage.Source;
        PreviousImage.Source = null;
        PreviousPlaceholder.Visibility = Visibility.Collapsed;
        PreviousLayer.Opacity = 0;
        DisposeSource(source);
    }
    private void OnMotionCompleted() => ClearPrevious();

    private void ReleaseMotion()
    {
        if (_motion is null) return;
        _motion.Completed -= OnMotionCompleted;
        _motion.Dispose();
        _motion = null;
    }

    private void UpdateRoundedClip()
    {
        if (!IsLoaded) return;
        if (_coverVisual is null)
        {
            // 连接动画会接管并清除源/目标元素的根 Visual.Clip。
            // 裁切挂在私有内容层，动画仍以整个控件为快照边界。
            _coverVisual = ElementCompositionPreview.GetElementVisual(CoverPresentation);
            _roundedGeometry = _coverVisual.Compositor.CreateRoundedRectangleGeometry();
            _roundedClip = _coverVisual.Compositor.CreateGeometricClip(_roundedGeometry);
            _coverVisual.Clip = _roundedClip;
        }
        _roundedGeometry!.Size = new Vector2((float)ActualWidth, (float)ActualHeight);
        float radius = (float)Math.Min(8, Math.Min(ActualWidth, ActualHeight) / 2);
        _roundedGeometry.CornerRadius = new Vector2(radius);
    }

    private static void DisposeSource(ImageSource? source)
    {
        try { (source as IDisposable)?.Dispose(); }
        catch (Exception ex) { App.GetLogger<MusicCoverControl>().LogDebug(ex, "释放首页封面失败"); }
    }
}
