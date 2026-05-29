using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Numerics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace WinUIMusicPlayer.Helper
{
    public static class ImageHelper
    {
        private static float blurAmount = 10.0f;
        private static int TargetWidth = 200;
        private static CanvasDevice device = CanvasDevice.GetSharedDevice();
        public static async Task<WriteableBitmap> ApplyMicaEffectWin2DAsync(
                   this byte[] cover,
                   bool isDarkMode)
        {
            try
            {
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
