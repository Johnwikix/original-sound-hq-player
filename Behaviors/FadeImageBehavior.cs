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
        private CancellationTokenSource _cts;

        // ── 去重状态改为实例字段，各控件独立判断 ──────────────────────
        private long _lastLength = -1;
        private int _lastHash;

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
            if (d is FadeImageBehavior behavior)
            {
                var newBytes = e.NewValue as byte[];
                if (behavior.IsDuplicateAndUpdate(newBytes)) return;  // 实例方法，不再跨控件共享

                behavior._cts?.Cancel();
                behavior._cts = new CancellationTokenSource();
                var token = behavior._cts.Token;

                try
                {
                    var bitmapImage = await behavior.DecodeToBitmapAsync(newBytes, token);
                    if (!token.IsCancellationRequested && bitmapImage != null)
                    {
                        behavior.TransitionToNewSource(bitmapImage);
                    }
                }
                catch (OperationCanceledException) { }
            }
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

        // ── DecodePixelWidth 依赖属性：0 = 按原图解码 ──────────────────
        public int DecodePixelWidth
        {
            get => (int)GetValue(DecodePixelWidthProperty);
            set => SetValue(DecodePixelWidthProperty, value);
        }

        public static readonly DependencyProperty DecodePixelWidthProperty =
            DependencyProperty.Register(nameof(DecodePixelWidth), typeof(int), typeof(FadeImageBehavior),
                new PropertyMetadata(0));   // 0 = 原图大小

        // ── 解码 ───────────────────────────────────────────────────────
        private async Task<BitmapImage> DecodeToBitmapAsync(byte[] bytes, CancellationToken token)
        {
            if (bytes == null || bytes.Length == 0) return null;

            try
            {
                using var stream = new InMemoryRandomAccessStream();
                await stream.WriteAsync(bytes.AsBuffer());
                stream.Seek(0);

                if (token.IsCancellationRequested) return null;

                var bitmap = new BitmapImage
                {
                    DecodePixelType = DecodePixelType.Logical
                };

                // DecodePixelWidth > 0 时才限制宽度，否则让 WIC 按原图解码
                int decodeWidth = DecodePixelWidth;
                if (decodeWidth > 0)
                    bitmap.DecodePixelWidth = decodeWidth;

                await bitmap.SetSourceAsync(stream);
                return bitmap;
            }
            catch { return null; }
        }

        // ── 生命周期 ───────────────────────────────────────────────────
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

        // ── 淡入淡出过渡 ───────────────────────────────────────────────
        private void TransitionToNewSource(ImageSource newSource)
        {
            if (AssociatedObject == null || AssociatedObject.Source == newSource) return;

            var parent = VisualTreeHelper.GetParent(AssociatedObject) as Panel;
            if (parent == null || AssociatedObject.Visibility == Visibility.Collapsed)
            {
                AssociatedObject.Source = newSource;
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
                _tempOverlayImage.Source = null;
                _tempOverlayImage = null;
            }
        }
    }
}