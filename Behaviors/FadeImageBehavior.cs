using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Xaml.Interactivity;
using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage.Streams;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.Behaviors
{
    public class FadeImageBehavior : Behavior<Image>
    {
        private Storyboard _currentTransitionStoryboard;
        private Image _tempOverlayImage;
        private CancellationTokenSource _cts; // 用于取消正在进行的解码任务
        private static long _lastLength = -1;
        private static int _lastHash;

        public static bool IsDuplicateAndUpdate(byte[]? newBytes)
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

        public static void Invalidate()
        {
            _lastLength = -1;
            _lastHash = 0;
        }

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
            if (d is FadeImageBehavior behavior)
            {
                var newBytes = e.NewValue as byte[];
                if (IsDuplicateAndUpdate(newBytes)) return;

                behavior._cts?.Cancel();
                behavior._cts = new CancellationTokenSource();
                var token = behavior._cts.Token;

                try
                {
                    // DecodeToBitmapAsync 内部已有 DecodePixelWidth 限制
                    // 这确保了即便原图 3000px，进入 GPU 的纹理也只有 150px 左右
                    var bitmapImage = await behavior.DecodeToBitmapAsync(newBytes, token);
                    if (!token.IsCancellationRequested && bitmapImage != null)
                    {
                        behavior.TransitionToNewSource(bitmapImage);
                    }
                }
                catch (OperationCanceledException) { }
            }
        }

        public Duration Duration
        {
            get => (Duration)GetValue(DurationProperty);
            set => SetValue(DurationProperty, value);
        }

        public static readonly DependencyProperty DurationProperty =
            DependencyProperty.Register(nameof(Duration), typeof(Duration), typeof(FadeImageBehavior),
                new PropertyMetadata(new Duration(TimeSpan.FromMilliseconds(500))));

        // ── 优化后的解码：减少中间拷贝 ─────────────────────────────
        private async Task<BitmapImage> DecodeToBitmapAsync(byte[] bytes, CancellationToken token)
        {
            if (bytes == null || bytes.Length == 0) return null;

            try
            {
                // 1. 直接通过 MemoryStream 转换，避免 DataWriter 的二次拷贝
                using var stream = new InMemoryRandomAccessStream();
                await stream.WriteAsync(bytes.AsBuffer());
                stream.Seek(0);

                if (token.IsCancellationRequested) return null;

                var bitmap = new BitmapImage
                {
                    DecodePixelWidth = 150, // 维持原有的低内存解码策略
                    DecodePixelType = DecodePixelType.Logical
                };

                await bitmap.SetSourceAsync(stream);
                return bitmap;
            }
            catch { return null; }
        }

        protected override void OnAttached()
        {
            base.OnAttached();
            if (AssociatedObject != null && ImageBytes != null) _ = InitAsync();
        }

        private async Task InitAsync()
        {
            _cts = new CancellationTokenSource();
            var bitmap = await DecodeToBitmapAsync(ImageBytes, _cts.Token);
            if (AssociatedObject != null && bitmap != null) AssociatedObject.Source = bitmap;
        }

        protected override void OnDetaching()
        {
            _cts?.Cancel();
            StopAndCleanup();
            base.OnDetaching();
        }

        private void TransitionToNewSource(ImageSource newSource)
        {
            if (AssociatedObject == null || AssociatedObject.Source == newSource) return;

            var parent = VisualTreeHelper.GetParent(AssociatedObject) as Panel;
            if (parent == null || AssociatedObject.Visibility == Visibility.Collapsed)
            {
                AssociatedObject.Source = newSource;
                return;
            }

            // 在创建新动画层前，清理之前的动画（如果上一个淡出还没结束）
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

                // 关键点：底层切换新图，断开旧图引用
                AssociatedObject.Source = newSource;
                _currentTransitionStoryboard.Begin();
            }
            else
            {
                AssociatedObject.Source = newSource;
            }
        }

        private void StopAndCleanup()
        {
            _currentTransitionStoryboard?.Stop();
            _currentTransitionStoryboard = null;

            if (_tempOverlayImage != null)
            {
                var parent = VisualTreeHelper.GetParent(_tempOverlayImage) as Panel;
                parent?.Children.Remove(_tempOverlayImage);

                // 强制解除 Source 绑定，利于垃圾回收
                _tempOverlayImage.Source = null;
                _tempOverlayImage = null;
            }
        }
    }
}