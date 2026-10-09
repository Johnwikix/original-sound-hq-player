using Microsoft.Extensions.Logging;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
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

        internal static async Task<ImageSource?> DecodeFileToBitmapAsync(
            string path, CancellationToken token, uint maxPixelSize = 0)
        {
            // BitmapImage has no deterministic disposal API; keep the decoded
            // native frame behind SoftwareBitmapSource so ownership is explicit.
            SoftwareBitmap? bitmap = null;
            try
            {
                token.ThrowIfCancellationRequested();
                await using var fileStream = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                    bufferSize: 64 * 1024, useAsync: true);
                using var stream = fileStream.AsRandomAccessStream();
                token.ThrowIfCancellationRequested();
                var decoder = await BitmapDecoder.CreateAsync(stream);
                token.ThrowIfCancellationRequested();
                var transform = CreateDecodeTransform(
                    decoder.OrientedPixelWidth,
                    decoder.OrientedPixelHeight,
                    maxPixelSize);
                bitmap = await decoder.GetSoftwareBitmapAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    transform,
                    ExifOrientationMode.RespectExifOrientation,
                    ColorManagementMode.DoNotColorManage);
                token.ThrowIfCancellationRequested();
                var result = await CreateSoftwareBitmapSourceAsync(bitmap, token);
                bitmap = null;
                return result;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DecodeFileToBitmapAsync 失败");
                return null;
            }
            finally
            {
                bitmap?.Dispose();
            }
        }

        internal static async Task<ImageSource?> DecodeToBitmapAsync(
            byte[]? bytes, int decodePixelWidth = 0, CancellationToken token = default)
        {
            if (bytes is not { Length: > 0 }) return null;

            SoftwareBitmap? bitmap = null;
            try
            {
                using var memStream = new MemoryStream(bytes, writable: false);
                using var stream = memStream.AsRandomAccessStream();

                token.ThrowIfCancellationRequested();
                var decoder = await BitmapDecoder.CreateAsync(stream);
                token.ThrowIfCancellationRequested();
                var transform = CreateDecodeTransform(
                    decoder.OrientedPixelWidth,
                    decoder.OrientedPixelHeight,
                    decodePixelWidth > 0 ? (uint)decodePixelWidth : 0);
                bitmap = await decoder.GetSoftwareBitmapAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    transform,
                    ExifOrientationMode.RespectExifOrientation,
                    ColorManagementMode.DoNotColorManage);
                token.ThrowIfCancellationRequested();
                var result = await CreateSoftwareBitmapSourceAsync(bitmap, token);
                bitmap = null;
                return result;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DecodeToBitmapAsync 失败");
                return null;
            }
            finally
            {
                bitmap?.Dispose();
            }
        }

        internal static async Task<ImageSource?> DecodeApplicationAssetAsync(
            string assetName, CancellationToken token)
        {
            SoftwareBitmap? bitmap = null;
            try
            {
                token.ThrowIfCancellationRequested();
                var path = Path.Combine(AppContext.BaseDirectory, "Assets", assetName);
                if (File.Exists(path))
                {
                    await using var fileStream = new FileStream(
                        path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                        bufferSize: 16 * 1024, useAsync: true);
                    using var stream = fileStream.AsRandomAccessStream();
                    bitmap = await DecodeAssetBitmapAsync(stream);
                }
                else
                {
                    var file = await StorageFile.GetFileFromApplicationUriAsync(
                        new Uri($"ms-appx:///Assets/{assetName}"));
                    using var stream = await file.OpenReadAsync();
                    bitmap = await DecodeAssetBitmapAsync(stream);
                }
                token.ThrowIfCancellationRequested();
                var result = await CreateSoftwareBitmapSourceAsync(bitmap, token);
                bitmap = null;
                return result;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DecodeApplicationAssetAsync 失败");
                return null;
            }
            finally
            {
                bitmap?.Dispose();
            }
        }

        private static async Task<SoftwareBitmap> DecodeAssetBitmapAsync(IRandomAccessStream stream)
        {
            var decoder = await BitmapDecoder.CreateAsync(stream);
            return await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                new BitmapTransform { InterpolationMode = BitmapInterpolationMode.Fant },
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.DoNotColorManage);
        }

        private static BitmapTransform CreateDecodeTransform(
            uint orientedWidth, uint orientedHeight, uint maxPixelSize)
        {
            var transform = new BitmapTransform
            {
                InterpolationMode = BitmapInterpolationMode.Fant
            };
            if (maxPixelSize > 0 && Math.Max(orientedWidth, orientedHeight) > maxPixelSize)
            {
                if (orientedWidth >= orientedHeight)
                    transform.ScaledWidth = maxPixelSize;
                else
                    transform.ScaledHeight = maxPixelSize;
            }
            return transform;
        }

        private static async Task<ImageSource?> CreateSoftwareBitmapSourceAsync(
            SoftwareBitmap bitmap, CancellationToken token)
        {
            SoftwareBitmapSource? source = null;
            ImageSource? result = null;
            try
            {
                await EnqueueOnDispatcherAsync(async () =>
                {
                    token.ThrowIfCancellationRequested();
                    source = new SoftwareBitmapSource();
                    await source.SetBitmapAsync(bitmap);
                    result = source;
                    source = null;
                }, token);
                token.ThrowIfCancellationRequested();
                var completed = result;
                result = null;
                return completed;
            }
            finally
            {
                source?.Dispose();
                (result as IDisposable)?.Dispose();
            }
        }

        private static Task EnqueueOnDispatcherAsync(Func<Task> callback, CancellationToken token)
        {
            var completion = new TaskCompletionSource<object?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            if (!App.MainWindow.DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    await callback();
                    completion.TrySetResult(null);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    completion.TrySetCanceled(token);
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            }))
            {
                completion.TrySetException(new InvalidOperationException(
                    "Image decode dispatcher rejected the callback."));
            }
            return completion.Task;
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
