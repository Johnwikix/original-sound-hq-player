using Microsoft.Extensions.Logging;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Xaml.Interactivity;
using System;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.Behaviors
{
    public class FadeImageBehaviorWin2d : Behavior<Image>
    {
        private static ILogger<FadeImageBehaviorWin2d> _logger = WinUIMusicPlayer.App.GetLogger<FadeImageBehaviorWin2d>();

        private Storyboard? _currentTransitionStoryboard;
        private Image? _tempOverlayImage;
        private CancellationTokenSource? _cts;

        // Win2D 设备：整个 Behavior 实例共享，避免反复创建开销
        // CanvasDevice 是线程安全的，可跨调用复用
        private CanvasDevice? _canvasDevice;

        private long _lastLength = -1;
        private int _lastHash;

        // ── 去重检测 ───────────────────────────────────────────────────
        private bool IsDuplicateAndUpdate(byte[]? newBytes)
        {
            if (newBytes is not { Length: > 0 })
            {
                bool wasEmpty = _lastLength == 0;
                _lastLength = 0;
                _lastHash = 0;
                return wasEmpty;
            }

            int hash = ToolUtils.ComputeFastHash(newBytes);
            if (newBytes.Length == _lastLength && hash == _lastHash)
                return true;

            _lastLength = newBytes.Length;
            _lastHash = hash;
            return false;
        }

        public void Invalidate()
        {
            _lastLength = -1;
            _lastHash = 0;
        }

        // ── 获取或创建 CanvasDevice（含设备丢失恢复）────────────────────
        private CanvasDevice GetOrCreateDevice()
        {
            if (_canvasDevice == null || _canvasDevice.IsDeviceLost(0))
            {
                _canvasDevice?.Dispose();
                // forceSoftwareRenderer = false：优先使用 GPU 硬件加速
                _canvasDevice = new CanvasDevice(forceSoftwareRenderer: false);
            }
            return _canvasDevice;
        }

        // ── Enable 依赖属性 ────────────────────────────────────────────
        public bool Enable
        {
            get => (bool)GetValue(EnableProperty);
            set => SetValue(EnableProperty, value);
        }

        public static readonly DependencyProperty EnableProperty =
            DependencyProperty.Register(nameof(Enable), typeof(bool), typeof(FadeImageBehavior),
                new PropertyMetadata(true, OnEnableChanged));

        private static void OnEnableChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not FadeImageBehaviorWin2d behavior) return;

            if (!(bool)e.NewValue)
            {
                behavior._cts?.Cancel();
                behavior.StopAndCleanup();
                if (behavior.AssociatedObject != null)
                    behavior.AssociatedObject.Source = null;
            }
            else
            {
                behavior.Invalidate();
                var bytes = behavior.ImageBytes;
                if (behavior.AssociatedObject != null && bytes != null)
                {
                    behavior._cts?.Cancel();
                    behavior._cts = new CancellationTokenSource();
                    _ = behavior.LoadAndTransitionAsync(bytes, behavior._cts.Token);
                }
            }
        }

        // ── ImageBytes 依赖属性 ────────────────────────────────────────
        public byte[] ImageBytes
        {
            get => (byte[])GetValue(ImageBytesProperty);
            set => SetValue(ImageBytesProperty, value);
        }

        public static readonly DependencyProperty ImageBytesProperty =
            DependencyProperty.Register(nameof(ImageBytes), typeof(byte[]), typeof(FadeImageBehavior),
                new PropertyMetadata(null, OnImageBytesChanged));

        private static async void OnImageBytesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not FadeImageBehaviorWin2d behavior) return;

            if (!behavior.Enable)
            {
                behavior.StopAndCleanup();
                if (behavior.AssociatedObject != null)
                    behavior.AssociatedObject.Source = null;
                return;
            }

            var newBytes = e.NewValue as byte[];
            if (behavior.IsDuplicateAndUpdate(newBytes)) return;

            behavior._cts?.Cancel();
            behavior._cts = new CancellationTokenSource();
            var token = behavior._cts.Token;

            try
            {
                await behavior.LoadAndTransitionAsync(newBytes, token);
            }
            catch (OperationCanceledException) { }
        }

        // ── Duration 依赖属性 ──────────────────────────────────────────
        public Duration Duration
        {
            get => (Duration)GetValue(DurationProperty);
            set => SetValue(DurationProperty, value);
        }

        public static readonly DependencyProperty DurationProperty =
            DependencyProperty.Register(nameof(Duration), typeof(Duration), typeof(FadeImageBehavior),
                new PropertyMetadata(new Duration(TimeSpan.FromMilliseconds(500))));

        // ── DecodePixelWidth：0 = 不缩放 ──────────────────────────────
        public int DecodePixelWidth
        {
            get => (int)GetValue(DecodePixelWidthProperty);
            set => SetValue(DecodePixelWidthProperty, value);
        }

        public static readonly DependencyProperty DecodePixelWidthProperty =
            DependencyProperty.Register(nameof(DecodePixelWidth), typeof(int), typeof(FadeImageBehavior),
                new PropertyMetadata(0));

        // ── 核心解码：Win2D CanvasBitmap + Cubic 缩放 ─────────────────
        /// <summary>
        /// 解码流程：
        ///   1. 后台线程：IRandomAccessStream → CanvasBitmap（GPU 纹理，硬件解码）
        ///   2. 若需缩放：ScaleEffect（InterpolationMode = HighQualityCubic）离屏渲染
        ///      → 输出写入目标尺寸的 CanvasRenderTarget（保持在 GPU 内存）
        ///   3. UI 线程：CanvasBitmap/CanvasRenderTarget → SoftwareBitmap（仅在需要
        ///      与 XAML Image.Source 兼容时才回读 CPU），再包装为 SoftwareBitmapSource
        ///
        /// 内存说明：
        ///   - InMemoryRandomAccessStream 用 using 确保立即释放（原始字节已在 bytes[] 里）
        ///   - 缩放后的 CanvasRenderTarget 用完即 Dispose，不长期持有
        ///   - SoftwareBitmapSource 持有 GPU 副本，CPU 端 SoftwareBitmap 解绑后立即释放
        /// </summary>
        private async Task<SoftwareBitmapSource?> DecodeWithWin2DAsync(
            byte[]? bytes, CancellationToken token)
        {
            if (bytes is not { Length: > 0 }) return null;

            try
            {
                var device = GetOrCreateDevice();

                // ① 从字节数组创建内存流，解码为 CanvasBitmap（GPU 纹理）
                CanvasBitmap sourceBitmap;
                using (var stream = new InMemoryRandomAccessStream())
                {
                    await stream.WriteAsync(bytes.AsBuffer());
                    stream.Seek(0);

                    if (token.IsCancellationRequested) return null;

                    // CanvasBitmap.LoadAsync 在内部完成硬件解码，走 WIC → D2D 路径
                    // DpiX/DpiY 传 96 以使逻辑像素 = 物理像素，避免 DPI 缩放干扰后续计算
                    sourceBitmap = await CanvasBitmap.LoadAsync(device, stream, 96f);
                }
                // using 结束，InMemoryRandomAccessStream 已释放 ↑

                if (token.IsCancellationRequested)
                {
                    sourceBitmap.Dispose();
                    return null;
                }

                // ② 判断是否需要缩放
                int targetWidth = DecodePixelWidth;
                bool needsScale = targetWidth > 0 &&
                                  sourceBitmap.SizeInPixels.Width > (uint)targetWidth;

                SoftwareBitmapSource resultSource;

                if (needsScale)
                {
                    // ③-A Cubic 缩放：等比算目标高度
                    double scale = (double)targetWidth / sourceBitmap.SizeInPixels.Width;
                    int targetHeight = (int)Math.Round(sourceBitmap.SizeInPixels.Height * scale);

                    // ScaleEffect 走 D2D HighQualityCubic：
                    //   等价于 D2D1_SCALE_INTERPOLATION_MODE_HIGH_QUALITY_CUBIC，
                    //   内部做 box filter 预处理，大幅缩小时无锯齿且比 Fant 更快（GPU 并行）
                    using var scaleEffect = new ScaleEffect
                    {
                        Source = sourceBitmap,
                        Scale = new System.Numerics.Vector2((float)scale),
                        InterpolationMode = CanvasImageInterpolation.HighQualityCubic,
                        BorderMode = EffectBorderMode.Hard   // 边缘不渗色
                    };

                    // CanvasRenderTarget 在 GPU 上分配目标尺寸帧缓冲
                    using var renderTarget = new CanvasRenderTarget(device, targetWidth, targetHeight, 96f);
                    using (var ds = renderTarget.CreateDrawingSession())
                    {
                        ds.Clear(Microsoft.UI.Colors.Transparent);
                        ds.DrawImage(scaleEffect);
                    }
                    // scaleEffect 和 ds 已 Dispose ↑，只保留 renderTarget 的内容

                    if (token.IsCancellationRequested)
                    {
                        sourceBitmap.Dispose();
                        return null;
                    }

                    // ④-A GPU → CPU（SoftwareBitmap）→ SoftwareBitmapSource
                    //   GetPixelBytes 将 GPU 纹理回读到 CPU，仅此处有一次回读开销
                    using var sb = await SoftwareBitmap.CreateCopyFromSurfaceAsync(renderTarget);
                    // renderTarget Dispose 在 using 结束时释放 GPU 帧缓冲 ↑

                    resultSource = await WrapSoftwareBitmapAsync(sb, token);
                }
                else
                {
                    // ③-B 无需缩放，直接回读
                    using var sb = await SoftwareBitmap.CreateCopyFromSurfaceAsync(sourceBitmap);
                    resultSource = await WrapSoftwareBitmapAsync(sb, token);
                }

                sourceBitmap.Dispose(); // GPU 纹理使命完成，释放
                return resultSource;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (_canvasDevice?.IsDeviceLost(ex.HResult) == true)
                {
                    _canvasDevice.Dispose();
                    _canvasDevice = null;
                }
                _logger.LogError(ex, $"DecodeWithWin2DAsync 操作失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 将 SoftwareBitmap（Bgra8 Premultiplied）包装为 SoftwareBitmapSource。
        /// SoftwareBitmapSource.SetBitmapAsync 必须在 UI 线程上调用。
        /// </summary>
        private static async Task<SoftwareBitmapSource> WrapSoftwareBitmapAsync(
            SoftwareBitmap softwareBitmap, CancellationToken token)
        {
            // 确保格式兼容 SoftwareBitmapSource（要求 Bgra8 + Premultiplied）
            SoftwareBitmap compatible;
            if (softwareBitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8 ||
                softwareBitmap.BitmapAlphaMode != BitmapAlphaMode.Premultiplied)
            {
                compatible = SoftwareBitmap.Convert(
                    softwareBitmap,
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied);
            }
            else
            {
                compatible = softwareBitmap;
            }

            var source = new SoftwareBitmapSource();
            await source.SetBitmapAsync(compatible); // UI 线程，上传 GPU 纹理
            // SetBitmapAsync 完成后 source 持有独立 GPU 副本，compatible 可释放
            if (!ReferenceEquals(compatible, softwareBitmap))
                compatible.Dispose();

            return source;
        }

        // ── 公共加载+过渡逻辑 ──────────────────────────────────────────
        private async Task LoadAndTransitionAsync(byte[]? bytes, CancellationToken token)
        {
            try
            {
                var source = await DecodeWithWin2DAsync(bytes, token);
                if (!token.IsCancellationRequested && source != null)
                    TransitionToNewSource(source);
                else
                    source?.Dispose();
            }
            catch (OperationCanceledException) { }
        }

        // ── 生命周期 ───────────────────────────────────────────────────
        protected override void OnAttached()
        {
            base.OnAttached();
            if (AssociatedObject != null && ImageBytes != null && Enable)
                _ = InitAsync();
        }

        private async Task InitAsync()
        {
            _cts = new CancellationTokenSource();
            var source = await DecodeWithWin2DAsync(ImageBytes, _cts.Token);
            if (AssociatedObject != null && source != null)
                AssociatedObject.Source = source;
            else
                source?.Dispose();
        }

        protected override void OnDetaching()
        {
            _cts?.Cancel();
            StopAndCleanup();
            _canvasDevice?.Dispose();
            _canvasDevice = null;
            base.OnDetaching();
        }

        // ── 淡入淡出过渡 ───────────────────────────────────────────────
        private void TransitionToNewSource(ImageSource newSource)
        {
            if (AssociatedObject == null) return;

            var parent = VisualTreeHelper.GetParent(AssociatedObject) as Panel;
            if (parent == null || AssociatedObject.Visibility == Visibility.Collapsed)
            {
                ReplaceAndDispose(newSource);
                return;
            }

            StopAndCleanup();

            if (AssociatedObject.Source != null)
            {
                _tempOverlayImage = new Image
                {
                    Source = AssociatedObject.Source,
                    Stretch = AssociatedObject.Stretch,
                    HorizontalAlignment = AssociatedObject.HorizontalAlignment,
                    VerticalAlignment = AssociatedObject.VerticalAlignment,
                    Opacity = 1,
                    IsHitTestVisible = false
                };

                Canvas.SetZIndex(_tempOverlayImage, Canvas.GetZIndex(AssociatedObject) + 1);
                parent.Children.Add(_tempOverlayImage);

                var ani = new DoubleAnimation
                {
                    From = 1,
                    To = 0,
                    Duration = Duration.TimeSpan,
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };

                _currentTransitionStoryboard = new Storyboard();
                _currentTransitionStoryboard.Children.Add(ani);
                Storyboard.SetTarget(ani, _tempOverlayImage);
                Storyboard.SetTargetProperty(ani, "Opacity");

                // 动画结束：移除覆盖层并释放其持有的旧 SoftwareBitmapSource（GPU 纹理）
                _currentTransitionStoryboard.Completed += (s, e) => StopAndCleanup();

                AssociatedObject.Source = newSource;
                _currentTransitionStoryboard.Begin();
            }
            else
            {
                AssociatedObject.Source = newSource;
            }
        }

        /// <summary>切换 Source 的同时 Dispose 旧的 SoftwareBitmapSource。</summary>
        private void ReplaceAndDispose(ImageSource newSource)
        {
            var old = AssociatedObject!.Source as SoftwareBitmapSource;
            AssociatedObject.Source = newSource;
            old?.Dispose();
        }

        private void StopAndCleanup()
        {
            _currentTransitionStoryboard?.Stop();
            _currentTransitionStoryboard = null;

            if (_tempOverlayImage != null)
            {
                var parent = VisualTreeHelper.GetParent(_tempOverlayImage) as Panel;
                parent?.Children.Remove(_tempOverlayImage);

                // 释放覆盖层持有的旧 SoftwareBitmapSource（避免 GPU 纹理泄漏）
                (_tempOverlayImage.Source as SoftwareBitmapSource)?.Dispose();
                _tempOverlayImage.Source = null;
                _tempOverlayImage = null;
            }
        }
    }
}