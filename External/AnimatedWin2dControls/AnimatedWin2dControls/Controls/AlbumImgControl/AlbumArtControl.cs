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
using Windows.Foundation;
using Windows.Graphics.Imaging;

namespace AnimatedWin2dControls.Controls.AlbumImgControl
{
    [TemplatePart(Name = PartCanvas, Type = typeof(CanvasControl))]
    public sealed class AlbumArtControl : Control, IDisposable, ISharedTickable
    {
        private const string PartCanvas = "canvas";

        // ── 依赖属性（保持不变） ──────────────────────────────────────────────

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
            var c = (AlbumArtControl)d;
            if (!c._isResourcesCreated) return;
            c._maskInvalidated = true;
            c._rtInvalidated = true;
            c._canvas?.Invalidate();
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
            var c = (AlbumArtControl)d;
            if (!c._isResourcesCreated || !c.IsActive) return;
            var newBytes = e.NewValue as byte[];
            if (c.IsDuplicateAndUpdate(newBytes)) return;
            if (newBytes is { Length: > 0 }) _ = c.LoadBitmapAsync(newBytes);
            else _ = c.LoadDefaultCoverAsync();
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
            var c = (AlbumArtControl)d;
            if (!c._isResourcesCreated || !c.IsActive) return;
            c.InvalidateDedup();
            if (c.ImageBytes is { Length: > 0 }) _ = c.LoadBitmapAsync(c.ImageBytes);
            else _ = c.LoadDefaultCoverAsync();
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
            var c = (AlbumArtControl)d;
            if (!c._isResourcesCreated) return;
            c._rtInvalidated = true;
            c._canvas?.Invalidate();
        }

