using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Streams;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.Controls
{
    public sealed partial class ImageSwitcher : UserControl
    {
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

        // ── 私有状态 ──────────────────────────────────────────────────────

        private int _lastHash = 0;                      // 上一次成功显示的 hash
        private CancellationTokenSource? _cts;          // 用于取消正在进行的解码

        // ── 构造 ──────────────────────────────────────────────────────────

        public ImageSwitcher()
        {
            InitializeComponent();
        }

        // ── 依赖属性回调 ──────────────────────────────────────────────────

        private static void OnDependencyPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not ImageSwitcher switcher) return;

            if (e.Property == ImageBytesProperty)
            {
                // 启动异步解码流程（不 await，fire-and-forget via _ = ...）
                _ = switcher.UpdateSourceAsync((byte[]?)e.NewValue);
            }
            else if (e.Property == IsDarkProperty)
            {
                // 主题切换：仅当当前没有有效封面时刷新默认图
                if (switcher._lastHash == 0)
                    _ = switcher.UpdateSourceAsync(switcher.ImageBytes);
            }
        }

        // ── 核心：异步解码 + hash 去重 ────────────────────────────────────

        private async Task UpdateSourceAsync(byte[]? newBytes)
        {
            // 取消上一次尚未完成的解码
            _cts?.Cancel();
            _cts?.Dispose();
            var cts = new CancellationTokenSource();
            _cts = cts;
            var token = cts.Token;

            // ① 判断是否需要更新（hash 去重）
            int newHash = (newBytes is { Length: > 0 }) ? ToolUtils.ComputeFastHash(newBytes) : 0;
            if (newHash != 0 && newHash == _lastHash) return;   // 与上次相同，跳过

            // ② 解码
            ImageSource? imageSource = await DecodeToBitmapAsync(newBytes, token);

            if (token.IsCancellationRequested) return;

            // ③ 解码失败或数据为空 → 读取默认封面
            if (imageSource == null)
            {
                imageSource = await LoadDefaultCoverAsync(token);
                newHash = 0;    // 默认封面不写入 hash，下次仍尝试解码真实数据
            }

            if (token.IsCancellationRequested) return;

            // ④ 更新 hash 并切换图片（必须在 UI 线程）
            _lastHash = newHash;
            UpdateImageSource(imageSource);
        }

        // ── 解码 byte[] → BitmapImage ────────────────────────────────────

        private static async Task<BitmapImage?> DecodeToBitmapAsync(byte[]? bytes, CancellationToken token = default)
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
                await bitmap.SetSourceAsync(stream);
                return bitmap;
            }
            catch
            {
                return null;
            }
        }

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
            catch
            {
                return null;
            }
        }

        // ── 切换图片（调度到 UI 线程后执行动画）────────────────────────────

        private void UpdateImageSource(ImageSource? source)
        {
            // DispatcherQueue 确保在 UI 线程执行
            DispatcherQueue.TryEnqueue(() =>
            {
                switch (SwitchType)
                {
                    case ImageSwitchType.Crossfade:
                        UpdateSourceCrossfade(source);
                        break;
                    case ImageSwitchType.Slide:
                        UpdateSourceSlide(source);
                        break;
                }
            });
        }

        // ── 原有动画逻辑，参数改为传入 ImageSource ────────────────────────

        private void UpdateSourceCrossfade(ImageSource? source)
        {
            LastAlbumArtImage.Source = AlbumArtImage.Source;
            LastAlbumArtImage.TranslationTransition = null;
            LastAlbumArtImage.OpacityTransition = null;
            LastAlbumArtImage.Translation = new();
            LastAlbumArtImage.Opacity = 1;
            LastAlbumArtImage.OpacityTransition = new ScalarTransition { Duration = Constants.Time.AnimationDuration };

            AlbumArtImage.TranslationTransition = null;
            AlbumArtImage.OpacityTransition = null;
            AlbumArtImage.Translation = new();
            AlbumArtImage.Opacity = 0;
            AlbumArtImage.OpacityTransition = new ScalarTransition { Duration = Constants.Time.AnimationDuration };
            AlbumArtImage.Source = source;

            LastAlbumArtImage.Opacity = 0;
            AlbumArtImage.Opacity = 1;
        }

        private void UpdateSourceSlide(ImageSource? source)
        {
            LastAlbumArtImage.Source = AlbumArtImage.Source;
            LastAlbumArtImage.TranslationTransition = null;
            LastAlbumArtImage.OpacityTransition = null;
            LastAlbumArtImage.Translation = new();
            LastAlbumArtImage.Opacity = 1;
            LastAlbumArtImage.TranslationTransition = new Vector3Transition { Duration = Constants.Time.AnimationDuration };
            LastAlbumArtImage.OpacityTransition = new ScalarTransition { Duration = Constants.Time.AnimationDuration };

            AlbumArtImage.TranslationTransition = null;
            AlbumArtImage.OpacityTransition = null;
            AlbumArtImage.Translation = new(-(float)ActualWidth, 0, 0);
            AlbumArtImage.Opacity = 0;
            AlbumArtImage.TranslationTransition = new Vector3Transition { Duration = Constants.Time.AnimationDuration };
            AlbumArtImage.OpacityTransition = new ScalarTransition { Duration = Constants.Time.AnimationDuration };
            AlbumArtImage.Source = source;

            LastAlbumArtImage.Opacity = 0;
            AlbumArtImage.Opacity = 1;
            LastAlbumArtImage.Translation = new(-(float)ActualWidth, 0, 0);
            AlbumArtImage.Translation = new();
        }
    }

    public enum ImageSwitchType : byte
    {
        Crossfade,
        Slide
    }
}