using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
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
    private Music? _observed;
    private CancellationTokenSource? _load;
    /// <summary>获取或设置封面所属曲目。</summary>
    public Music? Music { get => (Music?)GetValue(MusicProperty); set => SetValue(MusicProperty, value); }
    /// <summary>获取或设置解码尺寸。</summary>
    public int DecodePixelSize { get => (int)GetValue(DecodePixelSizeProperty); set => SetValue(DecodePixelSizeProperty, value); }
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
    }
    private static void MusicChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) => ((MusicCoverControl)sender).Reload();
    private void OnLoaded(object sender, RoutedEventArgs args) => Reload();
    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        Unsubscribe();
        Cancel();
        SetSource(null);
    }
    private void Unsubscribe()
    {
        if (_observed is not null) _observed.PropertyChanged -= MetadataChanged;
        _observed = null;
    }
    private void MetadataChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(Music.ImageHash) or nameof(Music.Path)) Reload();
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
            SetSource(source);
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
    private void SetSource(ImageSource? source)
    {
        var previous = CoverImage.Source;
        if (ReferenceEquals(previous, source)) return;
        CoverImage.Source = source;
        (previous as IDisposable)?.Dispose();
    }
}
