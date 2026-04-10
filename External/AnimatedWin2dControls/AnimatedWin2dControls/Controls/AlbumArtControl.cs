using AnimatedWin2dControls.Utils;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;

namespace AnimatedWin2dControls.Controls
{
    /// <summary>
    /// 无 XAML 文件版本的专辑封面控件。
    /// 继承 UserControl，在构造函数里直接把 CanvasControl 赋给 Content，
    /// 与原版 UserControl + XAML 的运行时行为完全等价，无需 Generic.xaml。
    /// </summary>
    public sealed class AlbumArtControl : UserControl, IDisposable
    {
        // ── 依赖属性 ──────────────────────────────────────────────────────────

        public static readonly DependencyProperty DpiScaleProperty =
            DependencyProperty.Register(nameof(DpiScale), typeof(double),
                typeof(AlbumArtControl), new PropertyMetadata(1.0, OnDpiScaleChanged));

        public double DpiScale
        {
            get => (double)GetValue(DpiScaleProperty);
            set => SetValue(DpiScaleProperty, value);
        }

        private static void OnDpiScaleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var ctrl = (AlbumArtControl)d;
            if (!ctrl._isResourcesCreated) return;
            ctrl._maskInvalidated = true;
            ctrl._rtInvalidated = true;
            ctrl._canvas.Invalidate();
        }

        public static readonly DependencyProperty ImageBytesProperty =
            DependencyProperty.Register(nameof(ImageBytes), typeof(byte[]),
                typeof(AlbumArtControl), new PropertyMetadata(null, OnImageBytesChanged));

        public byte[] ImageBytes
        {
            get => (byte[])GetValue(ImageBytesProperty);
            set => SetValue(ImageBytesProperty, value);
        }

        private static void OnImageBytesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var ctrl = (AlbumArtControl)d;
            if (!ctrl._isResourcesCreated) return;
            if (!ctrl.IsActive) return;

            var newBytes = e.NewValue as byte[];
            if (ctrl.IsDuplicateAndUpdate(newBytes)) return;

