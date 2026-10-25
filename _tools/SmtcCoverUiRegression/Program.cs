using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using WinUIMusicPlayer.Controls;
using WinUIMusicPlayer.Helper;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(initialization =>
        {
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new WinUIMusicPlayer.App();
        });
    }
}

namespace WinUIMusicPlayer
{
    public partial class App : Application
    {
        public static ILogger<T> GetLogger<T>() => NullLogger<T>.Instance;
        public static Window MainWindow { get; private set; } = null!;
        public App() { InitializeComponent(); }
        protected override async void OnLaunched(LaunchActivatedEventArgs args)
        {
            var host = new Grid();
            var window = new Window { Content = host };
            MainWindow = window;
            window.AppWindow.Move(new Windows.Graphics.PointInt32(-30000, -30000));
            window.Activate();
            string result;
            try
            {
                var probe = await ImageHelper.DecodeFileToBitmapAsync(
                    Utils.ToolUtils.FindRawCachePath("large"),
                    CancellationToken.None,
                    PlaybackCoverImage.MaxPixelSize);
                if (probe is not SoftwareBitmapSource probeSource)
                    throw new Exception("ImageHelper 未返回 SoftwareBitmapSource");
                probeSource.Dispose();

                var control = new ImageSwitcher { IsActive = false, Width = 300, Height = 300 };
                host.Children.Add(control);
                await WaitAsync(() => control.IsLoaded);
                control.ImageHash = "missing";
                control.IsActive = true;
                await WaitAsync(() => IsSoftwareSource(Current(control)));
                var defaultSource = Current(control)
                    ?? throw new Exception("默认封面未发布");
                control.ImageHash = "large";
                await WaitAsync(() => IsSoftwareSource(Current(control)) &&
                    !ReferenceEquals(Current(control), defaultSource));
                defaultSource = null;
                var largeSource = Current(control)
                    ?? throw new Exception("大图封面未发布");
                control.ImageHash = "portrait";
                await WaitAsync(() => IsSoftwareSource(Current(control)) &&
                    !ReferenceEquals(Current(control), largeSource));
                var portraitSource = Current(control)
                    ?? throw new Exception("纵向封面未发布");
                largeSource = null;
                control.ImageHash = "small";
                await WaitAsync(() => IsSoftwareSource(Current(control)) &&
                    !ReferenceEquals(Current(control), portraitSource));
                portraitSource = null;

                await VerifyCacheFallbackAsync(control, "portrait", 800, 2400);
                await VerifyCacheFallbackAsync(control, "large", 3072, 3072);
                await VerifyWidthLimitDecodeAsync();
                await VerifyCacheFallbackAsync(control, "oriented", 2400, 800);

                using (var cancelled = new CancellationTokenSource())
                {
                    cancelled.Cancel();
                    try
                    {
                        await ImageHelper.DecodeFileToBitmapAsync(Utils.ToolUtils.FindRawCachePath("portrait"),
                            cancelled.Token, PlaybackCoverImage.MaxPixelSize);
                        throw new Exception("原图兜底未传播取消");
                    }
                    catch (OperationCanceledException) { }
                }

                control.ImageHash = "large";
                control.ImageHash = "portrait";
                control.ImageHash = "small";
                var rapidPrevious = Current(control);
                await WaitAsync(() => IsSoftwareSource(Current(control)) &&
                    !ReferenceEquals(Current(control), rapidPrevious));
                var rapidCurrent = Current(control);
                rapidPrevious = null;
                await Task.Delay(500);
                if (!IsSoftwareSource(Current(control)) ||
                    !ReferenceEquals(Current(control), rapidCurrent))
                    throw new Exception("迟到的大图覆盖小图");
                control.IsActive = false;
                control.ImageHash = "large";
                await Task.Delay(100);
                if (!IsSoftwareSource(Current(control)))
                    throw new Exception("隐藏详情页仍加载新图");
                var previousSource = Current(control)
                    ?? throw new Exception("隐藏详情页没有保留当前封面");
                control.IsActive = true;
                await WaitAsync(() => IsSoftwareSource(Current(control)) &&
                    !ReferenceEquals(Current(control), previousSource));
                await Task.Delay(500);
                if (((ShadowImage)control.FindName("LastAlbumArtImage")).Source is not null)
                    throw new Exception("动画结束后仍保留旧图片");
                var previousWeak = new WeakReference(previousSource);
                previousSource = null;
                rapidCurrent = null;
                await AssertSourceCollectedAsync(previousWeak, "切歌后的旧封面");
                var unloadedSource = Current(control)
                    ?? throw new Exception("卸载前没有当前封面");
                var unloadedWeak = new WeakReference(unloadedSource);
                unloadedSource = null;
                host.Children.Remove(control);
                // IsLoaded 在 Unloaded 回调之前即可变为 false，等待实际资源清理完成。
                await WaitAsync(() => !control.IsLoaded && Current(control) is null);
                await AssertSourceCollectedAsync(unloadedWeak, "卸载后的当前封面");
                await CoverPipelineRegression.RunAsync();
                result = "PASS: 真实 ImageSwitcher 与 CoverPresentation/SMTC 缓存失败兜底、限尺寸解码、SoftwareBitmapSource 类型与回收、连续切歌、取消、退出与句柄释放";
            }
            catch (Exception ex) { result = "FAIL: " + ex; }
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "result.txt"), result);
            window.Close();
            Exit();
        }
        private static ImageSource? Current(ImageSwitcher control)
            => ((ShadowImage)control.FindName("AlbumArtImage")).Source;

        private static bool IsSoftwareSource(ImageSource? source)
            => source is SoftwareBitmapSource;

        private static async Task VerifyCacheFallbackAsync(ImageSwitcher control, string source, int width, int height)
        {
            string hash = "blocked_" + source;
            string raw = Utils.ToolUtils.FindRawCachePath(hash);
            File.Copy(Utils.ToolUtils.FindRawCachePath(source), raw, true);
            string display = PlaybackCoverImage.GetCachePath(raw);
            // 纵向图阻止新缓存发布；其余图片锁住过期文件，阻止原子替换。
            FileStream? locked = null;
            if (source == "portrait") Directory.CreateDirectory(display);
            else
            {
                await File.WriteAllTextAsync(display, "stale cache");
                File.SetLastWriteTimeUtc(display, File.GetLastWriteTimeUtc(raw).AddMinutes(-1));
                locked = new FileStream(display, FileMode.Open, FileAccess.Read, FileShare.Read);
            }
            try
            {
                await VerifyFixtureDimensionsAsync(Utils.ToolUtils.FindRawCachePath(source), width, height);
                await VerifyFallbackDecodeSizeAsync(source, width, height);
                var previous = Current(control);
                control.ImageHash = hash;
                await WaitAsync(() => Current(control) is { } image &&
                    !ReferenceEquals(image, previous) &&
                    IsSoftwareSource(image));
            }
            finally
            {
                locked?.Dispose();
                if (source == "portrait") Directory.Delete(display);
                else File.Delete(display);
            }
        }

        private static async Task VerifyFixtureDimensionsAsync(string path, int width, int height)
        {
            await using var file = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 64 * 1024, useAsync: true);
            using var stream = file.AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            if (decoder.OrientedPixelWidth != width || decoder.OrientedPixelHeight != height)
                throw new Exception($"测试封面尺寸不符: {decoder.OrientedPixelWidth}x{decoder.OrientedPixelHeight}，期望 {width}x{height}");
        }

        // 生产原图兜底走最长边 MaxPixelSize 限制（ImageHelper.CreateLongestSideTransform）。
        // WIC 在未旋转的原始像素上应用 Scaled*，EXIF 旋转在其后应用，
        // 期望值按同一语义独立重算，防止缩放逻辑与旋转组合回归。
        private static async Task VerifyFallbackDecodeSizeAsync(string source, int width, int height)
        {
            await using var file = new FileStream(
                Utils.ToolUtils.FindRawCachePath(source), FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 64 * 1024, useAsync: true);
            using var stream = file.AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(stream);

            uint rawW = decoder.PixelWidth, rawH = decoder.PixelHeight;
            if (decoder.OrientedPixelWidth != width || decoder.OrientedPixelHeight != height)
                throw new Exception($"{source} 测试封面尺寸不符: {decoder.OrientedPixelWidth}x{decoder.OrientedPixelHeight}，期望 {width}x{height}");

            uint max = PlaybackCoverImage.MaxPixelSize;
            uint scaledW = rawW, scaledH = rawH;
            if (Math.Max(rawW, rawH) > max)
            {
                if (rawW >= rawH)
                {
                    scaledW = max;
                    scaledH = Math.Max(1u, (uint)Math.Round((double)max * rawH / rawW));
                }
                else
                {
                    scaledH = max;
                    scaledW = Math.Max(1u, (uint)Math.Round((double)max * rawW / rawH));
                }
            }
            bool rotated = rawW != rawH && decoder.OrientedPixelWidth == rawH;
            uint expectedW = rotated ? scaledH : scaledW;
            uint expectedH = rotated ? scaledW : scaledH;

            var transform = ImageHelper.CreateLongestSideTransform(rawW, rawH, max);
            using var bitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
                ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
            if (bitmap.PixelWidth != expectedW || bitmap.PixelHeight != expectedH)
                throw new Exception($"{source} 限尺寸解码输出 {bitmap.PixelWidth}x{bitmap.PixelHeight}，期望 {expectedW}x{expectedH}");
        }

        // FadeImageBehavior 原图兜底走固定宽解码（ImageHelper.CreateWidthLimitTransform），
        // 与缩略图像素缓存同语义；窄于目标宽的图不上采样。
        private static async Task VerifyWidthLimitDecodeAsync()
        {
            var portrait = ImageHelper.CreateWidthLimitTransform(800, 2400, 150);
            if (portrait.ScaledWidth != 150 || portrait.ScaledHeight != 450)
                throw new Exception($"纵向封面固定宽变换 {portrait.ScaledWidth}x{portrait.ScaledHeight}，期望 150x450");
            var narrow = ImageHelper.CreateWidthLimitTransform(100, 200, 150);
            if (narrow.ScaledWidth != 0 || narrow.ScaledHeight != 0)
                throw new Exception("窄于目标宽的图不应被上采样");

            await using var file = new FileStream(
                Utils.ToolUtils.FindRawCachePath("portrait"), FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 64 * 1024, useAsync: true);
            using var stream = file.AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            using var bitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, portrait,
                ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
            if (bitmap.PixelWidth != 150 || bitmap.PixelHeight != 450)
                throw new Exception($"纵向封面固定宽解码 {bitmap.PixelWidth}x{bitmap.PixelHeight}，期望 150x450");
        }

        private static async Task AssertSourceCollectedAsync(WeakReference source, string description)
        {
            var timeout = DateTime.UtcNow.AddSeconds(2);
            while (source.IsAlive && DateTime.UtcNow < timeout)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                await Task.Delay(20);
            }
            if (source.IsAlive)
                throw new Exception($"{description} 清空后仍被 WinUI 引用");
        }

        private static async Task WaitAsync(Func<bool> predicate)
        {
            var timeout = DateTime.UtcNow.AddSeconds(10);
            while (!predicate())
            {
                if (DateTime.UtcNow > timeout) throw new TimeoutException("等待生产图片控件超时");
                await Task.Delay(20);
            }
        }
    }
}
namespace WinUIMusicPlayer.Utils
{
    internal static partial class ToolUtils
    {
        internal static string FindRawCachePath(string hash)
            => Path.Combine(AppContext.BaseDirectory, "fixtures", hash + "_raw.bin");
    }
}