        private static void OnLayoutChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var c = (AlbumArtControl)d;
            if (!c._isResourcesCreated || !c.IsActive) return;
            c._maskInvalidated = true;
            c._rtInvalidated = true;
            c._canvas?.Invalidate();
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
            var c = (AlbumArtControl)d;
            if (!c._isResourcesCreated) return;
            if ((bool)e.NewValue)
            {
                if (c.ImageBytes is { Length: > 0 } b) _ = c.LoadBitmapAsync(b);
                else _ = c.LoadDefaultCoverAsync();
            }
            else
            {
                c._loadCts?.Cancel();
                c._isFading = false;
                c._canvas?.Invalidate();
            }
        }

        // ── 私有字段 ──────────────────────────────────────────────────────────

        private CanvasControl? _canvas;
        private bool _isResourcesCreated;
        private bool _disposed;

        // ── 单张 RT 架构 ──────────────────────────────────────────────────────
        // 当前正在显示的 baked RT（已含圆角+阴影）
        private BakedRT? _currentRT;
        // 即将替换 _currentRT 的下一张 RT（bake 完成后在 Draw 里切换）
        private BakedRT? _nextRT;

        // 解码完成、等待在 Draw 里 bake 的 bitmap
        // （bake 必须在 UI 线程，所以不能在 Task.Run 里完成）
        private CanvasBitmap? _pendingBitmap;
        // 当前 RT 对应的原始 bitmap（用于 _rtInvalidated 时重新 bake）
        private CanvasBitmap? _currentBitmap;
        // 队列中最新一张（过渡期间到来的更新只保留最新）
        private CanvasBitmap? _queuedBitmap;

        private readonly Queue<object> _disposeQueue = new();

        // ── Scale-fade 过渡 ───────────────────────────────────────────────────
        // t ∈ [0,1]：0=旧图全显，1=新图全显
        // [0, 0.5)：旧图 scale-out + fade-out
        // [0.5, 1]：新图 scale-in  + fade-in
        private float _t;
        private bool _isFading;
        private const float FadeSpeed = 2.2f;   // 整个动画约 0.45s
        private const float ScaleSmall = 0.90f; // 缩放最小值（新图从此 scale in）

        // ── 内容区域缓存 ──────────────────────────────────────────────────────
        private Rect _contentRect;

        // ── Mask / RT 缓存 ────────────────────────────────────────────────────
        private CanvasRenderTarget? _maskRT;
        private (float w, float h, float radius) _maskSize;
        private bool _maskInvalidated;
        private bool _rtInvalidated;
        private (float w, float h) _lastBakeSize;

        private CancellationTokenSource? _loadCts;
        private bool _isClockRegistered;

        private long _lastLength = -1;
        private int _lastHash;
        private const float HardMaxSize = 1280f;
        private readonly SemaphoreSlim _decodeSemaphore = new(1, 1);
        private CancellationTokenSource? _decodeCts; // 独立于 _loadCts

        // ── 构造 ──────────────────────────────────────────────────────────────

        public AlbumArtControl()
        {
            DefaultStyleKey = typeof(AlbumArtControl);
            Unloaded += (_, _) => Dispose(true);
        }

        protected override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
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
            if (ImageBytes is { Length: > 0 } b) await LoadBitmapAsync(b, sender);
            else await LoadDefaultCoverAsync(sender);
        }

        private void Canvas_Draw(CanvasControl sender, CanvasDrawEventArgs e)
        {
            // ── 在 Draw 回调里做 bake（保证 UI 线程 + GPU 上下文就绪）──────
            TryBakePending(sender);

            DrawFrame(e.DrawingSession, sender);
            FlushDisposeQueue();
        }

        // ── Pending bake（UI 线程，Draw 回调内执行）──────────────────────────

        /// <summary>
        /// 如果有待 bake 的 bitmap，在此处同步完成 bake 并存入 _nextRT。
        /// 全程在 UI 线程，不涉及跨线程 GPU 资源创建。
        /// </summary>
        private void TryBakePending(CanvasControl sender)
        {
            if (_pendingBitmap == null) return;
            var bitmap = _pendingBitmap;
            _pendingBitmap = null;

            float cw = (float)sender.Size.Width;
            float ch = (float)sender.Size.Height;
            if (cw <= 0 || ch <= 0) return;

            ComputeContentRect(cw, ch);
            if (_contentRect == Rect.Empty) return;

            // letterbox 计算出实际绘制尺寸，bake 就按这个尺寸做
            Rect destRect = CalcDestRect(bitmap, _contentRect);
            float bakeW = (float)destRect.Width;
            float bakeH = (float)destRect.Height;
            if (bakeW <= 0 || bakeH <= 0) return;

            float radius = (float)ArtCornerRadius;
            EnsureMask(sender.Device, bakeW, bakeH, radius);
            if (_maskRT == null) return;

            var baked = BakeCore(sender.Device, bitmap, bakeW, bakeH,
                                  _maskRT, IsShadowEnabled, (float)DpiScale);

            if (_nextRT != null) _disposeQueue.Enqueue(_nextRT);
            _nextRT = baked;

            if (_currentBitmap != null && _currentBitmap != bitmap)
                _disposeQueue.Enqueue(_currentBitmap);
            _currentBitmap = bitmap;

            StartTransition();
        }

        // ── 状态机 ────────────────────────────────────────────────────────────

        private void StartTransition()
        {
            _t = 0f;
            _isFading = true;
            StartRenderingLoop();
        }

        /// <summary>
        /// 新 bitmap 解码完成后调用。
        /// 过渡中：只更新队列（只保留最新），不立刻插入新过渡。
        /// 空闲时：直接存入 _pendingBitmap，触发下一帧 bake。
        /// </summary>
        private void EnqueueDecoded(CanvasBitmap bitmap)
        {
            if (_isFading)
            {
                if (_queuedBitmap != null) _disposeQueue.Enqueue(_queuedBitmap);
                _queuedBitmap = bitmap;
                // 不调用 SetPending，等当前过渡结束后由 OnSharedTick 拉队列
            }
            else
            {
                SetPending(bitmap);
            }
        }


        private void SetPending(CanvasBitmap bitmap)
        {
            if (_pendingBitmap != null) _disposeQueue.Enqueue(_pendingBitmap);
            _pendingBitmap = bitmap;
            _canvas?.Invalidate();
        }

        // ── Tick ─────────────────────────────────────────────────────────────

        public void OnSharedTick(TimeSpan elapsed)
        {
            if (!_isFading && _queuedBitmap == null) return;

            float delta = Math.Min((float)elapsed.TotalSeconds, 0.1f);
            bool wasF = _isFading;

            if (_isFading)
            {
                _t = Math.Min(1f, _t + delta * FadeSpeed);

                if (_t >= 0.5f && _nextRT != null)
                {
                    if (_currentRT != null) _disposeQueue.Enqueue(_currentRT);
                    _currentRT = _nextRT;
                    _nextRT = null;
                }

                if (_t >= 1f)
                {
                    _t = 0f;
                    _isFading = false;
                }
            }

            // 过渡刚结束，拉队列里最新的一张
            if (wasF && !_isFading && _queuedBitmap != null)
            {
                var next = _queuedBitmap;
                _queuedBitmap = null;
                SetPending(next);
            }

            if (_isFading || wasF != _isFading) _canvas?.Invalidate();
            if (!_isFading && _queuedBitmap == null) StopRenderingLoop();
        }

        // ── 绘制 ──────────────────────────────────────────────────────────────

        // ── DrawFrame：用 CalcDestRect 计算 letterbox 矩形传给 DrawBaked ───────
        private void DrawFrame(CanvasDrawingSession ds, CanvasControl sender)
        {
            if (!IsActive) return;
            float cw = (float)sender.Size.Width;
            float ch = (float)sender.Size.Height;
            if (cw <= 0 || ch <= 0) return;

            ComputeContentRect(cw, ch);
            if (_contentRect == Rect.Empty) return;

            // 尺寸变化检测（以 contentRect 的宽高为基准）
            float contentW = (float)_contentRect.Width;
            float contentH = (float)_contentRect.Height;
            if (MathF.Abs(_lastBakeSize.w - contentW) > 0.5f ||
                MathF.Abs(_lastBakeSize.h - contentH) > 0.5f)
            {
                _lastBakeSize = (contentW, contentH);
                _rtInvalidated = true;
            }

            if (_rtInvalidated)
            {
                _rtInvalidated = false;
                _maskInvalidated = true;
                if (_currentBitmap != null)
                {
                    if (_pendingBitmap != null) _disposeQueue.Enqueue(_pendingBitmap);
                    _pendingBitmap = _currentBitmap;
                }
                if (_currentRT != null) { _disposeQueue.Enqueue(_currentRT); _currentRT = null; }
                if (_nextRT != null) { _disposeQueue.Enqueue(_nextRT); _nextRT = null; }
                _canvas?.Invalidate();
                return; // 本帧跳过绘制，下一帧 TryBakePending 会重建
            }

            if (_currentRT == null && _nextRT == null) return;

            // 用 BakedRT 里记录的 srcW/srcH 还原 aspect ratio，计算 letterbox 矩形
            BakedRT? drawRT = _currentRT ?? _nextRT;
            Rect destRect = drawRT != null
                ? CalcDestRectFromSize(drawRT.SrcW, drawRT.SrcH, _contentRect)
                : _contentRect;

            if (!_isFading)
            {
                if (_currentRT != null)
                    DrawBaked(ds, _currentRT, destRect, 1f, 1f);
            }
            else
            {
                float t = _t;
                if (t < 0.5f)
                {
                    float localT = t / 0.5f;
                    float easedT = EaseIn(localT);
                    float alpha = 1f - easedT;
                    float scale = 1f - (1f - ScaleSmall) * easedT;
                    if (_currentRT != null && alpha > 0f)
                    {
                        Rect r = CalcDestRectFromSize(_currentRT.SrcW, _currentRT.SrcH, _contentRect);
                        DrawBaked(ds, _currentRT, r, alpha, scale);
                    }
                }
                else
                {
                    float localT = (t - 0.5f) / 0.5f;
                    float easedT = EaseOut(localT);
                    float alpha = easedT;
                    float scale = ScaleSmall + (1f - ScaleSmall) * easedT;
                    if (_currentRT != null && alpha > 0f)
                    {
                        Rect r = CalcDestRectFromSize(_currentRT.SrcW, _currentRT.SrcH, _contentRect);
                        DrawBaked(ds, _currentRT, r, alpha, scale);
                    }
                }
            }
        }

        private static Rect CalcDestRectFromSize(float srcW, float srcH, Rect contentRect)
        {
            float cw = (float)contentRect.Width;
            float ch = (float)contentRect.Height;
            if (srcW <= 0 || srcH <= 0 || cw <= 0 || ch <= 0)
                return contentRect;

            float aspect = srcW / srcH;
            float drawW, drawH;
            if (aspect >= cw / ch) { drawW = cw; drawH = drawW / aspect; }
            else { drawH = ch; drawW = drawH * aspect; }

            return new Rect(
                contentRect.X + (cw - drawW) * 0.5f,
                contentRect.Y + (ch - drawH) * 0.5f,
                drawW, drawH);
        }

        private static void DrawBaked(CanvasDrawingSession ds,
                                       BakedRT baked, Rect destRect,
                                       float alpha, float scale)
        {
            if (destRect.Width <= 0 || destRect.Height <= 0) return;

            double cx = destRect.X + destRect.Width * 0.5;
            double cy = destRect.Y + destRect.Height * 0.5;
            double dw = (destRect.Width + baked.Pad * 2) * scale;
            double dh = (destRect.Height + baked.Pad * 2) * scale;
            var dest = new Rect(cx - dw * 0.5, cy - dh * 0.5, dw, dh);

            ds.DrawImage(baked.RT, dest, baked.RT.Bounds, alpha,
                         CanvasImageInterpolation.Linear);
        }

        // ── 缓动 ─────────────────────────────────────────────────────────────

        private static float EaseIn(float t) => t * t;              // 加速淡出
        private static float EaseOut(float t) => 1f - (1f - t) * (1f - t); // 减速淡入

        // ── 内容矩形 ──────────────────────────────────────────────────────────

        private void ComputeContentRect(float cw, float ch)
        {
            float x = (float)MarginLeftRatio;
            float y = (float)MarginTopRatio;
            float w = cw - x - (float)MarginRightRatio;
            float h = ch - y - (float)MarginBottomRatio;
            _contentRect = w > 0 && h > 0 ? new Rect(x, y, w, h) : Rect.Empty;
        }

        // ── Mask ──────────────────────────────────────────────────────────────

        private void EnsureMask(CanvasDevice device, float w, float h, float radius)
        {
            if (!_maskInvalidated && _maskRT != null
                && MathF.Abs(_maskSize.w - w) < 0.5f
                && MathF.Abs(_maskSize.h - h) < 0.5f
                && MathF.Abs(_maskSize.radius - radius) < 0.5f)
                return;

            _maskRT?.Dispose();
            float dpi = 96f * (float)DpiScale;
            _maskRT = new CanvasRenderTarget(device, w, h, dpi);
            using (var mds = _maskRT.CreateDrawingSession())
            {
                mds.Clear(Microsoft.UI.Colors.Transparent);
                mds.FillRoundedRectangle(0, 0, w, h, radius, radius, Microsoft.UI.Colors.White);
            }
            _maskSize = (w, h, radius);
            _maskInvalidated = false;
        }

        // ── Bake（全部在 UI 线程，由 Canvas_Draw → TryBakePending 调用）──────

        private static BakedRT BakeCore(
    CanvasDevice device, CanvasBitmap bitmap,
    float w, float h,
    CanvasRenderTarget maskRT,
    bool shadow, float dpiScale)
        {
            float pad = shadow ? 34f : 0f;
            var rt = new CanvasRenderTarget(device, w + pad * 2, h + pad * 2, 96f * dpiScale);

            using var rtDs = rt.CreateDrawingSession();
            rtDs.Clear(Microsoft.UI.Colors.Transparent);

            float scaleRatio = Math.Min(w / bitmap.SizeInPixels.Width,
                                        h / bitmap.SizeInPixels.Height);
            var interp = scaleRatio < 0.5f
                ? CanvasImageInterpolation.HighQualityCubic
                : CanvasImageInterpolation.Linear;

            using var scaleEff = new ScaleEffect
            {
                Source = bitmap,
                Scale = new Vector2(w / bitmap.SizeInPixels.Width,
                                    h / bitmap.SizeInPixels.Height),
                InterpolationMode = interp
            };
            using var masked = new AlphaMaskEffect { Source = scaleEff, AlphaMask = maskRT };

            if (shadow)
            {
                using var shadowFx = new ShadowEffect
                {
                    Source = masked,
                    BlurAmount = 10f,
                    ShadowColor = Windows.UI.Color.FromArgb(100, 0, 0, 0)
                };
                using var shadowOff = new Transform2DEffect
                {
                    Source = shadowFx,
                    TransformMatrix = Matrix3x2.CreateTranslation(2f, 3f)
                };
                using var comp = new CompositeEffect();
                comp.Sources.Add(shadowOff);
                comp.Sources.Add(masked);
                rtDs.DrawImage(comp, pad, pad);
            }
            else
            {
                rtDs.DrawImage(masked, pad, pad);
            }

            return new BakedRT(rt, pad,
                bitmap.SizeInPixels.Width, bitmap.SizeInPixels.Height);
        }

        // ── 加载 ──────────────────────────────────────────────────────────────

        public async Task LoadBitmapAsync(byte[]? imageBytes,
                                  ICanvasResourceCreator? resourceCreator = null)
        {
            if (imageBytes is not { Length: > 0 })
            {
                await LoadDefaultCoverAsync(resourceCreator);
                return;
            }

            _loadCts?.Cancel();
            var cts = new CancellationTokenSource();
            _loadCts = cts;

            try
            {
                await Task.Delay(80, cts.Token); // 防抖保留

                // ── 替换原来整个 Task.Run 块 ──────────────────────────────────
                using (PerformanceTracker.Measure("A1_decode_Task.Run"))
                {
                    var result = await DecodeImageAsync(imageBytes, cts.Token);
                    if (result == null) return; // 被新请求抢占，静默退出

                    cts.Token.ThrowIfCancellationRequested();

                    CanvasBitmap bmp;
                    using (PerformanceTracker.Measure("A2_CreateFromBytes"))
                    {
                        ICanvasResourceCreator creator = resourceCreator ?? _canvas!;
                        bmp = CanvasBitmap.CreateFromBytes(
                            creator,
                            result.Value.pixels,
                            (int)result.Value.w,
                            (int)result.Value.h,
                            Windows.Graphics.DirectX.DirectXPixelFormat.R8G8B8A8UIntNormalized);
                    }

                    EnqueueDecoded(bmp);
                }
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

        /// <summary>
        /// 替换原来的 Task.Run 解码块。
        /// 保证同时只有 1 个解码在跑，新调用会取消上一个（但不会堆积）。
        /// </summary>
        private async Task<(byte[] pixels, uint w, uint h)?> DecodeImageAsync(
            byte[] imageBytes, CancellationToken outerToken)
        {
            // 取消上一次解码
            var oldCts = Interlocked.Exchange(ref _decodeCts,
                             new CancellationTokenSource());
            oldCts?.Cancel();
            oldCts?.Dispose();

            var cts = _decodeCts!;
            // 合并外部 cancel（控件卸载）和内部 cancel（被新请求抢占）
            using var linked = CancellationTokenSource
                                   .CreateLinkedTokenSource(outerToken, cts.Token);
            var token = linked.Token;

            // 等待前一个解码释放（最多等一个解码周期）
            // 用 TryWait 而非 Wait，避免在高频切换时阻塞调用方
            bool gotSlot = await _decodeSemaphore.WaitAsync(2000, token)
                               .ConfigureAwait(false);
            if (!gotSlot) return null;

            try
            {
                token.ThrowIfCancellationRequested();

                return await Task.Run(async () =>
                {
                    using var mem = new MemoryStream(imageBytes, writable: false);
                    using var ras = mem.AsRandomAccessStream();
                    var decoder = await BitmapDecoder.CreateAsync(ras);

                    uint srcW = decoder.PixelWidth, srcH = decoder.PixelHeight;
                    float sc = Math.Min(1f,
                        Math.Min(HardMaxSize / srcW, HardMaxSize / srcH));
                    uint dstW = Math.Max(1, (uint)(srcW * sc));
                    uint dstH = Math.Max(1, (uint)(srcH * sc));

                    // BitmapDecoder 内部 IO 完成后再检查取消
                    token.ThrowIfCancellationRequested();

                    var pd = await decoder.GetPixelDataAsync(
                        BitmapPixelFormat.Rgba8, BitmapAlphaMode.Premultiplied,
                        new BitmapTransform
                        {
                            ScaledWidth = dstW,
                            ScaledHeight = dstH,
                            InterpolationMode = BitmapInterpolationMode.Fant
                        },
                        ExifOrientationMode.RespectExifOrientation,
                        ColorManagementMode.DoNotColorManage);

                    token.ThrowIfCancellationRequested();
                    return (pd.DetachPixelData(), dstW, dstH);

                }, token).ConfigureAwait(false);
            }
            finally
            {
                _decodeSemaphore.Release();
            }
        }

        public async Task LoadDefaultCoverAsync(ICanvasResourceCreator? resourceCreator = null)
        {
            try
            {
                string fileName = IsDark ? "default_cover_black.png" : "default_cover_white.png";
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", fileName);
                var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
                using var stream = await file.OpenReadAsync();
                ICanvasResourceCreator creator = resourceCreator ?? _canvas!;
                var bmp = await CanvasBitmap.LoadAsync(creator, stream);
                EnqueueDecoded(bmp);
            }
            catch { }
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
            _lastLength = newBytes.Length; _lastHash = hash;
            return false;
        }

        public void InvalidateDedup() { _lastLength = -1; _lastHash = 0; }

        // ── 时钟 ──────────────────────────────────────────────────────────────

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

        // ── 释放队列 ──────────────────────────────────────────────────────────

        private void FlushDisposeQueue()
        {
            while (_disposeQueue.TryDequeue(out var item))
            {
                switch (item)
                {
                    case BakedRT b:
                        if (b != _currentRT && b != _nextRT) b.Dispose();
                        break;
                    case CanvasBitmap bmp:
                        if (bmp != _currentBitmap && bmp != _pendingBitmap && bmp != _queuedBitmap)
                            bmp.Dispose();
                        break;
                }
            }
        }

        // ── Dispose ───────────────────────────────────────────────────────────

        public void Dispose() { Dispose(true); GC.SuppressFinalize(this); }

        private void Dispose(bool disposing)
        {
            if (!disposing || _disposed) return;
            _disposed = true;

            _loadCts?.Cancel(); _loadCts?.Dispose(); _loadCts = null;
            StopRenderingLoop();

            if (_canvas != null)
            {
                _canvas.CreateResources -= Canvas_CreateResources;
                _canvas.Draw -= Canvas_Draw;
                _canvas = null;
            }

            _maskRT?.Dispose(); _maskRT = null;
            _currentRT?.Dispose(); _currentRT = null;
            _nextRT?.Dispose(); _nextRT = null;
            _currentBitmap?.Dispose(); _currentBitmap = null;
            _pendingBitmap?.Dispose(); _pendingBitmap = null;
            _queuedBitmap?.Dispose(); _queuedBitmap = null;

            while (_disposeQueue.TryDequeue(out var item))
            {
                if (item is BakedRT b) b.Dispose();
                else if (item is CanvasBitmap bmp) bmp.Dispose();
            }
        }
        private static Rect CalcDestRect(CanvasBitmap bmp, Rect contentRect)
        {
            float cw = (float)contentRect.Width;
            float ch = (float)contentRect.Height;
            float imgW = bmp.SizeInPixels.Width;
            float imgH = bmp.SizeInPixels.Height;
            if (imgW <= 0 || imgH <= 0 || cw <= 0 || ch <= 0)
                return contentRect;

            float aspect = imgW / imgH;
            float drawW, drawH;
            if (aspect >= cw / ch) { drawW = cw; drawH = drawW / aspect; }
            else { drawH = ch; drawW = drawH * aspect; }

            return new Rect(
                contentRect.X + (cw - drawW) * 0.5f,
                contentRect.Y + (ch - drawH) * 0.5f,
                drawW, drawH);
        }
    }

    internal sealed class BakedRT : IDisposable
    {
        public readonly CanvasRenderTarget RT;
        public readonly float Pad;
        public readonly float SrcW; // bitmap 原始像素宽
        public readonly float SrcH; // bitmap 原始像素高
        public BakedRT(CanvasRenderTarget rt, float pad, float srcW, float srcH)
        { RT = rt; Pad = pad; SrcW = srcW; SrcH = srcH; }
        public void Dispose() => RT.Dispose();
    }
}