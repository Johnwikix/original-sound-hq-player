using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Utils;

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

        public ImageSwitcher()
        {
            InitializeComponent();
            Loaded += (_, _) => _ = UpdateSourceAsync();
            Unloaded += OnUnloaded;
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            _loadState.Reset();
            AlbumArtImage.Source = null;
            LastAlbumArtImage.Source = null;
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

        private async Task UpdateSourceAsync()
        {
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

            if (hasData)
            {
                string rawPath = ToolUtils.FindRawCachePath(newHash!);
                if (File.Exists(rawPath))
                {
                    try
                    {
                        byte[] rawBytes = await Task.Run(() => File.ReadAllBytes(rawPath), token);
                        imageSource = await ImageHelper.DecodeToBitmapAsync(rawBytes, 0, token);
                    }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex) { _logger.LogError(ex, "ImageSwitcher 从缓存加载失败"); }
                }
            }

            if (token.IsCancellationRequested) return;

            bool usesDefault = imageSource is null;
            if (usesDefault)
            {
                imageSource = await LoadDefaultCoverAsync(isDark, token);
            }

            if (token.IsCancellationRequested || !_loadState.IsCurrent(version)) return;

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
            if (imageSource is not null) _loadState.Commit(version, newHash, usesDefault, isDark);
            else _loadState.Reset();
        }

        private async Task<ImageSource?> LoadDefaultCoverAsync(bool isDark, CancellationToken token)
        {
            try
            {
                string assetName = isDark ? "default_cover_black.png" : "default_cover_white.png";
                var uri = new Uri($"ms-appx:///Assets/{assetName}");

                if (token.IsCancellationRequested) return null;

                var file = await StorageFile.GetFileFromApplicationUriAsync(uri);
                using var stream = await file.OpenReadAsync();

                if (token.IsCancellationRequested) return null;

                var bitmap = new BitmapImage();
                await bitmap.SetSourceAsync(stream);
                return bitmap;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"LoadDefaultCoverAsync 操作失败: {ex.Message}");
                return null;
            }
        }

        private void UpdateSourceCrossfade(ImageSource? source)
        {
            LastAlbumArtImage.Source = null;
            LastAlbumArtImage.Source = AlbumArtImage.Source;
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
            AlbumArtImage.Source = source;

            LastAlbumArtImage.Opacity = 0;
            AlbumArtImage.Opacity = 1;
        }

        private void UpdateSourceSlide(ImageSource? source)
        {
            LastAlbumArtImage.Source = null;
            LastAlbumArtImage.Source = AlbumArtImage.Source;
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
            AlbumArtImage.Source = source;

            LastAlbumArtImage.Opacity = 0;
            AlbumArtImage.Opacity = 1;
            LastAlbumArtImage.Translation = new(-(float)ActualWidth, 0, 0);
            AlbumArtImage.Translation = new();
        }
        private void UpdateSourceScaleInOut(ImageSource? source)
        {
            LastAlbumArtImage.Source = null;
            LastAlbumArtImage.Source = AlbumArtImage.Source;
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

            AlbumArtImage.Source = source;
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
        }
    }

    public enum ImageSwitchType : byte
    {
        Crossfade,
        Slide,
        ScaleInOut
    }
}
