using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI.Xaml;
using System;
using System.Runtime.InteropServices.WindowsRuntime;
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
        private readonly object _resourceGate = new();
        private readonly SemaphoreSlim _decodeSignal = new(0, 1);
        private readonly CancellationTokenSource _lifetimeCts = new();
        private string _sourcePath = string.Empty;
        private double _blurAmount = 20;
        private int _sourceVersion;
        private int _loadedVersion = -1;
        private PendingImage? _pending;
        private CanvasBitmap? _bitmap;
        private GaussianBlurEffect? _blur;
        private bool _resourcesInvalid = true;
        private bool _disposed;
        private bool _decodeEnabled = true;
        private bool _decodeNeeded;
        private CancellationTokenSource? _requestCts;
        private Task? _decodeWorker;

        private sealed record PendingImage(byte[] Pixels, int Width, int Height, int Version);

        public ImageBackgroundRenderer(string sourcePath, double blurAmount)
        {
            SetSource(sourcePath, blurAmount);
        }

        public void SetSource(string? sourcePath, double blurAmount)
        {
            string path = sourcePath ?? string.Empty;
            double normalizedBlur = NormalizeBlur(blurAmount);
            bool sourceChanged;
            bool startWorker = false;

            lock (_pendingGate)
            {
                if (_disposed)
                    return;

                sourceChanged = !string.Equals(_sourcePath, path, StringComparison.Ordinal);
                _blurAmount = normalizedBlur;
                _sourcePath = path;
                if (sourceChanged)
                {
                    ++_sourceVersion;
                    _pending = null;
                    _requestCts?.Cancel();
                    _decodeNeeded = !string.IsNullOrWhiteSpace(path);

                    if (_decodeEnabled && _decodeNeeded)
                    {
                        EnsureDecodeWorker();
                        startWorker = true;
                    }
                }
                else if (_decodeEnabled && _decodeNeeded)
                {
                    EnsureDecodeWorker();
                    startWorker = true;
                }
            }

            // A blur-only change does not require another file read. A pending decode
            // observes the new amount when it creates the Win2D effect.
            if (!sourceChanged)
            {
                lock (_resourceGate)
                {
                    if (_blur is not null)
                        _blur.BlurAmount = (float)normalizedBlur;
                }
            }
            if (startWorker)
            {
                SignalDecodeWorker();
            }
        }

        public void SetActive(bool active)
        {
            bool startWorker = false;
            lock (_pendingGate)
            {
                if (_disposed || _decodeEnabled == active)
                    return;

                _decodeEnabled = active;
                if (!active)
                {
                    // Do not retain a second full-size managed pixel buffer while the
                    // image background is hidden. The source version remains current,
                    // so reactivation will request it again when needed.
                    if (_pending is not null)
                    {
                        _pending = null;
                        _decodeNeeded = !string.IsNullOrWhiteSpace(_sourcePath);
                    }
                    _requestCts?.Cancel();
                }
                else if (_decodeNeeded && !string.IsNullOrWhiteSpace(_sourcePath))
                {
                    EnsureDecodeWorker();
                    startWorker = true;
                }
            }

            if (startWorker)
                SignalDecodeWorker();
        }

        private void EnsureDecodeWorker()
        {
            if (_decodeWorker is null || _decodeWorker.IsCompleted)
                _decodeWorker = DecodeLoopAsync(_lifetimeCts.Token);
        }

        private void SignalDecodeWorker()
        {
            try
            {
                if (_decodeSignal.CurrentCount == 0)
                    _decodeSignal.Release();
            }
            catch (ObjectDisposedException)
            {
                // Dispose won the race with a late dependency-property callback.
            }
            catch (SemaphoreFullException)
            {
                // Another source update already signalled the single-slot wake-up.
            }
        }

        public override void LoadResources()
        {
            // CanvasBitmap and effects are device-owned. Recreate them lazily in Draw,
            // where the current CanvasAnimatedControl is available after a device loss.
            lock (_resourceGate)
            {
                if (!_disposed)
                    _resourcesInvalid = true;
            }

            bool startWorker = false;
            lock (_pendingGate)
            {
                if (_disposed || string.IsNullOrWhiteSpace(_sourcePath))
                    return;

                // The previous CanvasBitmap may belong to a lost device and the
                // pending CPU pixels have already been consumed. Re-decode once so
                // the next Draw can seed a bitmap for the new device.
                _decodeNeeded = true;
                if (_decodeEnabled)
                {
                    EnsureDecodeWorker();
                    startWorker = true;
                }
            }

            if (startWorker)
                SignalDecodeWorker();
        }

        public override void Update(TimeSpan deltaTime)
        {
            // The image itself is static; the animated host still drives Draw and lyrics.
        }

        public override void Draw(ICanvasAnimatedControl control, CanvasDrawingSession ds)
        {
            // SwapBackgroundRenderer pauses the host before replacement, but Win2D can
            // already have entered this callback. Skip instead of racing Dispose.
            if (!Monitor.TryEnter(_resourceGate, 0))
                return;

            try
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
            finally
            {
                Monitor.Exit(_resourceGate);
            }
        }

        private void EnsureBitmap(ICanvasAnimatedControl control)
        {
            PendingImage? pending = null;
            int version;
            double blurAmount;
            lock (_pendingGate)
            {
                version = _sourceVersion;
                blurAmount = _blurAmount;
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
                    BlurAmount = (float)blurAmount,
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

        private async Task DecodeLoopAsync(CancellationToken lifetimeToken)
        {
            try
            {
                while (true)
                {
                    await _decodeSignal.WaitAsync(lifetimeToken).ConfigureAwait(false);

                    string path;
                    int version;
                    CancellationTokenSource requestCts;
                    lock (_pendingGate)
                    {
                        if (_disposed || !_decodeEnabled || !_decodeNeeded ||
                            string.IsNullOrWhiteSpace(_sourcePath))
                            continue;

                        path = _sourcePath;
                        version = _sourceVersion;
                        requestCts = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
                        _requestCts = requestCts;
                    }

                    try
                    {
                        await DecodeAsync(path, version, requestCts.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        lock (_pendingGate)
                        {
                            if (ReferenceEquals(_requestCts, requestCts))
                                _requestCts = null;
                        }
                        requestCts.Dispose();
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception)
            {
                // Keep the worker task observed even if a platform async operation
                // fails outside the normal decode error path.
            }
        }

        private async Task DecodeAsync(string path, int version, CancellationToken cancellationToken)
        {
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(path)
                    .AsTask(cancellationToken).ConfigureAwait(false);
                using var stream = await file.OpenReadAsync()
                    .AsTask(cancellationToken).ConfigureAwait(false);
                var decoder = await BitmapDecoder.CreateAsync(stream)
                    .AsTask(cancellationToken).ConfigureAwait(false);
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
                    ColorManagementMode.ColorManageToSRgb)
                    .AsTask(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                var pending = new PendingImage(
                    pixels.DetachPixelData(),
                    (int)transform.ScaledWidth,
                    (int)transform.ScaledHeight,
                    version);

                lock (_pendingGate)
                {
                    if (!_disposed && _decodeEnabled && version == _sourceVersion &&
                        !cancellationToken.IsCancellationRequested)
                    {
                        _pending = pending;
                        _decodeNeeded = false;
                    }
                }
            }
            catch (OperationCanceledException)
            {
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
            lock (_pendingGate)
            {
                if (_disposed) return;
                _disposed = true;
                ++_sourceVersion;
                _pending = null;
                _requestCts?.Cancel();
                _lifetimeCts.Cancel();
            }

            lock (_resourceGate)
            {
                _blur?.Dispose();
                _blur = null;
                _bitmap?.Dispose();
                _bitmap = null;
            }

            // The worker is cancellation-aware. Dispose the synchronization objects
            // only after it leaves WinRT, otherwise a late callback can touch a
            // disposed semaphore/CTS and turn shutdown into an unobserved fault.
            var worker = _decodeWorker;
            if (worker is null || worker.IsCompleted)
            {
                _decodeSignal.Dispose();
                _lifetimeCts.Dispose();
            }
            else
            {
                _ = worker.ContinueWith(_ =>
                {
                    _decodeSignal.Dispose();
                    _lifetimeCts.Dispose();
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
    }
}
