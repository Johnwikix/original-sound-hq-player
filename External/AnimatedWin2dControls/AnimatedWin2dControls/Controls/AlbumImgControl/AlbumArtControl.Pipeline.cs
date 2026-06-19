using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using System;
using System.Buffers;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;

namespace AnimatedWin2dControls.Controls.AlbumImgControl
{
    public sealed partial class AlbumArtControl
    {
        // ── Canvas 资源 / 尺寸事件 ────────────────────────────────────────────

        private void Canvas_CreateResources(CanvasControl sender,
            Microsoft.Graphics.Canvas.UI.CanvasCreateResourcesEventArgs e)
        {
            var oldCts = Interlocked.Exchange(ref _pipelineCts, new CancellationTokenSource());
            oldCts.Cancel();
            oldCts.Dispose();

            _cachedContentW = -1f;
            _cachedContentH = -1f;

            _currentBmp?.Dispose();
            _currentBmp = null;
            _nextBmp?.Dispose();
            _nextBmp = null;

            _isResourcesCreated = true;
            Task.Run(() => DecodeLoopAsync(_pipelineCts.Token));

            if (!IsActive) return;
            RequestLoad(LoadImageBytesFromCache());
        }

        private void Canvas_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!_isResourcesCreated || !IsActive) return;

            // 失效 contentRect 缓存
            _cachedContentW = -1f;
            _cachedContentH = -1f;

            // 安全替换 resizeCts，避免旧 CTS 被 double-dispose
            var oldCts = Interlocked.Exchange(ref _resizeCts, new CancellationTokenSource());
            oldCts.Cancel();
            oldCts.Dispose();

            var ct = _resizeCts.Token;
            var dq = _canvas?.DispatcherQueue;
            if (dq is null) return;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(ResizeDebounceMs, ct).ConfigureAwait(false);
                    dq.TryEnqueue(DispatcherQueuePriority.Normal, () =>
                    {
                        if (_disposed || !IsActive) return;
                        RequestLoad(LoadImageBytesFromCache(), isResize: true);
                    });
                }
                catch (OperationCanceledException) { }
            });
        }

        // ── 解码循环（后台线程） ──────────────────────────────────────────────

        private async Task DecodeLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await _decodeSignal.WaitAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }

                var req = Interlocked.Exchange(ref _pendingRequest, null);
                if (req == null) continue;

                try
                {
                    DecodedFrame frame;

                    if (req.Bytes is { Length: > 0 })
                    {
                        try
                        {
                            var d = await DecodeImageAsync(req.Bytes, ct).ConfigureAwait(false);
                            frame = d ?? throw new InvalidDataException("Image decoding returned null.");
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            System.Diagnostics.Debug.WriteLine(
                                $"[AlbumArt] Decode failed: {ex.Message}. Falling back to default.");
                            var d = await GetOrDecodeDefaultAsync(req.IsDark, ct).ConfigureAwait(false);
                            if (d == null) continue;
                            frame = d.Value;
                        }
                    }
                    else
                    {
                        var d = await GetOrDecodeDefaultAsync(req.IsDark, ct).ConfigureAwait(false);
                        if (d == null) continue;
                        frame = d.Value;
                    }

                    await _decodeChannel.Writer.WriteAsync((frame, req), ct).ConfigureAwait(false);

                    _canvas?.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal,
                        () => _canvas?.Invalidate());
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[AlbumArt] Critical error in pipeline: {ex.Message}");
                }
            }
        }

        // ── 图像解码 ──────────────────────────────────────────────────────────

        private static async Task<DecodedFrame?> DecodeImageAsync(byte[] bytes, CancellationToken ct)
        {
            using var mem = new MemoryStream(bytes, writable: false);
            using var stream = mem.AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(ct).ConfigureAwait(false);

            uint srcW = decoder.PixelWidth, srcH = decoder.PixelHeight;
            float sc = Math.Min(1f, Math.Min(HardMaxSize / srcW, HardMaxSize / srcH));
            uint dstW = Math.Max(1, (uint)(srcW * sc));
            uint dstH = Math.Max(1, (uint)(srcH * sc));

            ct.ThrowIfCancellationRequested();

            using var softwareBitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Rgba8, BitmapAlphaMode.Premultiplied,
                new BitmapTransform
                {
                    ScaledWidth = dstW,
                    ScaledHeight = dstH,
                    InterpolationMode = BitmapInterpolationMode.Fant,
                },
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.DoNotColorManage).AsTask(ct).ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();

            int pixelBytes = (int)(dstW * dstH * 4);
            var buffer = ArrayPool<byte>.Shared.Rent(pixelBytes);
            try
            {
                softwareBitmap.CopyToBuffer(buffer.AsBuffer(0, pixelBytes));
                return new DecodedFrame(buffer, (int)dstW, (int)dstH, IsPooled: true);
            }
            catch
            {
                ArrayPool<byte>.Shared.Return(buffer);
                throw;
            }
        }

        // ── 默认封面缓存 ──────────────────────────────────────────────────────

        /// <summary>
        /// 获取默认封面。首次调用执行 IO + 解码，后续从静态缓存直接返回。
        /// 缓存的 DecodedFrame.Pixels 数组只读，可安全复用。
        /// </summary>
        private static async Task<DecodedFrame?> GetOrDecodeDefaultAsync(bool isDark, CancellationToken ct)
        {
            // 快速路径：已缓存
            if (isDark && _cachedDefaultDark.HasValue) return _cachedDefaultDark;
            if (!isDark && _cachedDefaultLight.HasValue) return _cachedDefaultLight;

            await _defaultCacheLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // 双重检查
                if (isDark && _cachedDefaultDark.HasValue) return _cachedDefaultDark;
                if (!isDark && _cachedDefaultLight.HasValue) return _cachedDefaultLight;

                string name = isDark ? "default_cover_black.png" : "default_cover_white.png";
                string path = System.IO.Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "Assets", name);

                var file = await Windows.Storage.StorageFile
                    .GetFileFromPathAsync(path).AsTask(ct).ConfigureAwait(false);

                using var s = await file.OpenReadAsync().AsTask(ct).ConfigureAwait(false);
                var ms = new MemoryStream();
                using (var rs = s.AsStream())
                    await rs.CopyToAsync(ms, ct).ConfigureAwait(false);

                var pooledFrame = await DecodeImageAsync(ms.ToArray(), ct).ConfigureAwait(false);
                if (pooledFrame == null) return null;

                var pf = pooledFrame.Value;
                int len = pf.W * pf.H * 4;
                var cachedPixels = new byte[len];
                Array.Copy(pf.Pixels, cachedPixels, len);
                if (pf.IsPooled) ArrayPool<byte>.Shared.Return(pf.Pixels);
                var frame = new DecodedFrame(cachedPixels, pf.W, pf.H, IsPooled: false);

                if (isDark) _cachedDefaultDark = frame;
                else _cachedDefaultLight = frame;

                return frame;
            }
            catch { return null; }
            finally { _defaultCacheLock.Release(); }
        }
    }
}
