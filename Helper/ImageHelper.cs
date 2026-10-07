using Microsoft.Extensions.Logging;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace WinUIMusicPlayer.Helper
{
    public static class ImageHelper
    {
        private static readonly ILogger _logger = App.GetLogger<ImageHelperLogMarker>();
        private sealed class ImageHelperLogMarker { }

        internal static async Task<BitmapImage?> DecodeFileToBitmapAsync(
            string path, CancellationToken token, uint maxPixelSize = 0)
        {
            BitmapImage? bitmap = null;
            try
            {
                token.ThrowIfCancellationRequested();
                var file = await StorageFile.GetFileFromPathAsync(path);
                using var stream = await file.OpenReadAsync();
                token.ThrowIfCancellationRequested();
                bitmap = new BitmapImage { DecodePixelType = DecodePixelType.Physical };
                if (maxPixelSize > 0)
                {
                    var decoder = await BitmapDecoder.CreateAsync(stream);
                    token.ThrowIfCancellationRequested();
                    // 按 EXIF 方向后的最长边限制解码；小图保持原尺寸。
                    if (Math.Max(decoder.OrientedPixelWidth, decoder.OrientedPixelHeight) > maxPixelSize)
                    {
                        if (decoder.OrientedPixelWidth >= decoder.OrientedPixelHeight)
                            bitmap.DecodePixelWidth = checked((int)maxPixelSize);
                        else
                            bitmap.DecodePixelHeight = checked((int)maxPixelSize);
                    }
                    stream.Seek(0);
                }
                await bitmap.SetSourceAsync(stream);
                token.ThrowIfCancellationRequested();
                var result = bitmap;
                bitmap = null;
                return result;
            }
            catch (OperationCanceledException)
            {
                bitmap = null;
                throw;
            }
            catch (Exception ex)
            {
                bitmap = null;
                _logger.LogError(ex, "DecodeFileToBitmapAsync 失败");
                return null;
            }
        }

        internal static async Task<BitmapImage?> DecodeToBitmapAsync(
            byte[]? bytes, int decodePixelWidth = 0, CancellationToken token = default)
        {
            if (bytes is not { Length: > 0 }) return null;

            BitmapImage? bitmap = null;
            try
            {
                using var memStream = new MemoryStream(bytes, writable: false);
                using var stream = memStream.AsRandomAccessStream();

                if (token.IsCancellationRequested) return null;

                bitmap = new BitmapImage
                {
                    DecodePixelType = DecodePixelType.Logical
                };

                if (decodePixelWidth > 0)
                    bitmap.DecodePixelWidth = decodePixelWidth;

                await bitmap.SetSourceAsync(stream);
                token.ThrowIfCancellationRequested();
                var result = bitmap;
                bitmap = null;
                return result;
            }
            catch (OperationCanceledException)
            {
                bitmap = null;
                return null;
            }
            catch (Exception ex)
            {
                bitmap = null;
                _logger.LogError(ex, "DecodeToBitmapAsync 失败");
                return null;
            }
        }

        private static float blurAmount = 10.0f;
        private static int TargetWidth = 200;
        public static async Task<WriteableBitmap> ApplyMicaEffectWin2DAsync(
                   this byte[] cover,
                   bool isDarkMode)
        {
            try
            {
                var device = CanvasDevice.GetSharedDevice();
                using var imageStream = new InMemoryRandomAccessStream();
                await imageStream.WriteAsync(cover.AsBuffer());
                imageStream.Seek(0);
                using var canvasBitmap = await CanvasBitmap.LoadAsync(device, imageStream);
                int originalWidth = (int)canvasBitmap.SizeInPixels.Width;
                int originalHeight = (int)canvasBitmap.SizeInPixels.Height;
                float scaleFactor;
                int targetWidth;
                int targetHeight;
                if (originalWidth > TargetWidth)
                {
                    scaleFactor = (float)TargetWidth / originalWidth;
                    targetWidth = TargetWidth;
                    targetHeight = (int)Math.Round(originalHeight * scaleFactor);
                }
                else
                {
                    scaleFactor = 1.0f;
                    targetWidth = originalWidth;
                    targetHeight = originalHeight;
                }
                // 构建 GPU 效果链 (Transform2DEffect 和 GaussianBlurEffect)
                using var scaledSource = new Transform2DEffect { Source = canvasBitmap, TransformMatrix = Matrix3x2.CreateScale(scaleFactor) };
                using var blurEffect = new GaussianBlurEffect { Source = scaledSource, BlurAmount = blurAmount, Optimization = EffectOptimization.Speed };
                using var renderTarget = new CanvasRenderTarget(device, targetWidth, targetHeight, 96);
                using (var ds = renderTarget.CreateDrawingSession())
                {
                    ds.Clear(Colors.Black);
                    ds.DrawImage(blurEffect);

                    // 绘制渐变遮罩
                    Windows.UI.Color color1 = isDarkMode ? Windows.UI.Color.FromArgb(90, 0, 0, 0) : Windows.UI.Color.FromArgb(90, 255, 255, 255);
                    Windows.UI.Color color2 = isDarkMode ? Windows.UI.Color.FromArgb(120, 0, 0, 0) : Windows.UI.Color.FromArgb(120, 255, 255, 255);

                    using var brush = new CanvasLinearGradientBrush(device, color1, color2)
                    {
                        StartPoint = new Vector2(0, 0),
                        EndPoint = new Vector2(0, targetHeight)
                    };

                    ds.FillRectangle(0, 0, targetWidth, targetHeight, brush);
                }
                using SoftwareBitmap resultSoftwareBitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(renderTarget);

                var writeableBitmap = new WriteableBitmap(targetWidth, targetHeight);
                using (var converted = SoftwareBitmap.Convert(
                    resultSoftwareBitmap,
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied))
                {
                    // 复制像素数据到最终的 WriteableBitmap
                    converted.CopyToBuffer(writeableBitmap.PixelBuffer);
                }
                return writeableBitmap;
            }
            catch
            {
                return null;
            }
            finally
            {
            }
        }
    }
}
