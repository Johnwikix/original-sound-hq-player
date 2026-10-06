using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI.Xaml;
using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.DirectX;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace AnimatedWin2dControls.Renderer.Background
{
    /// <summary>
    /// 在 NowPlayingCanvas 的同一交换链中绘制窗口图片。
    /// 这不是歌词区域的背景副本：图片始终按整个画布的 UniformToFill 绘制，随后由
    /// LyricsRenderCoordinator 在同一 DrawingSession 中绘制歌词，避免交换链透明合成限制。
    /// </summary>
    internal sealed class ImageBackgroundRenderer : BaseBackgroundRenderer
    {
        private readonly object _pendingGate = new();
        private string _sourcePath = string.Empty;
        private double _blurAmount = 20;
        private int _sourceVersion;
        private int _loadedVersion = -1;
        private PendingImage? _pending;
        private CanvasBitmap? _bitmap;
        private GaussianBlurEffect? _blur;
        private bool _resourcesInvalid = true;
        private bool _disposed;

        private sealed record PendingImage(byte[] Pixels, int Width, int Height, int Version);

        public ImageBackgroundRenderer(string sourcePath, double blurAmount)
        {
            SetSource(sourcePath, blurAmount);
        }

        public void SetSource(string? sourcePath, double blurAmount)
        {
            int version;
            lock (_pendingGate)
            {
                _sourcePath = sourcePath ?? string.Empty;
                _blurAmount = NormalizeBlur(blurAmount);
                version = ++_sourceVersion;
                _pending = null;
            }

            if (!string.IsNullOrWhiteSpace(_sourcePath))
                _ = DecodeAsync(_sourcePath, version);
        }

        public override void LoadResources()
        {
            // CanvasBitmap and effects are device-owned. Recreate them lazily in Draw,
            // where the current CanvasAnimatedControl is available after a device loss.
            _resourcesInvalid = true;
        }

        public override void Update(TimeSpan deltaTime)
        {
            // The image itself is static; the animated host still drives Draw and lyrics.
        }

        public override void Draw(ICanvasAnimatedControl control, CanvasDrawingSession ds)
        {
            if (_disposed || control.Size.Width <= 0 || control.Size.Height <= 0)
                return;

            float width = (float)control.Size.Width;
            float height = (float)control.Size.Height;
            var destination = new Windows.Foundation.Rect(0, 0, width, height);
            var veil = IsDark
                ? Windows.UI.Color.FromArgb(204, 32, 32, 32)
                : Windows.UI.Color.FromArgb(204, 255, 255, 255);

            EnsureBitmap(control);
            if (_blur is null || _bitmap is null)
            {
                ds.FillRectangle(destination, veil);
                return;
            }

            var source = GetUniformToFillSource(_bitmap.Size.Width, _bitmap.Size.Height, width, height);
            ds.DrawImage(_blur, destination, source);

            // WindowBackgroundImage previously used the page veil above the image.
            // Keep that visual treatment in the same session instead of relying on a
            // second composition visual behind a swap chain.
            ds.FillRectangle(destination, veil);
        }

        private void EnsureBitmap(ICanvasAnimatedControl control)
        {
            PendingImage? pending = null;
            int version;
            lock (_pendingGate)
            {
                version = _sourceVersion;
                if (_pending is { } candidate && candidate.Version == version)
                {
                    pending = candidate;
                    _pending = null;
                }
            }

            if (_resourcesInvalid || _loadedVersion != version)
            {
                _blur?.Dispose();
                _blur = null;
                _bitmap?.Dispose();
                _bitmap = null;
                _loadedVersion = version;
                _resourcesInvalid = false;
            }

            if (pending is null || pending.Version != _loadedVersion)
                return;

            try
            {
                _bitmap = CanvasBitmap.CreateFromBytes(
                    control,
                    pending.Pixels,
                    pending.Width,
                    pending.Height,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized,
                    control.Dpi,
                    CanvasAlphaMode.Premultiplied);
                _blur = new GaussianBlurEffect
                {
                    Source = _bitmap,
                    BlurAmount = (float)_blurAmount,
                    BorderMode = EffectBorderMode.Hard
                };
            }
            catch
            {
                _bitmap?.Dispose();
                _bitmap = null;
                _blur?.Dispose();
                _blur = null;
            }
        }

        private async Task DecodeAsync(string path, int version)
        {
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(path);
                using var stream = await file.OpenReadAsync();
                var decoder = await BitmapDecoder.CreateAsync(stream);
                uint width = decoder.OrientedPixelWidth;
                uint height = decoder.OrientedPixelHeight;
                if (width == 0 || height == 0) return;

                double scale = Math.Min(1d, 2560d / Math.Max(width, height));
                var transform = new BitmapTransform
                {
                    ScaledWidth = Math.Max(1u, (uint)Math.Round(width * scale)),
                    ScaledHeight = Math.Max(1u, (uint)Math.Round(height * scale)),
                    InterpolationMode = BitmapInterpolationMode.Fant
                };
                var pixels = await decoder.GetPixelDataAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    transform,
                    ExifOrientationMode.RespectExifOrientation,
                    ColorManagementMode.ColorManageToSRgb);
                var pending = new PendingImage(
                    pixels.DetachPixelData(),
                    (int)transform.ScaledWidth,
                    (int)transform.ScaledHeight,
                    version);

                lock (_pendingGate)
                {
                    if (!_disposed && version == _sourceVersion)
                        _pending = pending;
                }
            }
            catch
            {
                // A missing or invalid image leaves the background transparent; the
                // existing load-error path remains owned by WindowBackgroundImage.
            }
        }

        private static double NormalizeBlur(double amount)
            => double.IsFinite(amount) ? Math.Clamp(amount, 0, 100) : 20;

        private static Windows.Foundation.Rect GetUniformToFillSource(
            double sourceWidth, double sourceHeight, float destinationWidth, float destinationHeight)
        {
            double sourceAspect = sourceWidth / sourceHeight;
            double destinationAspect = destinationWidth / destinationHeight;
            if (sourceAspect > destinationAspect)
            {
                double width = sourceHeight * destinationAspect;
                return new Windows.Foundation.Rect((sourceWidth - width) / 2, 0, width, sourceHeight);
            }

            double height = sourceWidth / destinationAspect;
            return new Windows.Foundation.Rect(0, (sourceHeight - height) / 2, sourceWidth, height);
        }

        public override void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            lock (_pendingGate)
            {
                _sourceVersion++;
                _pending = null;
            }
            _blur?.Dispose();
            _blur = null;
            _bitmap?.Dispose();
            _bitmap = null;
        }
    }
}
