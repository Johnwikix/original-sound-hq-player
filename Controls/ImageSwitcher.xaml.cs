using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.Constants;

namespace WinUIMusicPlayer.Controls
{
    public sealed partial class ImageSwitcher : UserControl
    {
        private static ILogger<ImageSwitcher> _logger = WinUIMusicPlayer.App.GetLogger<ImageSwitcher>();

        public int CornerRadiusAmount
        {
            get => (int)GetValue(CornerRadiusAmountProperty);
            set => SetValue(CornerRadiusAmountProperty, value);
        }
        public static readonly DependencyProperty CornerRadiusAmountProperty =
            DependencyProperty.Register(nameof(CornerRadiusAmount), typeof(int), typeof(ImageSwitcher), new PropertyMetadata(0));

        public int ShadowAmount
        {
            get => (int)GetValue(ShadowAmountProperty);
            set => SetValue(ShadowAmountProperty, value);
        }
        public static readonly DependencyProperty ShadowAmountProperty =
            DependencyProperty.Register(nameof(ShadowAmount), typeof(int), typeof(ImageSwitcher), new PropertyMetadata(0));

        public Stretch Stretch
        {
            get => (Stretch)GetValue(StretchProperty);
            set => SetValue(StretchProperty, value);
        }
        public static readonly DependencyProperty StretchProperty =
            DependencyProperty.Register(nameof(Stretch), typeof(Stretch), typeof(ImageSwitcher), new PropertyMetadata(Stretch.Uniform));

        public ImageSwitchType SwitchType
        {
            get => (ImageSwitchType)GetValue(SwitchTypeProperty);
            set => SetValue(SwitchTypeProperty, value);
        }
        public static readonly DependencyProperty SwitchTypeProperty =
            DependencyProperty.Register(nameof(SwitchType), typeof(ImageSwitchType), typeof(ImageSwitcher), new PropertyMetadata(ImageSwitchType.Crossfade));

        public bool IsDark
        {
            get => (bool)GetValue(IsDarkProperty);
            set => SetValue(IsDarkProperty, value);
        }
        public static readonly DependencyProperty IsDarkProperty =
            DependencyProperty.Register(nameof(IsDark), typeof(bool), typeof(ImageSwitcher),
                new PropertyMetadata(false, OnIsDarkChanged));

        public bool IsActive
        {
            get => (bool)GetValue(IsActiveProperty);
            set => SetValue(IsActiveProperty, value);
        }
        public static readonly DependencyProperty IsActiveProperty =
            DependencyProperty.Register(nameof(IsActive), typeof(bool), typeof(ImageSwitcher),
                new PropertyMetadata(true, OnIsActiveChanged));

        public string? ImageHash
        {
            get => (string?)GetValue(ImageHashProperty);
            set => SetValue(ImageHashProperty, value);
        }
        public static readonly DependencyProperty ImageHashProperty =
            DependencyProperty.Register(nameof(ImageHash), typeof(string), typeof(ImageSwitcher),
                new PropertyMetadata(null, OnImageHashChanged));

        private readonly CoverLoadState _loadState = new();
        private CancellationTokenSource? _cts;
        private DispatcherQueueTimer? _lastSourceClearTimer;

        public ImageSwitcher()
        {
            InitializeComponent();
            Loaded += (_, _) => _ = UpdateSourceAsync();
            Unloaded += OnUnloaded;
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            StopPendingWork(clearLastSource: true);
            _loadState.Reset();
            ReplaceImageSource(AlbumArtImage, null);
            ReplaceImageSource(LastAlbumArtImage, null);
        }

