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

namespace AnimatedWin2dControls.Controls
{
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

        private CanvasControl? _canvas;

        private CanvasBitmap? _currentBitmap;
        private CanvasBitmap? _incomingBitmap;

        // ── 统一过渡进度 t ∈ [0, 1] ──────────────────────────────────────────
        // t=0：完全显示 current，t=1：完全显示 incoming
        // alpha 和 scale 都由同一个 t 驱动，确保同步
        private float _transitionT = 0f;
        private const float FadeSpeed = 1.5f;

        // 记录过渡开始时 current 的实际绘制矩形，用于 scale 插值
        private Rect _currentDestRectAtStart;
        // incoming 的目标矩形，在 StartTransition 时计算
        private Rect _incomingTargetRect;
        // 标记 _incomingTargetRect 是否已计算（需要 canvas 尺寸）
        private bool _targetRectDirty = true;

        private bool _isFading = false;

        private CanvasBitmap? _queuedBitmap;
        private readonly Queue<CanvasBitmap> _disposeQueue = new();

        private CancellationTokenSource? _loadCts;
        private bool _isResourcesCreated = false;
        private bool _disposed = false;

        private long _lastLength = -1;
        private int _lastHash;
        private const float HardMaxSize = 1536f;

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
            DefaultStyleKey = typeof(AlbumArtControl);
            Unloaded += (_, _) => Dispose(true);
        }

        // ── 模板应用 ──────────────────────────────────────────────────────────

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

            StartRenderingLoop();
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
                _incomingBitmap = null;
            }

            _currentDestRectAtStart = GetCurrentDestRect();
            _incomingBitmap = newBitmap;
            _incomingBaked = null;
            _transitionT = 0f;
            _targetRectDirty = true;
            _isFading = true;

            _ = TryPreBakeIncomingAsync(newBitmap); // fire-and-forget，不阻塞
        }

        private async Task TryPreBakeIncomingAsync(CanvasBitmap bitmap)
        {
            if (_canvas == null) return;
            float cw = (float)_canvas.Size.Width;
            float ch = (float)_canvas.Size.Height;
            if (cw <= 0 || ch <= 0) return;

            float contentW = cw - (float)MarginLeftRatio - (float)MarginRightRatio;
            float contentH = ch - (float)MarginTopRatio - (float)MarginBottomRatio;
            if (contentW <= 0 || contentH <= 0) return;

            var targetRect = CalcDestRect(bitmap,
                (float)MarginLeftRatio, (float)MarginTopRatio, contentW, contentH);
            float bakeW = (float)targetRect.Width;
            float bakeH = (float)targetRect.Height;
            if (bakeW <= 0 || bakeH <= 0) return;

            // mask 必须在 UI 线程上准备好再传给后台线程
            EnsureMaskRenderTarget(_canvas.Device, bakeW, bakeH, (float)ArtCornerRadius);

            await PreBakeIncomingAsync(bitmap, bakeW, bakeH);
        }

        private async Task PreBakeIncomingAsync(CanvasBitmap bitmap, float bakeW, float bakeH)
        {
            if (_canvas == null) return;
            var device = _canvas.Device;

            // 快照当前参数（避免闭包捕获 this 上的可变字段）
            bool shadow = IsShadowEnabled;
            float dpiScale = (float)DpiScale;
            float radius = (float)ArtCornerRadius;
            var maskRT = _maskRT; // 此时 mask 已在调用方 EnsureMask 后就绪

            BakedRT? baked = null;
            try
            {
                baked = await Task.Run(() =>
                    BakeRenderTargetCore(device, bitmap, bakeW, bakeH,
                                         maskRT!, shadow, dpiScale));
            }
            catch { return; }

            // 回到 UI 线程再赋值
            if (_incomingBitmap == bitmap) // 确认没被新的过渡取代
                _incomingBaked = baked;
            else
                baked?.Dispose(); // 已过时，丢弃
        }

        /// <summary>
        /// 返回 _currentBitmap 在当前 canvas 尺寸下的目标矩形；
        /// canvas 未就绪时退化为零矩形。
        /// </summary>
        private Rect GetCurrentDestRect()
        {
            if (_currentBitmap == null || _canvas == null) return Rect.Empty;
            float cw = (float)_canvas.Size.Width;
            float ch = (float)_canvas.Size.Height;
            if (cw <= 0 || ch <= 0) return Rect.Empty;

            float padL = (float)MarginLeftRatio;
            float padR = (float)MarginRightRatio;
            float padT = (float)MarginTopRatio;
            float padB = (float)MarginBottomRatio;
            float contentW = cw - padL - padR;
            float contentH = ch - padT - padB;
            if (contentW <= 0 || contentH <= 0) return Rect.Empty;

            return CalcDestRect(_currentBitmap, padL, padT, contentW, contentH);
        }

        private void UpdateFadeState(float delta)
        {
            FlushDisposeQueue();
            if (!_isFading) return;

            _transitionT = Math.Min(1f, _transitionT + delta * FadeSpeed);

            if (_transitionT >= 1f)
            {
                // 过渡完成：incoming 变成 current
                if (_currentBitmap != null) _disposeQueue.Enqueue(_currentBitmap);
                _currentBaked?.Dispose();
                _currentBaked = _incomingBaked;
                _incomingBaked = null;
                _currentBitmap = _incomingBitmap;
                _incomingBitmap = null;
                _transitionT = 0f;
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

        // ── 缓动函数 ─────────────────────────────────────────────────────────

        /// <summary>Cubic ease-out：快进慢出，自然减速感。</summary>
        private static float EaseOutN(float t, float n)
        {
            float f = 1f - t;
            return 1f - MathF.Pow(f, n);
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
            if (!_isFading && _queuedBitmap == null) return;

            float delta = Math.Min((float)elapsed.TotalSeconds, 0.1f);
            UpdateFadeState(delta);
            _canvas?.Invalidate();

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

            Rect incomingTarget = (_incomingBitmap != null)
                ? CalcDestRect(_incomingBitmap, contentX, contentY, contentW, contentH)
                : CalcDestRect(refBmp, contentX, contentY, contentW, contentH);

            if (_isFading && _targetRectDirty)
            {
                _incomingTargetRect = incomingTarget;
                if (_currentDestRectAtStart == Rect.Empty)
                    _currentDestRectAtStart = incomingTarget;
                _targetRectDirty = false;
            }

            float easedT = _isFading ? EaseOutN(_transitionT, 8f) : (_currentBitmap != null ? 1f : 0f);
            float linearT = _transitionT;

            Rect currentDrawRect = _isFading
                ? LerpRect(_currentDestRectAtStart, _incomingTargetRect, easedT)
                : incomingTarget;

            Rect incomingDrawRect = currentDrawRect;

            float bakeW = (float)incomingTarget.Width;
            float bakeH = (float)incomingTarget.Height;
            float radius = (float)ArtCornerRadius;
            if (bakeW <= 0 || bakeH <= 0) return;

            // mask 仍需在 UI 线程同步维护
            EnsureMaskRenderTarget(ds.Device, bakeW, bakeH, radius);

            if (MathF.Abs(_lastBakeSize.w - bakeW) > 0.5f ||
                MathF.Abs(_lastBakeSize.h - bakeH) > 0.5f)
                _rtInvalidated = true;

            if (_rtInvalidated)
            {
                _currentBaked?.Dispose(); _currentBaked = null;
                _incomingBaked?.Dispose(); _incomingBaked = null;
                _rtInvalidated = false;
                _lastBakeSize = (bakeW, bakeH);
                // 尺寸变化时重新触发异步烘焙
                if (_currentBitmap != null) _ = ReBakeCurrentAsync(_currentBitmap, bakeW, bakeH);
                if (_incomingBitmap != null) _ = ReBakeIncomingAsync(_incomingBitmap, bakeW, bakeH);
            }

            // ── 不再有任何同步 BakeRenderTarget 调用 ────────────────────────────
            // _currentBaked / _incomingBaked 由异步路径填充，未就绪时静默跳过

            // ── 绘制 current ────────────────────────────────────────────────────
            float currentAlpha = _isFading ? Math.Max(0f, 1f - linearT) : 1f;
            if (_currentBaked != null)
            {
                if (_isFading && currentAlpha > 0f)
                {
                    DrawBaked(ds, _currentBaked, currentDrawRect, currentAlpha);
                }
                else if (!_isFading)
                {
                    Rect staticRect = CalcDestRect(_currentBitmap!, contentX, contentY, contentW, contentH);
                    DrawBaked(ds, _currentBaked, staticRect, 1f);
                }
            }

            // ── 绘制 incoming ───────────────────────────────────────────────────
            float incomingAlpha = _isFading ? Math.Min(1f, linearT) : 0f;
            if (_incomingBaked != null && incomingAlpha > 0f)
            {
                DrawBaked(ds, _incomingBaked, incomingDrawRect, incomingAlpha);
            }
        }

        private async Task ReBakeCurrentAsync(CanvasBitmap bitmap, float bakeW, float bakeH)
        {
            if (_canvas == null) return;
            var device = _canvas.Device;
            bool shadow = IsShadowEnabled;
            float dpi = (float)DpiScale;
            float radius = (float)ArtCornerRadius;
            var maskRT = _maskRT;
            if (maskRT == null) return;

            BakedRT? baked = null;
            try { baked = await Task.Run(() => BakeRenderTargetCore(device, bitmap, bakeW, bakeH, maskRT, shadow, dpi)); }
            catch { return; }

            if (_currentBitmap == bitmap) _currentBaked = baked;
            else baked?.Dispose();
            _canvas?.Invalidate();
        }

        private async Task ReBakeIncomingAsync(CanvasBitmap bitmap, float bakeW, float bakeH)
        {
            if (_canvas == null) return;
            var device = _canvas.Device;
            bool shadow = IsShadowEnabled;
            float dpi = (float)DpiScale;
            float radius = (float)ArtCornerRadius;
            var maskRT = _maskRT;
            if (maskRT == null) return;

            BakedRT? baked = null;
            try { baked = await Task.Run(() => BakeRenderTargetCore(device, bitmap, bakeW, bakeH, maskRT, shadow, dpi)); }
            catch { return; }

            if (_incomingBitmap == bitmap) _incomingBaked = baked;
            else baked?.Dispose();
            _canvas?.Invalidate();
        }

        /// <summary>
        /// 将 BakedRT 按 <paramref name="destRect"/> 指定的区域绘制，支持缩放。
        /// BakedRT 内含 pad（用于阴影），destRect 是图像内容区域（不含 pad）。
        /// </summary>
        private static void DrawBaked(CanvasDrawingSession ds,
                                      BakedRT baked,
                                      Rect destRect,
                                      float alpha)
        {
            if (destRect.Width <= 0 || destRect.Height <= 0) return;

            // 目标区域要扩展 pad 以包含阴影
            var destWithPad = new Rect(
                destRect.X - baked.Pad,
                destRect.Y - baked.Pad,
                destRect.Width + baked.Pad * 2,
                destRect.Height + baked.Pad * 2);

            ds.DrawImage(baked.RT, destWithPad, baked.RT.Bounds, alpha);
        }

        // ── 矩形线性插值 ──────────────────────────────────────────────────────

        private static Rect LerpRect(Rect a, Rect b, float t)
        {
            return new Rect(
                a.X + (b.X - a.X) * t,
                a.Y + (b.Y - a.Y) * t,
                a.Width + (b.Width - a.Width) * t,
                a.Height + (b.Height - a.Height) * t);
        }

        // ── RenderTarget 烘焙 ─────────────────────────────────────────────────

        private static BakedRT BakeRenderTargetCore(
            CanvasDevice device,
            CanvasBitmap bitmap,
            float w, float h,
            CanvasRenderTarget maskRT,
            bool shadow, float dpiScale)
        {
            float pad = shadow ? 10f * 3f + 4f : 0f;
            float rtW = w + pad * 2f;
            float rtH = h + pad * 2f;
            float dpi = 96f * dpiScale;
            var rt = new CanvasRenderTarget(device, rtW, rtH, dpi);

            using var rtDs = rt.CreateDrawingSession();
            rtDs.Clear(Microsoft.UI.Colors.Transparent);

            float scaleRatio = Math.Min(w / bitmap.SizeInPixels.Width,
                                        h / bitmap.SizeInPixels.Height);
            var interpolation = scaleRatio < 0.5f
                ? CanvasImageInterpolation.HighQualityCubic
                : CanvasImageInterpolation.Linear;

            using var scale = new ScaleEffect
            {
                Source = bitmap,
                Scale = new Vector2(w / bitmap.SizeInPixels.Width,
                                    h / bitmap.SizeInPixels.Height),
                InterpolationMode = interpolation
            };
            using var masked = new AlphaMaskEffect
            {
                Source = scale,
                AlphaMask = maskRT
            };

            if (shadow)
            {
                using var shadowFx = new ShadowEffect
                {
                    Source = masked,
                    BlurAmount = 10f,
                    ShadowColor = Windows.UI.Color.FromArgb(100, 0, 0, 0)
                };
                using var shadowOffset = new Transform2DEffect
                {
                    Source = shadowFx,
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

        private static Rect CalcDestRect(
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

            StopRenderingLoop();

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