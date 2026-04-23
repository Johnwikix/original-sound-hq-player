using AnimatedWin2dControls.Utils;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.IO;
using System.Numerics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Graphics.Imaging;

namespace AnimatedWin2dControls.Controls.AlbumImgControl
{
    [TemplatePart(Name = PartCanvas, Type = typeof(CanvasControl))]
    public sealed class AlbumArtControl : Control, IDisposable
    {
        private const string PartCanvas = "canvas";
        private const float Margin = 20f;
        private const float CornerRadius = 16f;

        // ── 依赖属性 ──────────────────────────────────────────────────────────

        public static readonly DependencyProperty DpiScaleProperty =
            DependencyProperty.Register(nameof(DpiScale), typeof(double),
                typeof(AlbumArtControl), new PropertyMetadata(1.0, OnDpiScaleChanged));
        public double DpiScale
        {
            get => (double)GetValue(DpiScaleProperty);
            set => SetValue(DpiScaleProperty, value);
        }

        public static readonly DependencyProperty ImageBytesProperty =
            DependencyProperty.Register(nameof(ImageBytes), typeof(byte[]),
                typeof(AlbumArtControl), new PropertyMetadata(null, OnImageBytesChanged));
        public byte[] ImageBytes
        {
            get => (byte[])GetValue(ImageBytesProperty);
            set => SetValue(ImageBytesProperty, value);
        }

        public static readonly DependencyProperty IsDarkProperty =
            DependencyProperty.Register(nameof(IsDark), typeof(bool),
                typeof(AlbumArtControl), new PropertyMetadata(true, OnIsDarkChanged));
        public bool IsDark
        {
            get => (bool)GetValue(IsDarkProperty);
            set => SetValue(IsDarkProperty, value);
        }

        public static readonly DependencyProperty IsShadowEnabledProperty =
            DependencyProperty.Register(nameof(IsShadowEnabled), typeof(bool),
                typeof(AlbumArtControl), new PropertyMetadata(true, OnShadowEnabledChanged));
        public bool IsShadowEnabled
        {
            get => (bool)GetValue(IsShadowEnabledProperty);
            set => SetValue(IsShadowEnabledProperty, value);
        }

        public static readonly DependencyProperty IsActiveProperty =
            DependencyProperty.Register(nameof(IsActive), typeof(bool),
                typeof(AlbumArtControl), new PropertyMetadata(false, OnIsActiveChanged));
        public bool IsActive
        {
            get => (bool)GetValue(IsActiveProperty);
            set => SetValue(IsActiveProperty, value);
        }

        // ── 依赖属性回调 ──────────────────────────────────────────────────────

        private static void OnImageBytesChanged(DependencyObject d,
            DependencyPropertyChangedEventArgs e)
        {
            var c = (AlbumArtControl)d;
            if (!c._isResourcesCreated || !c.IsActive) return;
            c.RequestLoad(e.NewValue as byte[]);
        }

        private static void OnIsDarkChanged(DependencyObject d,
            DependencyPropertyChangedEventArgs e)
        {
            var c = (AlbumArtControl)d;
            if (!c._isResourcesCreated || !c.IsActive) return;
            c._lastLength = -1; c._lastHash = 0;
            if (c.ImageBytes is { Length: > 0 } b) c.RequestLoad(b);
            else c.RequestLoad(null);
        }

        private static void OnDpiScaleChanged(DependencyObject d,
            DependencyPropertyChangedEventArgs e)
        {
            var c = (AlbumArtControl)d;
            if (!c._isResourcesCreated) return;
            if (c.ImageBytes is { Length: > 0 } b) c.RequestLoad(b, isResize: true);
            else c.RequestLoad(null, isResize: true);
        }

        private static void OnShadowEnabledChanged(DependencyObject d,
            DependencyPropertyChangedEventArgs e)
        {
            var c = (AlbumArtControl)d;
            if (!c._isResourcesCreated) return;
            if (c.ImageBytes is { Length: > 0 } b) c.RequestLoad(b, isResize: true);
            else c.RequestLoad(null, isResize: true);
        }

