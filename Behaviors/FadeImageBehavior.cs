using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Xaml.Interactivity;
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
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
        private string? _lastImageHash;

        public void Invalidate()
        {
            _lastImageHash = null;
        }

        public Visibility ImageVisibility
        {
            get => (Visibility)GetValue(ImageVisibilityProperty);
            private set => SetValue(ImageVisibilityProperty, value);
        }

        public static readonly DependencyProperty ImageVisibilityProperty =
            DependencyProperty.Register(nameof(ImageVisibility), typeof(Visibility), typeof(FadeImageBehavior),
                new PropertyMetadata(Visibility.Collapsed));

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
                string? hash = behavior.ImageHash;
                if (behavior.AssociatedObject != null && hash is { Length: > 0 })
                {
                    behavior._cts?.Cancel();
                    behavior._cts?.Dispose();
                    behavior._cts = new CancellationTokenSource();
                    _ = behavior.LoadFromCacheAndTransitionAsync(hash, behavior._cts.Token);
                }
            }
        }

        public string? ImageHash
        {
            get => (string?)GetValue(ImageHashProperty);
            set => SetValue(ImageHashProperty, value);
        }

        public static readonly DependencyProperty ImageHashProperty =
            DependencyProperty.Register(nameof(ImageHash), typeof(string), typeof(FadeImageBehavior),
                new PropertyMetadata(null, OnImageHashChanged));

        private static async void OnImageHashChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not FadeImageBehavior behavior) return;

            if (!behavior.Enable)
            {
                behavior.StopAndCleanup();
                if (behavior.AssociatedObject != null)
                    behavior.SetSource(null);
                return;
            }

            string? newHash = e.NewValue as string;
            if (newHash is { Length: > 0 } && newHash == behavior._lastImageHash)
                return;

            if (newHash is not { Length: > 0 })
            {
                behavior._lastImageHash = null;
                return;
            }

            behavior._lastImageHash = newHash;

            behavior._cts?.Cancel();
            behavior._cts?.Dispose();
            behavior._cts = new CancellationTokenSource();
            var token = behavior._cts.Token;

            try
            {
                await behavior.LoadFromCacheAndTransitionAsync(newHash, token);
            }
            catch (OperationCanceledException) { }
        }

        public Duration Duration
        {
            get => (Duration)GetValue(DurationProperty);
            set => SetValue(DurationProperty, value);
        }

        public static readonly DependencyProperty DurationProperty =
            DependencyProperty.Register(nameof(Duration), typeof(Duration), typeof(FadeImageBehavior),
                new PropertyMetadata(new Duration(TimeSpan.FromMilliseconds(500))));

        private async Task LoadFromCacheAndTransitionAsync(string hash, CancellationToken token)
        {
            try
            {
                ImageSource? source = null;

                string thumbPath = CoverLoadQueue.GetThumbCachePath(hash, CoverLoadQueue.CoverSize);
                if (File.Exists(thumbPath))
                {
                    source = await LoadThumbFromCacheAsync(thumbPath, token);
                }

                if (source is null)
                {
                    string rawPath = ToolUtils.GetRawCachePath(hash);
                    if (File.Exists(rawPath))
                    {
                        byte[] rawBytes = await File.ReadAllBytesAsync(rawPath, token);
                        source = await ImageHelper.DecodeToBitmapAsync(rawBytes, 150, token);
                    }
                }

                if (!token.IsCancellationRequested)
                    TransitionToNewSource(source);
            }
            catch (OperationCanceledException) { }
        }

        private static async Task<ImageSource?> LoadThumbFromCacheAsync(string cachePath, CancellationToken token)
        {
            byte[]? pixelRented = null;
            var header = ArrayPool<byte>.Shared.Rent(54);
            try
            {
                int w, h, pixelBytes;

                try
                {
                    await using (var fs = new FileStream(
                        cachePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                        bufferSize: 4096, useAsync: true))
                    {
                        if (fs.Length < 58) return null;

                        await fs.ReadExactlyAsync(header.AsMemory(0, 54), token);

                        var h0 = header.AsSpan(0, 54);
                        if (h0[0] != (byte)'B' || h0[1] != (byte)'M') return null;

                        w = BinaryPrimitives.ReadInt32LittleEndian(h0[18..]);
                        h = Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(h0[22..]));
                        if (w <= 0 || h <= 0 || w > 4096 || h > 4096) return null;

                        pixelBytes = w * h * 4;
                        if (fs.Length < 54 + pixelBytes) return null;

                        pixelRented = ArrayPool<byte>.Shared.Rent(pixelBytes);
                        await fs.ReadExactlyAsync(pixelRented.AsMemory(0, pixelBytes), token);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(header, clearArray: false);
                }

                using var softwareBitmap = new SoftwareBitmap(
                    BitmapPixelFormat.Bgra8, w, h, BitmapAlphaMode.Premultiplied);
                softwareBitmap.CopyFromBuffer(pixelRented.AsBuffer(0, pixelBytes));
                var source = new SoftwareBitmapSource();
                await source.SetBitmapAsync(softwareBitmap);
                return source;
            }
            catch (OperationCanceledException) { return null; }
            catch { return null; }
            finally
            {
                if (pixelRented is not null)
                    ArrayPool<byte>.Shared.Return(pixelRented, clearArray: false);
            }
        }

        protected override void OnAttached()
        {
            base.OnAttached();
            if (AssociatedObject != null)
            {
                AssociatedObject.Unloaded += OnUnloaded;
                if (Enable && ImageHash is { Length: > 0 } hash)
                    _ = InitAsync(hash);
                else
                    SetSource(null);
            }
        }

        private async Task InitAsync(string hash)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            await LoadFromCacheAndTransitionAsync(hash, _cts.Token);
        }

        protected override void OnDetaching()
        {
            if (AssociatedObject != null)
                AssociatedObject.Unloaded -= OnUnloaded;
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            StopAndCleanup();
            base.OnDetaching();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            StopAndCleanup();
        }

        private void SetSource(ImageSource? source)
        {
            if (AssociatedObject == null) return;
            AssociatedObject.Source = null;
            AssociatedObject.Source = source;
            ImageVisibility = source != null ? Visibility.Collapsed : Visibility.Visible;
        }

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
                _currentTransitionStoryboard.Completed += OnTransitionCompleted;

                SetSource(newSource);
                _currentTransitionStoryboard.Begin();
            }
            else
            {
                SetSource(newSource);
            }
        }

        private void OnTransitionCompleted(object? sender, object e) => StopAndCleanup();

        private void StopAndCleanup()
        {
            if (_currentTransitionStoryboard != null)
            {
                _currentTransitionStoryboard.Completed -= OnTransitionCompleted;
                _currentTransitionStoryboard.Stop();
            }
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
