using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Streams;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Helpers;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.Controls
{
    public sealed partial class ImageSwitcher : UserControl
    {
        private static ILogger<ImageSwitcher> _logger = WinUIMusicPlayer.App.GetLogger<ImageSwitcher>();

        // ── 现有依赖属性保持不变 ──────────────────────────────────────────

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

        // ── 新增：IsDark 依赖属性 ─────────────────────────────────────────

        /// <summary>
        /// 决定解码失败或数据为空时使用哪张默认封面。
        /// true  → Assets/default_cover_black.png
        /// false → Assets/default_cover_white.png
        /// </summary>
        public bool IsDark
        {
            get => (bool)GetValue(IsDarkProperty);
            set => SetValue(IsDarkProperty, value);
        }
        public static readonly DependencyProperty IsDarkProperty =
            DependencyProperty.Register(nameof(IsDark), typeof(bool), typeof(ImageSwitcher),
                new PropertyMetadata(false, OnDependencyPropertyChanged));

        // ── 新增：Source 改为 byte[]，原 ImageSource 版本移除 ─────────────

        /// <summary>
        /// 封面图片的原始字节数据。设置后控件自动在内部解码并切换图片。
        /// 传入 null 或空数组时显示默认封面。
        /// </summary>
        public byte[]? ImageBytes
        {
            get => (byte[]?)GetValue(ImageBytesProperty);
            set => SetValue(ImageBytesProperty, value);
        }
        public static readonly DependencyProperty ImageBytesProperty =
            DependencyProperty.Register(nameof(ImageBytes), typeof(byte[]), typeof(ImageSwitcher),
                new PropertyMetadata(null, OnDependencyPropertyChanged));

        /// <summary>
        /// 封面图片的 XXHash64 十六进制哈希值，用于去重。
        /// 与 ImageBytes 配合使用，避免对相同图片重复解码。
        /// </summary>
        public string? ImageHash
        {
            get => (string?)GetValue(ImageHashProperty);
            set => SetValue(ImageHashProperty, value);
        }
        public static readonly DependencyProperty ImageHashProperty =
            DependencyProperty.Register(nameof(ImageHash), typeof(string), typeof(ImageSwitcher),
                new PropertyMetadata(null));

        // ── 私有状态 ──────────────────────────────────────────────────────

        private string? _lastImageHash;                 // 上一次成功显示图时的 hash
        private CancellationTokenSource? _cts;          // 用于取消正在进行的解码

        // ── 构造 ──────────────────────────────────────────────────────────

        public ImageSwitcher()
        {
            InitializeComponent();
            Unloaded += OnUnloaded;
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            _lastImageHash = null;
            AlbumArtImage.Source = null;
            LastAlbumArtImage.Source = null;
        }

        // ── 依赖属性回调 ──────────────────────────────────────────────────

        private static void OnDependencyPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not ImageSwitcher switcher) return;

            if (e.Property == ImageBytesProperty)
            {
                _ = switcher.UpdateSourceAsync();
            }
            else if (e.Property == IsDarkProperty)
            {
                if (switcher._lastImageHash is null)
                    _ = switcher.UpdateSourceAsync();
            }
        }

        // ── 核心：异步解码 + hash 去重 ────────────────────────────────────

        private async Task UpdateSourceAsync()
        {
            var newBytes = ImageBytes;
            string? newHash = ImageHash;

            bool hasData = newBytes is { Length: > 0 };
            if (hasData && newHash is { Length: > 0 } && newHash == _lastImageHash) return;
            if (!hasData && _lastImageHash is null) return;

            _cts?.Cancel();
            _cts?.Dispose();
            var cts = new CancellationTokenSource();
            _cts = cts;
            var token = cts.Token;

            ImageSource? imageSource = await DecodeToBitmapAsync(newBytes, token);

            if (token.IsCancellationRequested) return;

            if (imageSource == null)
            {
                imageSource = await LoadDefaultCoverAsync(token);
                newHash = null;
            }

            if (token.IsCancellationRequested) return;

            _lastImageHash = newHash;

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
        }

        private static Task<BitmapImage?> DecodeToBitmapAsync(byte[]? bytes, CancellationToken token = default)
            => ImageHelper.DecodeToBitmapAsync(bytes, 0, token);

        // ── 读取 Assets 中的默认封面 ──────────────────────────────────────

        private async Task<ImageSource?> LoadDefaultCoverAsync(CancellationToken token = default)
        {
            try
            {
                string assetName = IsDark ? "default_cover_black.png" : "default_cover_white.png";
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

        // ── 原有动画逻辑，参数改为传入 ImageSource ────────────────────────

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