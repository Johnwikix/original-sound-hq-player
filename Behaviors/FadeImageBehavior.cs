using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Xaml.Interactivity;
using System;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.Behaviors
{
    public class FadeImageBehavior : Behavior<Image>
    {
        private static ILogger<FadeImageBehavior> _logger = WinUIMusicPlayer.App.GetLogger<FadeImageBehavior>();

        private Storyboard _currentTransitionStoryboard;
        private Image _tempOverlayImage;
        private CancellationTokenSource _cts;

        private long _lastLength = -1;
        private string? _lastImageHash;

        private bool IsDuplicateAndUpdate(byte[]? newBytes)
        {
            if (newBytes is not { Length: > 0 })
            {
                bool wasEmpty = _lastLength == 0;
                _lastLength = 0;
                _lastImageHash = null;
                return wasEmpty;
            }

            string? imageHash = ImageHash;
            if (imageHash is { Length: > 0 } && imageHash == _lastImageHash)
                return true;

            _lastLength = newBytes.Length;
            _lastImageHash = imageHash;
            return false;
        }

        public void Invalidate()
        {
            _lastLength = -1;
            _lastImageHash = null;
        }

        // ── ImageVisibility 依赖属性 ───────────────────────────────────
        public Visibility ImageVisibility
        {
            get => (Visibility)GetValue(ImageVisibilityProperty);
            private set => SetValue(ImageVisibilityProperty, value);
        }

        public static readonly DependencyProperty ImageVisibilityProperty =
            DependencyProperty.Register(nameof(ImageVisibility), typeof(Visibility), typeof(FadeImageBehavior),
                new PropertyMetadata(Visibility.Collapsed));

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
            if (d is not FadeImageBehavior behavior) return;

            if (!(bool)e.NewValue)
            {
                behavior._cts?.Cancel();
                behavior._cts?.Dispose();
                behavior._cts = null;
                behavior.StopAndCleanup();
                if (behavior.AssociatedObject != null)
                    behavior.SetSource(null);
            }
            else
            {
                behavior.Invalidate();
                var bytes = behavior.ImageBytes;
                if (behavior.AssociatedObject != null && bytes != null)
                {
                    behavior._cts?.Cancel();
                    behavior._cts?.Dispose();
                    behavior._cts = new CancellationTokenSource();
                    var token = behavior._cts.Token;
                    _ = behavior.LoadAndTransitionAsync(bytes, token);
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

        // ── ImageHash 依赖属性 ──────────────────────────────────────────
        public string? ImageHash
        {
            get => (string?)GetValue(ImageHashProperty);
            set => SetValue(ImageHashProperty, value);
        }

        public static readonly DependencyProperty ImageHashProperty =
            DependencyProperty.Register(nameof(ImageHash), typeof(string), typeof(FadeImageBehavior),
                new PropertyMetadata(null));

        private static async void OnImageBytesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not FadeImageBehavior behavior) return;

            if (!behavior.Enable)
            {
                behavior.StopAndCleanup();
                if (behavior.AssociatedObject != null)
                    behavior.SetSource(null);
                return;
            }

            var newBytes = e.NewValue as byte[];
            if (behavior.IsDuplicateAndUpdate(newBytes)) return;

            behavior._cts?.Cancel();
            behavior._cts?.Dispose();
            behavior._cts = new CancellationTokenSource();
            var token = behavior._cts.Token;

            try
            {
                await behavior.LoadAndTransitionAsync(newBytes, token);
            }
            catch (OperationCanceledException) { }
        }

        // ── 公共加载+过渡逻辑 ─────────────────────────────────────────
        private async Task LoadAndTransitionAsync(byte[]? bytes, CancellationToken token)
        {
            try
            {
                var bitmapImage = await DecodeToBitmapAsync(bytes, token);
                if (!token.IsCancellationRequested)
                    TransitionToNewSource(bitmapImage); // null 也传下去
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

        // ── DecodePixelWidth 依赖属性：0 = 按原图解码 ──────────────────
        public int DecodePixelWidth
        {
            get => (int)GetValue(DecodePixelWidthProperty);
            set => SetValue(DecodePixelWidthProperty, value);
        }

        public static readonly DependencyProperty DecodePixelWidthProperty =
            DependencyProperty.Register(nameof(DecodePixelWidth), typeof(int), typeof(FadeImageBehavior),
                new PropertyMetadata(0));

        private Task<BitmapImage?> DecodeToBitmapAsync(byte[]? bytes, CancellationToken token)
            => ImageHelper.DecodeToBitmapAsync(bytes, DecodePixelWidth, token);

        // ── 生命周期 ───────────────────────────────────────────────────
        protected override void OnAttached()
        {
            base.OnAttached();
            if (AssociatedObject != null && Enable)
            {
                if (ImageBytes is { Length: > 0 })
                    _ = InitAsync();
                else
                    SetSource(null);
            }
        }

        private async Task InitAsync()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            var bitmap = await DecodeToBitmapAsync(ImageBytes, _cts.Token);
            if (AssociatedObject != null)
                SetSource(bitmap);
        }

        protected override void OnDetaching()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            StopAndCleanup();
            base.OnDetaching();
        }

        // ── Source 统一设置入口 ────────────────────────────────────────
        private void SetSource(ImageSource? source)
        {
            if (AssociatedObject == null) return;
            AssociatedObject.Source = null;
            AssociatedObject.Source = source;
            ImageVisibility = source != null ? Visibility.Collapsed : Visibility.Visible;
        }

        // ── 淡入淡出过渡 ───────────────────────────────────────────────
        private void TransitionToNewSource(ImageSource? newSource)
        {
            if (AssociatedObject == null) return;
            if (newSource != null && AssociatedObject.Source == newSource) return;

            var parent = VisualTreeHelper.GetParent(AssociatedObject) as Panel;
            if (parent == null || AssociatedObject.Visibility == Visibility.Collapsed)
            {
                SetSource(newSource);
                return;
            }

            StopAndCleanup();

            if (AssociatedObject.Source != null)
            {
                // 旧图 → 新图：叠一层旧图做淡出
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

                SetSource(newSource);
                _currentTransitionStoryboard.Begin();
            }
            else
            {
                // newSource 为 null（清空）或旧图为 null（首次加载）：直接赋值
                SetSource(newSource);
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