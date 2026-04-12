using AnimatedWin2dControls.Utils;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;

namespace AnimatedWin2dControls.Controls
{
    /// <summary>
    /// 继承 Control 的专辑封面控件。
    /// ControlTemplate 在 Themes/Generic.xaml 中定义，
    /// 模板根为一个命名为 "canvas" 的 CanvasControl。
    /// </summary>
    [TemplatePart(Name = PartCanvas, Type = typeof(CanvasControl))]
    public sealed class AlbumArtControl : Control, IDisposable, ISharedTickable
    {
        private const string PartCanvas = "canvas";

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
            ctrl._canvas?.Invalidate();
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
            ctrl._canvas?.Invalidate();
        }

        private static void OnLayoutChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var ctrl = (AlbumArtControl)d;
            if (!ctrl._isResourcesCreated) return;
            if (!ctrl.IsActive) return;
            ctrl._maskInvalidated = true;
            ctrl._rtInvalidated = true;
            ctrl._canvas?.Invalidate();
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
                ctrl._canvas?.Invalidate();
            }
        }

        // ── 私有字段 ──────────────────────────────────────────────────────────

        // 模板应用后才赋值；依赖属性回调中通过 null 守卫保护
        private CanvasControl? _canvas;

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
        private bool _isClockRegistered = false;

        // ── 构造函数 ──────────────────────────────────────────────────────────

        public AlbumArtControl()
        {
            // 告知 WinUI/XAML 运行时去 Generic.xaml 里查找本类型的 ControlTemplate
            DefaultStyleKey = typeof(AlbumArtControl);

            Unloaded += (_, _) => Dispose(true);
        }

        // ── 模板应用 ──────────────────────────────────────────────────────────

        protected override void OnApplyTemplate()
        {
            base.OnApplyTemplate();

            // 若之前已绑定过（热重载等场景），先解绑旧实例
            if (_canvas != null)
            {
                _canvas.CreateResources -= Canvas_CreateResources;
                _canvas.Draw -= Canvas_Draw;
                _canvas = null;
            }

            _canvas = GetTemplateChild(PartCanvas) as CanvasControl;
            if (_canvas == null) return;

            _canvas.CreateResources += Canvas_CreateResources;
            _canvas.Draw += Canvas_Draw;
        }

        // ── Canvas 事件 ───────────────────────────────────────────────────────

        private void Canvas_CreateResources(CanvasControl sender,
            Microsoft.Graphics.Canvas.UI.CanvasCreateResourcesEventArgs e)
        {
            _isResourcesCreated = true;
            if (!IsActive) return;
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
            DrawImageLayer(e.DrawingSession);
            FlushDisposeQueue();
        }

        // ── 核心状态机 ────────────────────────────────────────────────────────

        private void EnqueueBitmap(CanvasBitmap newBitmap)
        {
            if (_isFading)
            {
                if (_queuedBitmap != null) _disposeQueue.Enqueue(_queuedBitmap);
                _queuedBitmap = newBitmap;
            }
            else
            {
                StartTransition(newBitmap);
            }

            StartRenderingLoop(); // 有动画任务时注册时钟
        }

        private void StartTransition(CanvasBitmap newBitmap)
        {
            if (_incomingBitmap != null)
            {
                if (_currentBitmap != null) _disposeQueue.Enqueue(_currentBitmap);
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
                if (_currentBitmap != null) _disposeQueue.Enqueue(_currentBitmap);
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
                var (pixels, bmpW, bmpH) = await Task.Run(async () =>
                {
                    using var mem = new MemoryStream(imageBytes, writable: false);
                    using var ras = mem.AsRandomAccessStream();
                    var decoder = await BitmapDecoder.CreateAsync(ras);

                    uint srcW = decoder.PixelWidth;
                    uint srcH = decoder.PixelHeight;
                    // 保持原始宽高比缩放到 HardMaxSize 以内
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

                ICanvasResourceCreator creator = resourceCreator ?? _canvas!;
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

                ICanvasResourceCreator creator = resourceCreator ?? _canvas!;
                var bmp = await CanvasBitmap.LoadAsync(creator, stream);
                EnqueueBitmap(bmp);
            }
            catch { }
        }

        public void OnSharedTick(TimeSpan elapsed)
        {
            // 没有进行中的动画就不做任何事
            if (!_isFading && _queuedBitmap == null) return;

            float delta = Math.Min((float)elapsed.TotalSeconds, 0.1f);
            // 复用原有的 UpdateFadeState 逻辑（原来在 Canvas_Draw 里内联的）
            UpdateFadeState(delta);
            _canvas?.Invalidate();

            // 动画结束后注销，停止空转
            if (!_isFading && _queuedBitmap == null)
                StopRenderingLoop();
        }

        private void StartRenderingLoop()
        {
            if (_isClockRegistered) return;
            SharedAnimationClock.Register(this);
            _isClockRegistered = true;
        }

        private void StopRenderingLoop()
        {
            if (!_isClockRegistered) return;
            SharedAnimationClock.Unregister(this);
            _isClockRegistered = false;
        }

        // ── 绘制 ──────────────────────────────────────────────────────────────

        private void DrawImageLayer(CanvasDrawingSession ds)
        {
            if (!IsActive || _canvas == null) return;

            float canvasW = (float)_canvas.Size.Width;
            float canvasH = (float)_canvas.Size.Height;
            if (canvasW <= 0 || canvasH <= 0) return;

            float padTop = (float)MarginTopRatio;
            float padBottom = (float)MarginBottomRatio;
            float padLeft = (float)MarginLeftRatio;
            float padRight = (float)MarginRightRatio;

            float contentX = padLeft;
            float contentY = padTop;
            float contentW = canvasW - padLeft - padRight;
            float contentH = canvasH - padTop - padBottom;
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

                // 按实际宽高比缩放到目标尺寸，不拉伸
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

        /// <summary>
        /// Aspect-fit：在 (cx, cy, cw, ch) 区域内保持图片原始宽高比居中绘制。
        /// 图片比例完全不变，仅缩放后居中。
        /// </summary>
        private static Windows.Foundation.Rect CalcDestRect(
            CanvasBitmap bmp, float cx, float cy, float cw, float ch)
        {
            float imgW = bmp.SizeInPixels.Width;
            float imgH = bmp.SizeInPixels.Height;
            if (imgW <= 0 || imgH <= 0) return new(cx, cy, cw, ch);

            float aspect = imgW / imgH;
            float drawW, drawH;
            // 以容器短边为基准，保证图片完整显示且不拉伸
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

            StopRenderingLoop(); // 新增

            if (_canvas != null)
            {
                _canvas.CreateResources -= Canvas_CreateResources;
                _canvas.Draw -= Canvas_Draw;
                _canvas = null;
            }

            _maskRT?.Dispose(); _maskRT = null;
            _currentBaked?.Dispose(); _currentBaked = null;
            _incomingBaked?.Dispose(); _incomingBaked = null;
            _currentBitmap?.Dispose(); _currentBitmap = null;
            _incomingBitmap?.Dispose(); _incomingBitmap = null;
            _queuedBitmap?.Dispose(); _queuedBitmap = null;

            while (_disposeQueue.TryDequeue(out var b)) b?.Dispose();
        }
    }

    // ── BakedRT ───────────────────────────────────────────────────────────────

    internal sealed class BakedRT : IDisposable
    {
        public readonly CanvasRenderTarget RT;
        public readonly float Pad;
        public BakedRT(CanvasRenderTarget rt, float pad) { RT = rt; Pad = pad; }
        public void Dispose() => RT.Dispose();
    }
}