        private static void OnIsActiveChanged(DependencyObject d,
                DependencyPropertyChangedEventArgs e)
        {
            var c = (AlbumArtControl)d;
            if (!c._isResourcesCreated) return;
            if ((bool)e.NewValue)
            {
                if (c.ImageBytes is { Length: > 0 } b) c.RequestLoad(b);
                else c.RequestLoad(null);
            }
            else
            {
                // 停用时：清空所有待处理状态，全部在 UI 线程执行，无跨线程风险
                Interlocked.Exchange(ref c._pendingLoad, null);
                c.CancelPostAnimTimer();
                c._isFading = false;
                c._animLock = false;
                c._pendingBytesAfterAnim = null;
                c._lastDrawTicks = 0;
                c._canvas?.Invalidate();
            }
        }

        // ── Pipeline 数据结构 ─────────────────────────────────────────────────

        private readonly record struct RawFrame(
            byte[] Pixels, int W, int H, int SrcW, int SrcH);

        private readonly record struct BakedFrame(
            byte[] Pixels, int W, int H,
            float Pad, float SrcW, float SrcH,
            bool IsResize);

        private sealed class BakeParams
        {
            public readonly float ContentW, ContentH;
            public readonly bool Shadow;
            public readonly float DpiScale;
            public readonly bool IsResize;
            public BakeParams(float cw, float ch, bool s, float d, bool resize)
            { ContentW = cw; ContentH = ch; Shadow = s; DpiScale = d; IsResize = resize; }
        }

        private sealed record PendingLoad(byte[]? Bytes, bool IsResize);

        // ── Pipeline 管道 ─────────────────────────────────────────────────────

        private readonly Channel<RawFrameWithParams> _rawChannel;
        private readonly record struct RawFrameWithParams(RawFrame Raw, BakeParams Params);
        private readonly Channel<BakedFrame> _bakedChannel;

        // ── 渲染状态（仅 UI 线程访问） ────────────────────────────────────────

        private CanvasControl? _canvas;
        private bool _isResourcesCreated;
        private bool _disposed;

        private CanvasBitmap? _currentBmp;
        private CanvasBitmap? _nextBmp;

        private float _srcWCur, _srcHCur;
        private float _srcWNext, _srcHNext;
        private float _padCur, _padNext;

        // Scale-fade 动画
        private float _t;
        private bool _isFading;
        private const float FadeSpeed = 4f;
        private const float ScaleSmall = 0.90f;

        private Rect _contentRect;

        // ── 动画锁 & 冷却（全部仅 UI 线程访问，无需 Interlocked） ────────────
        //
        // 设计原则：
        //   _animLock 在 RequestLoad 入口拦截新的 bytes 请求。
        //   它在 BeginTransition() 置 true，在动画结束后的 DispatcherQueueTimer 回调置 false。
        //   所有读写均在 UI 线程（Canvas_Draw、RequestLoad、Timer 回调），无跨线程竞态。
        //
        //   _pendingBytesAfterAnim 保存锁期间最后一次收到的 bytes，
        //   动画结束冷却后检查其 hash 与当前显示图是否不同，不同才触发下一次过渡。
        //
        //   _currentDisplayHash 记录当前屏幕上图片的 hash，用于上述对比。
        //   它在动画切换完成（_t >= 1f）时更新。

        private bool _animLock;
        private byte[]? _pendingBytesAfterAnim;
        private int _currentDisplayHash;

        // 冷却 timer：纯 UI 线程，不持有后台 Task，Dispose 时直接 Stop，无悬挂引用
        private DispatcherQueueTimer? _postAnimTimer;
        private const int PostAnimDelayMs = 400;

        // ── Pipeline 控制 ─────────────────────────────────────────────────────

