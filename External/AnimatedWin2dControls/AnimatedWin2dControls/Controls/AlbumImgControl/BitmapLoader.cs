using AnimatedWin2dControls.Utils;
using Microsoft.Graphics.Canvas;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;

namespace AnimatedWin2dControls.Controls.AlbumImgControl
{
    /// <summary>
    /// 负责异步加载、解码并去重图片字节流，生成 <see cref="CanvasBitmap"/>。
    /// 不持有任何 GPU 资源——位图所有权在 <see cref="TransitionState"/> 中管理。
    /// </summary>
    internal sealed class BitmapLoader : IDisposable
    {
        private const float HardMaxSize = 1536f;

        private long _lastLength = -1;
        private int _lastHash;

        private CancellationTokenSource? _cts;
        private bool _disposed;

        // ── 去重 ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 检查新字节数组是否与上次相同。
        /// 相同则返回 true（调用方应跳过加载）；否则更新缓存并返回 false。
        /// </summary>
        public bool IsDuplicate(byte[]? newBytes)
        {
            if (newBytes is not { Length: > 0 })
            {
                bool wasEmpty = _lastLength == 0;
                _lastLength = 0;
                _lastHash = 0;
                return wasEmpty;
            }

            int hash = ToolUtils.ComputeFastHash(newBytes);
            if (newBytes.Length == _lastLength && hash == _lastHash)
                return true;

            _lastLength = newBytes.Length;
            _lastHash = hash;
            return false;
        }

        /// <summary>强制使去重缓存失效，下次任何内容都将被视为新内容。</summary>
        public void InvalidateDedup()
        {
            _lastLength = -1;
            _lastHash = 0;
        }

        // ── 加载 ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 异步解码 <paramref name="imageBytes"/> 并返回一个新的 <see cref="CanvasBitmap"/>。
        /// 调用方取得返回位图的所有权，负责在适当时候 Dispose。
        /// 若被取消则返回 null。
        /// </summary>
        public async Task<CanvasBitmap?> LoadAsync(
            byte[] imageBytes,
            ICanvasResourceCreator creator,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var (pixels, bmpW, bmpH) = await Task.Run(async () =>
            {
                using var mem = new MemoryStream(imageBytes, writable: false);
                using var ras = mem.AsRandomAccessStream();
                var decoder = await BitmapDecoder.CreateAsync(ras);

                uint srcW = decoder.PixelWidth;
                uint srcH = decoder.PixelHeight;
                float sc = Math.Min(1f, Math.Min(HardMaxSize / srcW, HardMaxSize / srcH));
                uint dstW = Math.Max(1, (uint)(srcW * sc));
                uint dstH = Math.Max(1, (uint)(srcH * sc));

                cancellationToken.ThrowIfCancellationRequested();

                var pixelData = await decoder.GetPixelDataAsync(
                    BitmapPixelFormat.Rgba8,
                    BitmapAlphaMode.Premultiplied,
                    new BitmapTransform
                    {
                        ScaledWidth = dstW,
                        ScaledHeight = dstH,
                        InterpolationMode = BitmapInterpolationMode.Fant
                    },
                    ExifOrientationMode.RespectExifOrientation,
                    ColorManagementMode.DoNotColorManage);

                return (pixelData.DetachPixelData(), dstW, dstH);
            }, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            return CanvasBitmap.CreateFromBytes(
                creator, pixels, (int)bmpW, (int)bmpH,
                Windows.Graphics.DirectX.DirectXPixelFormat.R8G8B8A8UIntNormalized);
        }

        /// <summary>
        /// 从本地 Assets 目录加载默认封面位图。
        /// </summary>
        public async Task<CanvasBitmap?> LoadDefaultAsync(
            bool isDark,
            ICanvasResourceCreator creator,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            string fileName = isDark ? "default_cover_black.png" : "default_cover_white.png";
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", fileName);

            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenReadAsync();

            cancellationToken.ThrowIfCancellationRequested();

            return await CanvasBitmap.LoadAsync(creator, stream);
        }

        // ── 取消控制 ─────────────────────────────────────────────────────────

        /// <summary>取消当前正在进行的加载操作并创建新的 CTS。</summary>
        public CancellationToken RenewCancellation()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            return _cts.Token;
        }

        public void CancelCurrent()
        {
            _cts?.Cancel();
        }

        // ── Dispose ───────────────────────────────────────────────────────────

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }
    }
}