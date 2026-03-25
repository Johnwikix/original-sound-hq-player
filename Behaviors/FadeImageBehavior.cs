using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Xaml.Interactivity;
using System;
using System.Threading.Tasks;
using Windows.Storage.Streams;

namespace WinUIMusicPlayer.Behaviors
{
    /// <summary>
    /// 针对 Image 控件的淡入淡出 Behavior
    /// 通过传入 byte[] 并限制解码尺寸为100px，显著降低内存开销
    /// </summary>
    public class FadeImageBehavior : Behavior<Image>
    {
        private Storyboard _currentTransitionStoryboard;
        private Image _tempOverlayImage;

        // ── 公开属性：传入原始图片字节数据 ──────────────────────────────────
        public byte[] ImageBytes
        {
            get => (byte[])GetValue(ImageBytesProperty);
            set => SetValue(ImageBytesProperty, value);
        }

        public static readonly DependencyProperty ImageBytesProperty =
            DependencyProperty.Register(
                nameof(ImageBytes),
                typeof(byte[]),
                typeof(FadeImageBehavior),
                new PropertyMetadata(null, OnImageBytesChanged));

        private static async void OnImageBytesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is FadeImageBehavior behavior)
            {
                var bytes = e.NewValue as byte[];
                var bitmapImage = await behavior.DecodeToBitmapAsync(bytes);
                behavior.TransitionToNewSource(bitmapImage);
            }
        }

        // ── 公开属性：淡出动画时长 ────────────────────────────────────────
        public Duration Duration
        {
            get => (Duration)GetValue(DurationProperty);
            set => SetValue(DurationProperty, value);
        }

        public static readonly DependencyProperty DurationProperty =
            DependencyProperty.Register(
                nameof(Duration),
                typeof(Duration),
                typeof(FadeImageBehavior),
                new PropertyMetadata(new Duration(TimeSpan.FromMilliseconds(500))));

        // ── 解码：限制为100px，节省约95%内存 ─────────────────────────────
        private async Task<BitmapImage> DecodeToBitmapAsync(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
                return null;

            try
            {
                var bitmap = new BitmapImage
                {
                    DecodePixelWidth = 150,
                    // Logical 模式自动适配 DPI 缩放（125%/150%），比 Physical 更安全
                    DecodePixelType = DecodePixelType.Logical
                };

                using var stream = new InMemoryRandomAccessStream();
                using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
                {
                    writer.WriteBytes(bytes);
                    await writer.StoreAsync();
                }
                stream.Seek(0);
                await bitmap.SetSourceAsync(stream);
                return bitmap;
            }
            catch
            {
                // 解码失败（损坏数据、不支持格式等）时静默返回 null
                return null;
            }
        }

        // ── Behavior 生命周期 ─────────────────────────────────────────────
        protected override void OnAttached()
        {
            base.OnAttached();
            // 重新挂载时，确保显示当前 Behavior 记录的最新 Source
            if (AssociatedObject != null && ImageBytes != null)
            {
                // 触发一次解码并更新，保持状态一致
                _ = InitAsync();
            }
        }

        private async Task InitAsync()
        {
            var bitmap = await DecodeToBitmapAsync(ImageBytes);
            if (AssociatedObject != null)
                AssociatedObject.Source = bitmap;
        }

        protected override void OnDetaching()
        {
            StopAndCleanup();
            base.OnDetaching();
        }

        // ── 核心：淡出过渡动画 ────────────────────────────────────────────
        private void TransitionToNewSource(ImageSource newSource)
        {
            if (AssociatedObject == null) return;

            // 新旧引用相同时跳过（byte[] 去重应在 ViewModel 层控制，此处仅做引用比较兜底）
            if (AssociatedObject.Source == newSource) return;

            var parent = VisualTreeHelper.GetParent(AssociatedObject) as Panel;
            if (parent == null)
            {
                AssociatedObject.Source = newSource;
                return;
            }

            // 控件不可见时直接更新，不做动画
            if (AssociatedObject.Visibility == Visibility.Collapsed)
            {
                AssociatedObject.Source = newSource;
                return;
            }

            StopAndCleanup();

            if (AssociatedObject.Source != null)
            {
                // 用临时层承载旧图，执行淡出动画
                _tempOverlayImage = new Image
                {
                    Source = AssociatedObject.Source,
                    Stretch = AssociatedObject.Stretch,
                    HorizontalAlignment = AssociatedObject.HorizontalAlignment,
                    VerticalAlignment = AssociatedObject.VerticalAlignment,
                    Opacity = 1,
                    IsHitTestVisible = false
                };

                int currentZIndex = Canvas.GetZIndex(AssociatedObject);
                Canvas.SetZIndex(_tempOverlayImage, currentZIndex + 1);
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

                _currentTransitionStoryboard.Completed += (s, e) => StopAndCleanup();

                // 底层原图立即切换为新图，旧图叠在上方淡出
                AssociatedObject.Source = newSource;
                _currentTransitionStoryboard.Begin();
            }
            else
            {
                // 旧图为空时直接设置新图，无需动画
                AssociatedObject.Source = newSource;
            }
        }

        // ── 清理：停止动画并释放临时图层资源 ─────────────────────────────
        private void StopAndCleanup()
        {
            if (_currentTransitionStoryboard != null)
            {
                _currentTransitionStoryboard.Stop();
                _currentTransitionStoryboard = null;
            }

            if (_tempOverlayImage != null)
            {
                var parent = VisualTreeHelper.GetParent(_tempOverlayImage) as Panel;
                parent?.Children.Remove(_tempOverlayImage);

                // 断开 ImageSource 引用，允许 GC 回收旧图内存
                _tempOverlayImage.Source = null;
                _tempOverlayImage = null;
            }
        }
    }
}