        private CancellationTokenSource _pipelineCts = new();
        private PendingLoad? _pendingLoad;
        private readonly SemaphoreSlim _decodeSignal = new(0, 1);
        private BakeParams? _pendingBakeParams;

        private bool _hasEverLoaded;
        private long _lastLength = -1;
        private int _lastHash;
        private int _debounceSeq;
        private const float HardMaxSize = 1280f;
        private long _lastDrawTicks = 0;

        // ── 构造 ──────────────────────────────────────────────────────────────

        public AlbumArtControl()
        {
            DefaultStyleKey = typeof(AlbumArtControl);

            _rawChannel = Channel.CreateBounded<RawFrameWithParams>(
                new BoundedChannelOptions(1)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = true,
                });
            _bakedChannel = Channel.CreateBounded<BakedFrame>(
                new BoundedChannelOptions(1)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = true,
                });

            Unloaded += (_, _) => Dispose(true);
        }

        protected override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            if (_canvas != null)
            {
                _canvas.CreateResources -= Canvas_CreateResources;
                _canvas.Draw -= Canvas_Draw;
                _canvas.SizeChanged -= Canvas_SizeChanged;
                _canvas = null;
            }
            _canvas = GetTemplateChild(PartCanvas) as CanvasControl;
            if (_canvas == null) return;
            _canvas.CreateResources += Canvas_CreateResources;
            _canvas.Draw += Canvas_Draw;
            _canvas.SizeChanged += Canvas_SizeChanged;
        }

        // ── Canvas 事件 ───────────────────────────────────────────────────────

        private void Canvas_CreateResources(CanvasControl sender,
            Microsoft.Graphics.Canvas.UI.CanvasCreateResourcesEventArgs e)
        {
            _isResourcesCreated = true;
            StartPipelineLoops();
            if (!IsActive) return;
            if (ImageBytes is { Length: > 0 } b) RequestLoad(b);
            else RequestLoad(null);
        }

        // 画布尺寸变化（窗口拖拽缩放）：防抖后触发 isResize 重绘，保持像素清晰
        private int _resizeSeq;
        private void Canvas_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!_isResourcesCreated || !IsActive) return;
            int seq = Interlocked.Increment(ref _resizeSeq);
            _ = Task.Run(async () =>
            {
                await Task.Delay(200).ConfigureAwait(false);
                if (Interlocked.CompareExchange(ref _resizeSeq, seq, seq) != seq) return;
                _canvas?.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, () =>
                {
                    if (_disposed || !IsActive) return;
                    if (ImageBytes is { Length: > 0 } b) RequestLoad(b, isResize: true);
                    else RequestLoad(null, isResize: true);
                });
            });
        }

        private void Canvas_Draw(CanvasControl sender, CanvasDrawEventArgs e)
        {
            // Stage 3：尝试从 raw channel 拿到解码帧，GpuBake 后应用
            ConsumeBakedChannel(sender);

            // 推进动画状态（自驱动：Draw 内部计算 delta，消除帧错位）
            if (_isFading)
            {
                long nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                float delta = 0f;

                if (_lastDrawTicks != 0)
                {
                    long elapsed = nowTicks - _lastDrawTicks;
                    delta = (float)((double)elapsed / System.Diagnostics.Stopwatch.Frequency);
                    delta = Math.Min(delta, 0.1f);
                }
                _lastDrawTicks = nowTicks;

                _t = Math.Min(1f, _t + delta * FadeSpeed);

                if (_t >= 0.5f && _nextBmp != null)
                {
                    _currentBmp?.Dispose();
                    _currentBmp = _nextBmp;
                    _nextBmp = null;
                    _srcWCur = _srcWNext;
                    _srcHCur = _srcHNext;
                    _padCur = _padNext;
                }

                if (_t >= 1f)
                {
                    _t = 0f;
                    _isFading = false;
                    _lastDrawTicks = 0;

                    // 动画播完：记录当前显示 hash，启动纯 UI 线程冷却 timer
                    _currentDisplayHash = _lastHash;
                    StartPostAnimTimer();
                }
            }

            DrawFrame(e.DrawingSession, sender);

            if (_isFading)
                sender.Invalidate();
        }

        // ── Stage 3：消费 Raw Channel ─────────────────────────────────────────

        private void ConsumeBakedChannel(CanvasControl sender)
        {
            if (!_rawChannel.Reader.TryRead(out var item)) return;

            var p = item.Params;
            float cw = p.ContentW, ch = p.ContentH;
            if (cw <= 0 || ch <= 0)
            {
                ComputeContentRect((float)sender.Size.Width, (float)sender.Size.Height);
                cw = (float)_contentRect.Width;
                ch = (float)_contentRect.Height;
            }

            if (cw > 0 && ch > 0)
            {
                var bmp = GpuBake(item.Raw, cw, ch, p.Shadow, p.DpiScale, p.IsResize, sender);
                if (bmp != null)
                    ApplyNewBitmap(bmp, item.Raw.SrcW, item.Raw.SrcH, p.IsResize);
            }
        }

        private void ApplyNewBitmap(CanvasBitmap bmp, int srcW, int srcH, bool isResize)
        {
            float pad = IsShadowEnabled ? 34f : 0f;

            if (isResize)
            {
                _nextBmp?.Dispose();
                _nextBmp = null;
                _currentBmp?.Dispose();
                _currentBmp = bmp;
                _srcWCur = srcW; _srcHCur = srcH;
                _padCur = pad;
                _isFading = false;
                _lastDrawTicks = 0;
                _canvas?.Invalidate();
                return;
            }

            // 若有进行中的动画，先快进到终态（保护 _nextBmp 不被 Canvas_Draw 读到已释放对象）
            if (_isFading)
            {
                if (_nextBmp != null)
                {
                    _currentBmp?.Dispose();
                    _currentBmp = _nextBmp;
                    _nextBmp = null;
                    _srcWCur = _srcWNext;
                    _srcHCur = _srcHNext;
                    _padCur = _padNext;
                }
                _isFading = false;
                _t = 0f;
                _lastDrawTicks = 0;
            }

            _nextBmp = bmp;
            _srcWNext = srcW; _srcHNext = srcH;
            _padNext = pad;
            BeginTransition();
        }

        // ── Stage 1：Decode Loop ──────────────────────────────────────────────

        private void StartPipelineLoops()
        {
            _pipelineCts = new CancellationTokenSource();
            var token = _pipelineCts.Token;
            Task.Run(() => DecodeLoopAsync(token), token);
        }

        private async Task DecodeLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try { await _decodeSignal.WaitAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }

                var req = Interlocked.Exchange(ref _pendingLoad, null);
                var bakeP = Interlocked.Exchange(ref _pendingBakeParams, null);
                if (req == null || bakeP == null) continue;

                try
                {
                    RawFrame raw;
                    if (req.Bytes is { Length: > 0 } bytes)
                    {
                        var decoded = await DecodeImageAsync(bytes, ct).ConfigureAwait(false);
                        if (decoded == null) continue;
                        raw = new RawFrame(decoded.Value.Pixels,
                            (int)decoded.Value.W, (int)decoded.Value.H,
                            (int)decoded.Value.W, (int)decoded.Value.H);
                    }
                    else
                    {
                        var decoded = await DecodeDefaultAsync(ct).ConfigureAwait(false);
                        if (decoded == null) continue;
                        raw = decoded.Value;
                    }

                    await _rawChannel.Writer.WriteAsync(
                        new RawFrameWithParams(raw, bakeP), ct).ConfigureAwait(false);

                    // 通知 UI 线程触发 Draw，在 ConsumeBakedChannel 里消费帧
                    var canvas = _canvas;
                    canvas?.DispatcherQueue.TryEnqueue(
                        DispatcherQueuePriority.Normal,
                        () => _canvas?.Invalidate());
                }
                catch (OperationCanceledException) { break; }
                catch { }
            }
        }

        // ── GpuBake ───────────────────────────────────────────────────────────

        private CanvasBitmap? GpuBake(
                RawFrame raw,
                float contentW, float contentH,
                bool shadow, float dpiScale,
                bool isResize,
                CanvasControl sender)
        {
            if (raw.W <= 0 || raw.H <= 0 || contentW <= 0 || contentH <= 0) return null;
            var device = sender.Device;

            using var srcBmp = CanvasBitmap.CreateFromBytes(
                device,
                raw.Pixels,
                raw.W, raw.H,
                Windows.Graphics.DirectX.DirectXPixelFormat.R8G8B8A8UIntNormalized,
                96f * dpiScale,
                CanvasAlphaMode.Premultiplied);

            float aspect = (float)raw.SrcW / raw.SrcH;
            float drawW, drawH;
            if (aspect >= contentW / contentH) { drawW = contentW; drawH = drawW / aspect; }
            else { drawH = contentH; drawW = drawH * aspect; }
            int dstW = Math.Max(1, (int)drawW);
            int dstH = Math.Max(1, (int)drawH);

            float pad = shadow ? 34f : 0f;
            int rtW = dstW + (int)pad * 2;
            int rtH = dstH + (int)pad * 2;

            using var scaledRt = new CanvasRenderTarget(device, dstW, dstH, 96f * dpiScale,
                Windows.Graphics.DirectX.DirectXPixelFormat.R8G8B8A8UIntNormalized,
                CanvasAlphaMode.Premultiplied);

            using (var ds = scaledRt.CreateDrawingSession())
            {
                ds.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0));
                var roundedRect = new Windows.Foundation.Rect(0, 0, dstW, dstH);
                using var geom = CanvasGeometry.CreateRoundedRectangle(device,
                    roundedRect, CornerRadius, CornerRadius);
                using (ds.CreateLayer(1f, geom))
                {
                    ds.DrawImage(srcBmp,
                        new Windows.Foundation.Rect(0, 0, dstW, dstH),
                        new Windows.Foundation.Rect(0, 0, raw.W, raw.H),
                        1f, CanvasImageInterpolation.MultiSampleLinear);
                }
            }

            var finalRt = new CanvasRenderTarget(device, rtW, rtH, 96f * dpiScale,
                Windows.Graphics.DirectX.DirectXPixelFormat.R8G8B8A8UIntNormalized,
                CanvasAlphaMode.Premultiplied);

            using (var ds = finalRt.CreateDrawingSession())
            {
                ds.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0));
                if (shadow)
                {
                    var blurEffect = new GaussianBlurEffect
                    {
                        Source = scaledRt,
                        BlurAmount = 8f,
                        BorderMode = EffectBorderMode.Soft,
                    };
                    var shadowColorEffect = new ColorMatrixEffect
                    {
                        Source = blurEffect,
                        ColorMatrix = new Matrix5x4
                        {
                            M11 = 0,
                            M12 = 0,
                            M13 = 0,
                            M14 = 0,
                            M21 = 0,
                            M22 = 0,
                            M23 = 0,
                            M24 = 0,
                            M31 = 0,
                            M32 = 0,
                            M33 = 0,
                            M34 = 0,
                            M41 = 0,
                            M42 = 0,
                            M43 = 0,
                            M44 = 100f / 255f,
                            M51 = 0,
                            M52 = 0,
                            M53 = 0,
                            M54 = 0
                        }
                    };
                    ds.DrawImage(shadowColorEffect, new Vector2(pad + 2, pad + 3));
                    ds.DrawImage(scaledRt, new Vector2(pad, pad));
                }
                else
                {
                    ds.DrawImage(scaledRt, new Vector2(pad, pad));
                }
            }

            return finalRt;
        }

        // ── 加载请求 ──────────────────────────────────────────────────────────

        /// <summary>
        /// 请求加载新图片。
        ///
        /// 动画锁逻辑：
        ///   _animLock 在 BeginTransition() 置 true，在动画播完后的冷却 Timer 回调里置 false。
        ///   锁期间（非 resize）：只暂存最新 bytes 到 _pendingBytesAfterAnim，
        ///   直接返回，不进入 dedup / decode 流程，彻底杜绝"解码比动画先跑完"的问题。
        ///   冷却结束后：对比 pending 与当前显示 hash，不同才触发下一轮 RequestLoad。
        /// </summary>
        public void RequestLoad(byte[]? bytes, bool isResize = false)
        {
            if (_disposed) return;
            if (!IsActive && _isResourcesCreated) return;

            // 动画锁期间：暂存最新 bytes，其余全部忽略，连解码都不进入
            if (_animLock && !isResize)
            {
                _pendingBytesAfterAnim = bytes;
                return;
            }

            if (bytes is { Length: > 0 } && IsDuplicateAndUpdate(bytes)) return;

            float cw = 0, ch = 0;
            if (_canvas is { } c)
            {
                ComputeContentRect((float)c.Size.Width, (float)c.Size.Height);
                cw = (float)_contentRect.Width;
                ch = (float)_contentRect.Height;
            }

            Interlocked.Exchange(ref _pendingBakeParams,
                new BakeParams(cw, ch, IsShadowEnabled, (float)DpiScale, isResize));
            Interlocked.Exchange(ref _pendingLoad, new PendingLoad(bytes, isResize));

            bool firstLoad = !_hasEverLoaded;
            _hasEverLoaded = true;

            if (firstLoad)
            {
                TrySignalDecode();
            }
            else
            {
                int seq = Interlocked.Increment(ref _debounceSeq);
                _ = Task.Run(async () =>
                {
                    await Task.Delay(200).ConfigureAwait(false);
                    if (Interlocked.CompareExchange(ref _debounceSeq, seq, seq) == seq)
                        TrySignalDecode();
                });
            }
        }

        private void TrySignalDecode()
        {
            if (_decodeSignal.CurrentCount == 0)
                _decodeSignal.Release();
        }

        // ── 动画锁冷却 Timer（纯 UI 线程，无后台 Task） ───────────────────────

        /// <summary>
        /// 启动动画结束后的冷却 timer。
        /// 全部在 DispatcherQueueTimer 上执行，不涉及任何后台线程，
        /// 彻底避免 Task.Run + TryEnqueue 在控件 Dispose 后访问已销毁消息泵的崩溃。
        /// </summary>
        private void StartPostAnimTimer()
        {
            CancelPostAnimTimer();

            // 必须有 DispatcherQueue 才能创建 timer（正常情况下 UI 控件一定有）
            var dq = DispatcherQueue.GetForCurrentThread() ?? _canvas?.DispatcherQueue;
            if (dq is null) { UnlockAndCheckPending(); return; }

            _postAnimTimer = dq.CreateTimer();
            _postAnimTimer.Interval = TimeSpan.FromMilliseconds(PostAnimDelayMs);
            _postAnimTimer.IsRepeating = false;
            _postAnimTimer.Tick += (_, _) =>
            {
                CancelPostAnimTimer();      // 停掉自己
                UnlockAndCheckPending();    // 解锁 + 检查 pending
            };
            _postAnimTimer.Start();
        }

        private void CancelPostAnimTimer()
        {
            if (_postAnimTimer is null) return;
            _postAnimTimer.Stop();
            _postAnimTimer = null;
        }

        /// <summary>
        /// 冷却结束后：解除动画锁，若有 pending bytes 且与当前显示图不同则触发下一轮过渡。
        /// 此方法只在 UI 线程调用（timer tick 或 OnIsActiveChanged）。
        /// </summary>
        private void UnlockAndCheckPending()
        {
            _animLock = false;

            var pending = _pendingBytesAfterAnim;
            _pendingBytesAfterAnim = null;
            if (pending is null) return;

            // 对比 pending 与当前屏幕上图片的 hash，相同则不触发
            int pendingHash = ToolUtils.ComputeFastHash(pending);
            if (pending.Length == _lastLength && pendingHash == _currentDisplayHash) return;

            // 重置 dedup 状态，避免被当作重复帧忽略
            _lastLength = -1;
            _lastHash = 0;
            RequestLoad(pending);
        }

        // ── 图像解码 ──────────────────────────────────────────────────────────

        private async Task<(byte[] Pixels, uint W, uint H)?> DecodeImageAsync(
            byte[] bytes, CancellationToken ct)
        {
            using var mem = new MemoryStream(bytes, writable: false);
            using var ras = mem.AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(ras).AsTask(ct).ConfigureAwait(false);

            uint srcW = decoder.PixelWidth, srcH = decoder.PixelHeight;
            float sc = Math.Min(1f, Math.Min(HardMaxSize / srcW, HardMaxSize / srcH));
            uint dstW = Math.Max(1, (uint)(srcW * sc));
            uint dstH = Math.Max(1, (uint)(srcH * sc));

            ct.ThrowIfCancellationRequested();

            var pd = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Rgba8, BitmapAlphaMode.Premultiplied,
                new BitmapTransform
                {
                    ScaledWidth = dstW,
                    ScaledHeight = dstH,
                    InterpolationMode = BitmapInterpolationMode.Fant
                },
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.DoNotColorManage).AsTask(ct).ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();
            return (pd.DetachPixelData(), dstW, dstH);
        }

        private async Task<RawFrame?> DecodeDefaultAsync(CancellationToken ct)
        {
            try
            {
                string name = IsDark ? "default_cover_black.png" : "default_cover_white.png";
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", name);
                var file = await Windows.Storage.StorageFile
                               .GetFileFromPathAsync(path).AsTask(ct).ConfigureAwait(false);
                using var stream = await file.OpenReadAsync().AsTask(ct).ConfigureAwait(false);

                var ms = new MemoryStream();
                using (var s = stream.AsStream()) await s.CopyToAsync(ms, ct).ConfigureAwait(false);
                var decoded = await DecodeImageAsync(ms.ToArray(), ct).ConfigureAwait(false);
                if (decoded == null) return null;
                return new RawFrame(decoded.Value.Pixels,
                    (int)decoded.Value.W, (int)decoded.Value.H,
                    (int)decoded.Value.W, (int)decoded.Value.H);
            }
            catch { return null; }
        }

        // ── 动画 ──────────────────────────────────────────────────────────────

        private void BeginTransition()
        {
            _t = 0f;
            _isFading = true;
            _animLock = true;       // 加锁：从此刻起直到冷却 timer 触发前，拒绝所有新请求进入解码
            _lastDrawTicks = 0;
            _canvas?.Invalidate();
        }

        private void DrawFrame(CanvasDrawingSession ds, CanvasControl sender)
        {
            if (!IsActive) return;
            float cw = (float)sender.Size.Width;
            float ch = (float)sender.Size.Height;
            if (cw <= 0 || ch <= 0) return;
            ComputeContentRect(cw, ch);
            if (_contentRect == Rect.Empty) return;

            if (!_isFading)
            {
                if (_currentBmp != null)
                    DrawBitmap(ds, _currentBmp, _srcWCur, _srcHCur, _padCur, 1f, 1f);
            }
            else
            {
                float t = _t;
                if (t < 0.5f)
                {
                    float e = EaseIn(t / 0.5f);
                    if (_currentBmp != null)
                        DrawBitmap(ds, _currentBmp, _srcWCur, _srcHCur, _padCur,
                            1f - e, 1f - (1f - ScaleSmall) * e);
                }
                else
                {
                    float e = EaseOut((t - 0.5f) / 0.5f);
                    if (_currentBmp != null)
                        DrawBitmap(ds, _currentBmp, _srcWCur, _srcHCur, _padCur,
                            e, ScaleSmall + (1f - ScaleSmall) * e);
                }
            }
        }

        private void DrawBitmap(CanvasDrawingSession ds, CanvasBitmap bmp,
            float srcW, float srcH, float pad, float alpha, float scale)
        {
            // srcW/srcH 是图片内容尺寸（不含 pad），用来计算内容在 _contentRect 内的位置和大小
            var contentDest = CalcDestRectFromSize(srcW, srcH, _contentRect);
            if (contentDest.Width <= 0 || contentDest.Height <= 0) return;

            // bitmap 本身已经包含 pad（阴影区域），以内容区中心为锚点向外扩展
            double cx = contentDest.X + contentDest.Width * 0.5;
            double cy = contentDest.Y + contentDest.Height * 0.5;
            double bmpW = (contentDest.Width + pad * 2) * scale;
            double bmpH = (contentDest.Height + pad * 2) * scale;
            var dest = new Rect(cx - bmpW * 0.5, cy - bmpH * 0.5, bmpW, bmpH);

            // 用 bmp.Bounds 作为 sourceRect，确保阴影像素被完整采样
            ds.DrawImage(bmp, dest, bmp.Bounds, alpha, CanvasImageInterpolation.Linear);
        }

        // ── 工具函数 ──────────────────────────────────────────────────────────

        private void ComputeContentRect(float cw, float ch)
        {
            float w = cw - Margin * 2;
            float h = ch - Margin * 2;
            _contentRect = w > 0 && h > 0 ? new Rect(Margin, Margin, w, h) : Rect.Empty;
        }

        private static Rect CalcDestRectFromSize(float srcW, float srcH, Rect cr)
        {
            float cw = (float)cr.Width, ch = (float)cr.Height;
            if (srcW <= 0 || srcH <= 0 || cw <= 0 || ch <= 0) return cr;
            float aspect = srcW / srcH;
            float dw, dh;
            if (aspect >= cw / ch) { dw = cw; dh = dw / aspect; }
            else { dh = ch; dw = dh * aspect; }
            return new Rect(cr.X + (cw - dw) * 0.5f, cr.Y + (ch - dh) * 0.5f, dw, dh);
        }

        private static float EaseIn(float t) => t * t;
        private static float EaseOut(float t) => 1f - (1f - t) * (1f - t);

        public bool IsDuplicateAndUpdate(byte[]? b)
        {
            if (b is not { Length: > 0 })
            {
                bool empty = _lastLength == 0;
                _lastLength = 0; _lastHash = 0;
                return empty;
            }
            int hash = ToolUtils.ComputeFastHash(b);
            if (b.Length == _lastLength && hash == _lastHash) return true;
            _lastLength = b.Length; _lastHash = hash;
            return false;
        }

        // ── Dispose ───────────────────────────────────────────────────────────

        public void Dispose() { Dispose(true); GC.SuppressFinalize(this); }

        private void Dispose(bool disposing)
        {
            if (!disposing || _disposed) return;
            _disposed = true;

            _pipelineCts.Cancel();
            _pipelineCts.Dispose();
            _rawChannel.Writer.TryComplete();
            _bakedChannel.Writer.TryComplete();
            _decodeSignal.Dispose();

            // 停掉冷却 timer，避免在已销毁的消息泵上触发回调
            CancelPostAnimTimer();

            _isFading = false;
            _animLock = false;
            _pendingBytesAfterAnim = null;
            _lastDrawTicks = 0;

            if (_canvas != null)
            {
                _canvas.CreateResources -= Canvas_CreateResources;
                _canvas.Draw -= Canvas_Draw;
                _canvas.SizeChanged -= Canvas_SizeChanged;
                _canvas = null;
            }

            _currentBmp?.Dispose();
            _nextBmp?.Dispose();
        }
    }
}