            if (newBytes is { Length: > 0 })
                _ = ctrl.LoadBitmapAsync(newBytes);
            else
                _ = ctrl.LoadDefaultCoverAsync();
        }

        public static readonly DependencyProperty IsDarkProperty =
            DependencyProperty.Register(nameof(IsDark), typeof(bool),
                typeof(AlbumArtControl), new PropertyMetadata(true, OnIsDarkChanged));

        public bool IsDark
        {
            get => (bool)GetValue(IsDarkProperty);
            set => SetValue(IsDarkProperty, value);
        }

        private static void OnIsDarkChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var ctrl = (AlbumArtControl)d;
            if (!ctrl._isResourcesCreated) return;
            if (!ctrl.IsActive) return;
            ctrl.InvalidateDedup();
            if (ctrl.ImageBytes is { Length: > 0 })
                _ = ctrl.LoadBitmapAsync(ctrl.ImageBytes);
            else
                _ = ctrl.LoadDefaultCoverAsync();
        }

        public static readonly DependencyProperty MarginTopRatioProperty =
            DependencyProperty.Register(nameof(MarginTopRatio), typeof(double),
                typeof(AlbumArtControl), new PropertyMetadata(20.0, OnLayoutChanged));
        public double MarginTopRatio
        {
            get => (double)GetValue(MarginTopRatioProperty);
            set => SetValue(MarginTopRatioProperty, value);
        }

        public static readonly DependencyProperty MarginBottomRatioProperty =
            DependencyProperty.Register(nameof(MarginBottomRatio), typeof(double),
                typeof(AlbumArtControl), new PropertyMetadata(20.0, OnLayoutChanged));
        public double MarginBottomRatio
        {
            get => (double)GetValue(MarginBottomRatioProperty);
            set => SetValue(MarginBottomRatioProperty, value);
        }

        public static readonly DependencyProperty MarginLeftRatioProperty =
            DependencyProperty.Register(nameof(MarginLeftRatio), typeof(double),
                typeof(AlbumArtControl), new PropertyMetadata(20.0, OnLayoutChanged));
        public double MarginLeftRatio
        {
            get => (double)GetValue(MarginLeftRatioProperty);
            set => SetValue(MarginLeftRatioProperty, value);
        }

        public static readonly DependencyProperty MarginRightRatioProperty =
            DependencyProperty.Register(nameof(MarginRightRatio), typeof(double),
                typeof(AlbumArtControl), new PropertyMetadata(20.0, OnLayoutChanged));
        public double MarginRightRatio
        {
            get => (double)GetValue(MarginRightRatioProperty);
            set => SetValue(MarginRightRatioProperty, value);
        }

        // Control 基类自带 CornerRadius，此处改名为 ArtCornerRadius 避免冲突
        public static readonly DependencyProperty ArtCornerRadiusProperty =
            DependencyProperty.Register(nameof(ArtCornerRadius), typeof(double),
                typeof(AlbumArtControl), new PropertyMetadata(16.0, OnLayoutChanged));
        public double ArtCornerRadius
        {
            get => (double)GetValue(ArtCornerRadiusProperty);
            set => SetValue(ArtCornerRadiusProperty, value);
        }

        public static readonly DependencyProperty IsShadowEnabledProperty =
            DependencyProperty.Register(nameof(IsShadowEnabled), typeof(bool),
                typeof(AlbumArtControl), new PropertyMetadata(true, OnShadowEnabledChanged));
        public bool IsShadowEnabled
        {
            get => (bool)GetValue(IsShadowEnabledProperty);
            set => SetValue(IsShadowEnabledProperty, value);
        }

        private static void OnShadowEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var ctrl = (AlbumArtControl)d;
            if (!ctrl._isResourcesCreated) return;
            ctrl._rtInvalidated = true;
            ctrl._canvas.Invalidate();
        }

        private static void OnLayoutChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var ctrl = (AlbumArtControl)d;
            if (!ctrl._isResourcesCreated) return;
            if (!ctrl.IsActive) return;
            ctrl._maskInvalidated = true;
            ctrl._rtInvalidated = true;
            ctrl._canvas.Invalidate();
        }

        public static readonly DependencyProperty IsActiveProperty =
            DependencyProperty.Register(nameof(IsActive), typeof(bool),
                typeof(AlbumArtControl), new PropertyMetadata(false, OnIsActiveChanged));

        public bool IsActive
        {
            get => (bool)GetValue(IsActiveProperty);
            set => SetValue(IsActiveProperty, value);
        }

        private static void OnIsActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var ctrl = (AlbumArtControl)d;
            if (!ctrl._isResourcesCreated) return;

            if ((bool)e.NewValue)
            {
                if (ctrl.ImageBytes is { Length: > 0 } bytes)
                    _ = ctrl.LoadBitmapAsync(bytes);
                else
                    _ = ctrl.LoadDefaultCoverAsync();
            }
            else
            {
                ctrl._loadCts?.Cancel();
                ctrl._isFading = false;
                ctrl._canvas.Invalidate();
            }
        }

        // ── 私有字段 ──────────────────────────────────────────────────────────

        private readonly CanvasControl _canvas;

        private CanvasBitmap? _currentBitmap;
        private float _currentAlpha = 0f;

        private CanvasBitmap? _incomingBitmap;
        private float _incomingAlpha = 0f;

        private bool _isFading = false;
        private const float FadeSpeed = 1.5f;

        private CanvasBitmap? _queuedBitmap;
        private readonly Queue<CanvasBitmap> _disposeQueue = new();

        private CancellationTokenSource? _loadCts;
        private bool _isResourcesCreated = false;
        private bool _disposed = false;

        private long _lastDrawTicks = 0;
        private const float HardMaxSize = 1536f;

        private long _lastLength = -1;
        private int _lastHash;

        private CanvasRenderTarget? _maskRT;
        private (float w, float h, float radius) _maskSize;
        private bool _maskInvalidated = false;

        private BakedRT? _currentBaked;
        private BakedRT? _incomingBaked;
        private (float w, float h) _lastBakeSize;
        private bool _rtInvalidated = false;

        // ── 构造函数 ──────────────────────────────────────────────────────────

        public AlbumArtControl()
        {
            // UserControl.Content 直接就是视觉根，不经过 ControlTemplate 展开，
            // 与原版 <canvas:CanvasControl x:Name="canvas"/> 在 XAML 里声明完全等价。
            // Win2D 的 CreateResources 和 Draw 事件在 CanvasControl 进入视觉树后正常触发。
            _canvas = new CanvasControl { ClearColor = Microsoft.UI.Colors.Transparent };
            Content = _canvas;

            _canvas.CreateResources += Canvas_CreateResources;
            _canvas.Draw += Canvas_Draw;

            Unloaded += (_, _) => Dispose(true);
        }

        // ── Canvas 事件 ───────────────────────────────────────────────────────

        private void Canvas_CreateResources(CanvasControl sender,
            Microsoft.Graphics.Canvas.UI.CanvasCreateResourcesEventArgs e)
        {
            _isResourcesCreated = true;
            if (!IsActive) return;

            // TrackAsyncAction：将异步任务的生命周期注册给 Win2D。
            // 若改用 async void 直接 await，handler 在第一个挂起点返回，
            // Win2D 认为 CreateResources 已完成并可能令 Device 失效，
            // 导致后续 CanvasBitmap 操作抛出"no CanvasDevice associated"。
            e.TrackAsyncAction(CreateResourcesAsync(sender).AsAsyncAction());
        }

        private async Task CreateResourcesAsync(CanvasControl sender)
        {
            if (ImageBytes is { Length: > 0 } bytes)
                await LoadBitmapAsync(bytes, sender);
            else
                await LoadDefaultCoverAsync(sender);
        }

        private void Canvas_Draw(CanvasControl sender, CanvasDrawEventArgs e)
        {
            long nowTicks = DateTime.UtcNow.Ticks;
            float delta = _lastDrawTicks == 0
                ? 0f
                : (float)((nowTicks - _lastDrawTicks) / (double)TimeSpan.TicksPerSecond);
            delta = Math.Min(delta, 0.1f);
            _lastDrawTicks = nowTicks;

            if (_isFading)
                UpdateFadeState(delta);

            DrawImageLayer(e.DrawingSession);

            if (_isFading || _queuedBitmap != null)
                _canvas.Invalidate();
            else
                FlushDisposeQueue();
        }

        // ── 核心状态机 ────────────────────────────────────────────────────────

        private void EnqueueBitmap(CanvasBitmap newBitmap)
        {
            if (_isFading)
            {
                if (_queuedBitmap != null)
                    _disposeQueue.Enqueue(_queuedBitmap);
                _queuedBitmap = newBitmap;
            }
            else
            {
                StartTransition(newBitmap);
            }
            _lastDrawTicks = 0;
            _canvas.Invalidate();
        }

        private void StartTransition(CanvasBitmap newBitmap)
        {
            if (_incomingBitmap != null)
            {
                if (_currentBitmap != null)
                    _disposeQueue.Enqueue(_currentBitmap);

                _currentBaked?.Dispose();
                _currentBaked = _incomingBaked;
                _incomingBaked = null;

                _currentBitmap = _incomingBitmap;
                _currentAlpha = _incomingAlpha;
                _incomingBitmap = null;
                _incomingAlpha = 0f;
            }

            _incomingBitmap = newBitmap;
            _incomingAlpha = 0f;
            _incomingBaked = null;
            _isFading = true;
        }

        private void UpdateFadeState(float delta)
        {
            FlushDisposeQueue();
            if (!_isFading) return;

            if (_currentBitmap != null) _currentAlpha = Math.Max(0f, _currentAlpha - delta * FadeSpeed);
            if (_incomingBitmap != null) _incomingAlpha = Math.Min(1f, _incomingAlpha + delta * FadeSpeed);

            if (_incomingAlpha >= 1f)
            {
                if (_currentBitmap != null)
                    _disposeQueue.Enqueue(_currentBitmap);

                _currentBaked?.Dispose();
                _currentBaked = _incomingBaked;
                _incomingBaked = null;

                _currentBitmap = _incomingBitmap;
                _currentAlpha = 1f;
                _incomingBitmap = null;
                _incomingAlpha = 0f;
                _isFading = false;

                if (_queuedBitmap != null)
                {
                    var next = _queuedBitmap;
                    _queuedBitmap = null;
                    StartTransition(next);
                }
            }
        }

        private void FlushDisposeQueue()
        {
            while (_disposeQueue.TryDequeue(out var bmp))
            {
                if (bmp != null
                    && bmp != _currentBitmap
                    && bmp != _incomingBitmap
                    && bmp != _queuedBitmap)
                    bmp.Dispose();
            }
        }

        // ── 去重 ──────────────────────────────────────────────────────────────

        public bool IsDuplicateAndUpdate(byte[]? newBytes)
        {
            if (newBytes is not { Length: > 0 })
            {
                bool wasEmpty = _lastLength == 0;
                _lastLength = 0; _lastHash = 0;
                return wasEmpty;
            }
            int hash = ToolUtils.ComputeFastHash(newBytes);
            if (newBytes.Length == _lastLength && hash == _lastHash) return true;
            _lastLength = newBytes.Length;
            _lastHash = hash;
            return false;
        }

        public void InvalidateDedup() { _lastLength = -1; _lastHash = 0; }

        // ── 加载 ──────────────────────────────────────────────────────────────

        /// <param name="resourceCreator">
        /// CreateResources 路径传入事件的 sender（Win2D 保证其 Device 全程有效）。
        /// 依赖属性变更路径传 null，降级使用 _canvas（此时控件已完全初始化）。
        /// </param>
        public async Task LoadBitmapAsync(byte[]? imageBytes,
                                          ICanvasResourceCreator? resourceCreator = null)
        {
            if (imageBytes == null || imageBytes.Length == 0)
            {
                await LoadDefaultCoverAsync(resourceCreator);
                return;
            }

            _loadCts?.Cancel();
            var cts = new CancellationTokenSource();
            _loadCts = cts;

            try
            {
                // 像素解码在后台线程完成，完全不涉及 Device
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

                    cts.Token.ThrowIfCancellationRequested();

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
                }, cts.Token);

                cts.Token.ThrowIfCancellationRequested();

                // CanvasBitmap.CreateFromBytes 必须在 UI 线程上调用，
                // 使用 resourceCreator（sender）或降级到 _canvas 作为 Device 来源
                ICanvasResourceCreator creator = resourceCreator ?? _canvas;
                var bmp = CanvasBitmap.CreateFromBytes(
                    creator, pixels, (int)bmpW, (int)bmpH,
                    Windows.Graphics.DirectX.DirectXPixelFormat.R8G8B8A8UIntNormalized);

                pixels = null;
                EnqueueBitmap(bmp);
            }
            catch (OperationCanceledException) { }
            catch
            {
                InvalidateDedup();
                await LoadDefaultCoverAsync(resourceCreator);
            }
            finally
            {
                if (ReferenceEquals(_loadCts, cts)) _loadCts = null;
                cts.Dispose();
            }
        }

        public async Task LoadDefaultCoverAsync(ICanvasResourceCreator? resourceCreator = null)
        {
            try
            {
                string fileName = IsDark ? "default_cover_black.png" : "default_cover_white.png";
                string path = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "Assets", fileName);

                var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
                using var stream = await file.OpenReadAsync();

                ICanvasResourceCreator creator = resourceCreator ?? _canvas;
                var bmp = await CanvasBitmap.LoadAsync(creator, stream);
                EnqueueBitmap(bmp);
            }
            catch { }
        }

        // ── 绘制 ──────────────────────────────────────────────────────────────

        private void DrawImageLayer(CanvasDrawingSession ds)
        {
            if (!IsActive) return;

            float canvasW = (float)_canvas.Size.Width;
            float canvasH = (float)_canvas.Size.Height;
            if (canvasW <= 0 || canvasH <= 0) return;

            float squareSize = Math.Min(canvasW, canvasH);
            float squareX = (canvasW - squareSize) * 0.5f;
            float squareY = (canvasH - squareSize) * 0.5f;

            float padTop = (float)MarginTopRatio;
            float padBottom = (float)MarginBottomRatio;
            float padLeft = (float)MarginLeftRatio;
            float padRight = (float)MarginRightRatio;

            float contentX = squareX + padLeft;
            float contentY = squareY + padTop;
            float contentW = squareSize - padLeft - padRight;
            float contentH = squareSize - padTop - padBottom;
            if (contentW <= 0 || contentH <= 0) return;

            CanvasBitmap? refBmp = _incomingBitmap ?? _currentBitmap;
            if (refBmp == null) return;

            var destRect = CalcDestRect(refBmp, contentX, contentY, contentW, contentH);
            float w = (float)destRect.Width;
            float h = (float)destRect.Height;
            float radius = (float)ArtCornerRadius;

            EnsureMaskRenderTarget(ds.Device, w, h, radius);

            if (MathF.Abs(_lastBakeSize.w - w) > 0.5f || MathF.Abs(_lastBakeSize.h - h) > 0.5f)
                _rtInvalidated = true;

            if (_rtInvalidated)
            {
                _currentBaked?.Dispose(); _currentBaked = null;
                _incomingBaked?.Dispose(); _incomingBaked = null;
                _rtInvalidated = false;
                _lastBakeSize = (w, h);
            }

            if (_currentBitmap != null && _currentBaked == null)
                _currentBaked = BakeRenderTarget(ds.Device, _currentBitmap, w, h);
            if (_incomingBitmap != null && _incomingBaked == null)
                _incomingBaked = BakeRenderTarget(ds.Device, _incomingBitmap, w, h);

            if (_currentBaked != null && _currentAlpha > 0f)
                ds.DrawImage(_currentBaked.RT,
                    new Vector2((float)destRect.X - _currentBaked.Pad,
                                (float)destRect.Y - _currentBaked.Pad),
                    _currentBaked.RT.Bounds, _currentAlpha);

            if (_incomingBaked != null && _incomingAlpha > 0f)
                ds.DrawImage(_incomingBaked.RT,
                    new Vector2((float)destRect.X - _incomingBaked.Pad,
                                (float)destRect.Y - _incomingBaked.Pad),
                    _incomingBaked.RT.Bounds, _incomingAlpha);
        }

        // ── RenderTarget 烘焙 ─────────────────────────────────────────────────

        private BakedRT BakeRenderTarget(CanvasDevice device,
                                         CanvasBitmap bitmap,
                                         float w, float h)
        {
            float pad = IsShadowEnabled ? 10f * 3f + 4f : 0f;
            float rtW = w + pad * 2f;
            float rtH = h + pad * 2f;
            float dpi = 96f * (float)DpiScale;
            var rt = new CanvasRenderTarget(device, rtW, rtH, dpi);

            try
            {
                using var rtDs = rt.CreateDrawingSession();
                rtDs.Clear(Microsoft.UI.Colors.Transparent);

                using var scale = new ScaleEffect
                {
                    Source = bitmap,
                    Scale = new Vector2(w / bitmap.SizeInPixels.Width,
                                                    h / bitmap.SizeInPixels.Height),
                    InterpolationMode = CanvasImageInterpolation.HighQualityCubic
                };
                using var masked = new AlphaMaskEffect
                {
                    Source = scale,
                    AlphaMask = _maskRT!
                };

                if (IsShadowEnabled)
                {
                    using var shadow = new ShadowEffect
                    {
                        Source = masked,
                        BlurAmount = 10f,
                        ShadowColor = Windows.UI.Color.FromArgb(100, 0, 0, 0)
                    };
                    using var shadowOffset = new Transform2DEffect
                    {
                        Source = shadow,
                        TransformMatrix = Matrix3x2.CreateTranslation(2f, 3f)
                    };
                    using var composite = new CompositeEffect();
                    composite.Sources.Add(shadowOffset);
                    composite.Sources.Add(masked);
                    rtDs.DrawImage(composite, pad, pad);
                }
                else
                {
                    rtDs.DrawImage(masked, pad, pad);
                }
            }
            catch { }

            return new BakedRT(rt, pad);
        }

        // ── 遮罩缓存 ──────────────────────────────────────────────────────────

        private void EnsureMaskRenderTarget(CanvasDevice device,
                                            float w, float h, float radius)
        {
            if (!_maskInvalidated
                && _maskRT != null
                && MathF.Abs(_maskSize.w - w) < 0.5f
                && MathF.Abs(_maskSize.h - h) < 0.5f
                && MathF.Abs(_maskSize.radius - radius) < 0.5f)
                return;

            _maskRT?.Dispose();
            float dpi = 96f * (float)DpiScale;
            _maskRT = new CanvasRenderTarget(device, w, h, dpi);

            using (var maskDs = _maskRT.CreateDrawingSession())
            {
                maskDs.Clear(Microsoft.UI.Colors.Transparent);
                maskDs.FillRoundedRectangle(0, 0, w, h, radius, radius,
                                            Microsoft.UI.Colors.White);
            }

            _maskSize = (w, h, radius);
            _maskInvalidated = false;

            _currentBaked?.Dispose(); _currentBaked = null;
            _incomingBaked?.Dispose(); _incomingBaked = null;
        }

        // ── 辅助 ──────────────────────────────────────────────────────────────

        private static Windows.Foundation.Rect CalcDestRect(
            CanvasBitmap bmp, float cx, float cy, float cw, float ch)
        {
            float imgW = bmp.SizeInPixels.Width;
            float imgH = bmp.SizeInPixels.Height;
            if (imgW <= 0 || imgH <= 0) return new(cx, cy, cw, ch);

            float aspect = imgW / imgH;
            float drawW, drawH;
            if (aspect >= cw / ch) { drawW = cw; drawH = drawW / aspect; }
            else { drawH = ch; drawW = drawH * aspect; }

            return new(cx + (cw - drawW) * 0.5f,
                       cy + (ch - drawH) * 0.5f,
                       drawW, drawH);
        }

        // ── 释放 ──────────────────────────────────────────────────────────────

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            if (!disposing || _disposed) return;
            _disposed = true;

            _loadCts?.Cancel();
            _loadCts?.Dispose();
            _loadCts = null;

            _canvas.CreateResources -= Canvas_CreateResources;
            _canvas.Draw -= Canvas_Draw;

            _maskRT?.Dispose(); _maskRT = null;
            _currentBaked?.Dispose(); _currentBaked = null;
            _incomingBaked?.Dispose(); _incomingBaked = null;
            _currentBitmap?.Dispose(); _currentBitmap = null;
            _incomingBitmap?.Dispose(); _incomingBitmap = null;
            _queuedBitmap?.Dispose(); _queuedBitmap = null;

            while (_disposeQueue.TryDequeue(out var b)) b?.Dispose();
        }
    }

    // ── BakedRT ───────────────────────────────────────────────────────────────────

    internal sealed class BakedRT : IDisposable
    {
        public readonly CanvasRenderTarget RT;
        public readonly float Pad;
        public BakedRT(CanvasRenderTarget rt, float pad) { RT = rt; Pad = pad; }
        public void Dispose() => RT.Dispose();
    }
}