        private static void OnImageHashChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not ImageSwitcher switcher) return;
            _ = switcher.UpdateSourceAsync();
        }

        private static void OnIsDarkChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not ImageSwitcher switcher) return;
            _ = switcher.UpdateSourceAsync();
        }

        private static void OnIsActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not ImageSwitcher switcher) return;

            if ((bool)e.NewValue)
            {
                _ = switcher.UpdateSourceAsync();
            }
            else
            {
                // Detail 页收起时保留当前可见图，快速恢复可直接继续显示；
                // 取消后台读取并释放过渡层的旧图，避免隐藏页继续持有大封面。
                switcher.StopPendingWork(clearLastSource: true);
            }
        }

        private async Task UpdateSourceAsync()
        {
            if (!IsActive) return;

            string? newHash = ImageHash;
            bool hasData = newHash is { Length: > 0 };
            bool isDark = IsDark;

            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            if (!_loadState.Begin(newHash, isDark, out int version)) return;
            var cts = new CancellationTokenSource();
            _cts = cts;
            var token = cts.Token;

            ImageSource? imageSource = null;
            try
            {
                if (hasData)
                {
                    string rawPath = ToolUtils.FindRawCachePath(newHash!);
                    if (File.Exists(rawPath))
                    {
                        try
                        {
                            string coverPath = await Task.Run(
                                () => PlaybackCoverImage.GetOrCreateAsync(rawPath, token), token);
                            imageSource = await ImageHelper.DecodeFileToBitmapAsync(coverPath, token);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex) { _logger.LogWarning(ex, "ImageSwitcher 展示缓存不可用，尝试原图"); }
                        if (imageSource is null)
                        {
                            imageSource = await ImageHelper.DecodeFileToBitmapAsync(
                                rawPath, token, PlaybackCoverImage.MaxPixelSize);
                        }
                    }
                }

                token.ThrowIfCancellationRequested();

                bool usesDefault = imageSource is null;
                if (usesDefault)
                    imageSource = await LoadDefaultCoverAsync(isDark, token);

                token.ThrowIfCancellationRequested();
                if (!_loadState.IsCurrent(version)) return;

                switch (SwitchType)
                {
                    case ImageSwitchType.Crossfade:
                        UpdateSourceCrossfade(imageSource);
                        break;
                    case ImageSwitchType.Slide:
                        UpdateSourceSlide(imageSource);
                        break;
                    case ImageSwitchType.ScaleInOut:
                        UpdateSourceScaleInOut(imageSource);
                        break;
                }
                bool hasImage = imageSource is not null;
                // Transfer ownership only after the target control actually holds it.
                if (IsSourceOwned(imageSource))
                    imageSource = null;
                if (hasImage && imageSource is null) _loadState.Commit(version, newHash, usesDefault, isDark);
                else _loadState.Reset();
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                if (!IsSourceOwned(imageSource))
                    DisposeImageSource(imageSource);
            }
        }

        private async Task<ImageSource?> LoadDefaultCoverAsync(bool isDark, CancellationToken token)
        {
            string assetName = isDark ? "default_cover_black.png" : "default_cover_white.png";
            return await ImageHelper.DecodeApplicationAssetAsync(assetName, token);
        }

        private void UpdateSourceCrossfade(ImageSource? source)
        {
            ReplaceImageSource(LastAlbumArtImage, AlbumArtImage.Source);
            LastAlbumArtImage.TranslationTransition = null;
            LastAlbumArtImage.OpacityTransition = null;
            LastAlbumArtImage.Translation = new();
            LastAlbumArtImage.Opacity = 1;
            LastAlbumArtImage.OpacityTransition = TransitionCache.Default;

            AlbumArtImage.TranslationTransition = null;
            AlbumArtImage.OpacityTransition = null;
            AlbumArtImage.Translation = new();
            AlbumArtImage.Opacity = 0;
            AlbumArtImage.OpacityTransition = TransitionCache.Default;
            ReplaceImageSource(AlbumArtImage, source);

            LastAlbumArtImage.Opacity = 0;
            AlbumArtImage.Opacity = 1;
            ScheduleLastSourceClear();
        }

        private void UpdateSourceSlide(ImageSource? source)
        {
            ReplaceImageSource(LastAlbumArtImage, AlbumArtImage.Source);
            LastAlbumArtImage.TranslationTransition = null;
            LastAlbumArtImage.OpacityTransition = null;
            LastAlbumArtImage.Translation = new();
            LastAlbumArtImage.Opacity = 1;
            LastAlbumArtImage.TranslationTransition = TransitionCache.DefaultVector3;
            LastAlbumArtImage.OpacityTransition = TransitionCache.Default;

            AlbumArtImage.TranslationTransition = null;
            AlbumArtImage.OpacityTransition = null;
            AlbumArtImage.Translation = new(-(float)ActualWidth, 0, 0);
            AlbumArtImage.Opacity = 0;
            AlbumArtImage.TranslationTransition = TransitionCache.DefaultVector3;
            AlbumArtImage.OpacityTransition = TransitionCache.Default;
            ReplaceImageSource(AlbumArtImage, source);

            LastAlbumArtImage.Opacity = 0;
            AlbumArtImage.Opacity = 1;
            LastAlbumArtImage.Translation = new(-(float)ActualWidth, 0, 0);
            AlbumArtImage.Translation = new();
            ScheduleLastSourceClear();
        }
        private void UpdateSourceScaleInOut(ImageSource? source)
        {
            ReplaceImageSource(LastAlbumArtImage, AlbumArtImage.Source);
            LastAlbumArtImage.ScaleTransition = null;
            LastAlbumArtImage.OpacityTransition = null;
            AlbumArtImage.ScaleTransition = null;
            AlbumArtImage.OpacityTransition = null;
            LastAlbumArtImage.CenterPoint = new(
                (float)(ActualWidth / 2),
                (float)(ActualHeight / 2),
                0);
            AlbumArtImage.CenterPoint = new(
                (float)(ActualWidth / 2),
                (float)(ActualHeight / 2),
                0);
            LastAlbumArtImage.Scale = new(1f, 1f, 1f);
            LastAlbumArtImage.Opacity = 1f;

            ReplaceImageSource(AlbumArtImage, source);
            AlbumArtImage.Scale = new(0.85f, 0.85f, 1f);
            AlbumArtImage.Opacity = 0f;
            LastAlbumArtImage.ScaleTransition = TransitionCache.DefaultVector3;
            LastAlbumArtImage.OpacityTransition = TransitionCache.Default;
            AlbumArtImage.ScaleTransition = TransitionCache.DefaultVector3;
            AlbumArtImage.OpacityTransition = TransitionCache.Default;
            LastAlbumArtImage.Scale = new(0.8f, 0.8f, 1f);
            LastAlbumArtImage.Opacity = 0f;

            AlbumArtImage.Scale = new(1f, 1f, 1f);
            AlbumArtImage.Opacity = 1f;
            ScheduleLastSourceClear();
        }

        private void StopPendingWork(bool clearLastSource)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            StopLastSourceClearTimer();
            if (clearLastSource)
                ReplaceImageSource(LastAlbumArtImage, null);
        }

        private void ScheduleLastSourceClear()
        {
            if (LastAlbumArtImage.Source is null) return;

            _lastSourceClearTimer ??= DispatcherQueue.CreateTimer();
            _lastSourceClearTimer.Stop();
            _lastSourceClearTimer.Interval = Time.AnimationDuration;
            _lastSourceClearTimer.Tick -= OnLastSourceClearTimerTick;
            _lastSourceClearTimer.Tick += OnLastSourceClearTimerTick;
            _lastSourceClearTimer.Start();
        }

        private void OnLastSourceClearTimerTick(DispatcherQueueTimer sender, object args)
        {
            StopLastSourceClearTimer();
            ReplaceImageSource(LastAlbumArtImage, null);
        }

        private void StopLastSourceClearTimer()
        {
            if (_lastSourceClearTimer is null) return;
            _lastSourceClearTimer.Stop();
            _lastSourceClearTimer.Tick -= OnLastSourceClearTimerTick;
        }

        private void ReplaceImageSource(ShadowImage image, ImageSource? source)
        {
            var previous = image.Source;
            if (ReferenceEquals(previous, source)) return;

            image.Source = null;
            image.Source = source;

            // A source can be temporarily shared by the two transition images.
            // Release it only after neither control owns it anymore.
            if (previous is not null
                && !ReferenceEquals(previous, AlbumArtImage.Source)
                && !ReferenceEquals(previous, LastAlbumArtImage.Source))
            {
                DisposeImageSource(previous);
            }
        }

        private static void DisposeImageSource(ImageSource? source)
        {
            if (source is IDisposable disposable)
                disposable.Dispose();
        }

        private bool IsSourceOwned(ImageSource? source) =>
            source is not null
            && (ReferenceEquals(source, AlbumArtImage.Source)
                || ReferenceEquals(source, LastAlbumArtImage.Source));
    }

    public enum ImageSwitchType : byte
    {
        Crossfade,
        Slide,
        ScaleInOut
    }
}
