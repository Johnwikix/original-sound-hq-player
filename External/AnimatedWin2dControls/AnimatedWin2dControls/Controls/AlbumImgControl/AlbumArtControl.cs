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
        private const float ShadowPad = 34f;
        private const float HardMaxSize = 1280f;
        private const float FadeSpeed = 4f;
        private const float ScaleSmall = 0.90f;
        private const int ResizeDebounceMs = 200;
        // 动画总时长：t 从 0 跑到 1，FadeSpeed=4f → 1000/4=250ms
        // 留 50ms 余量，确保 CT 到期时动画必然已完成
        private static readonly int AnimLockMs = (int)(1000f * 4 / FadeSpeed);

        // ── 帧描述符 ──────────────────────────────────────────────────────────

        private readonly record struct FrameInfo(float SrcW, float SrcH, float Pad);

        // ── 依赖属性 ──────────────────────────────────────────────────────────

        public static readonly DependencyProperty DpiScaleProperty =
            DependencyProperty.Register(nameof(DpiScale), typeof(double),
                typeof(AlbumArtControl), new PropertyMetadata(1.0, OnResizeTriggerChanged));
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
                typeof(AlbumArtControl), new PropertyMetadata(true, OnResizeTriggerChanged));
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

        private static void OnImageBytesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var c = (AlbumArtControl)d;
            if (!c._isResourcesCreated || !c.IsActive) return;
            c.RequestLoad(e.NewValue as byte[]);
        }

        private static void OnIsDarkChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var c = (AlbumArtControl)d;
            if (!c._isResourcesCreated || !c.IsActive) return;
            // default cover 换了，强制绕过 dedup
            c.InvalidateDedup();
            c.RequestLoad(c.ImageBytes is { Length: > 0 } b ? b : null);
        }

        // DpiScale / IsShadowEnabled 变化需重新 bake，走 isResize 直接替换（不播动画）
        private static void OnResizeTriggerChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var c = (AlbumArtControl)d;
            if (!c._isResourcesCreated) return;
            c.RequestLoad(c.ImageBytes is { Length: > 0 } b ? b : null, isResize: true);
        }

        private static void OnIsActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var c = (AlbumArtControl)d;
            if (!c._isResourcesCreated) return;
            if ((bool)e.NewValue)
            {
                c.RequestLoad(c.ImageBytes is { Length: > 0 } b ? b : null);
            }
            else
            {
                // 停用：清空所有待处理状态，重置动画
                Interlocked.Exchange(ref c._pendingRequest, null);
                c.CancelAnimLock();
                c._resizeCts.Cancel();
                c._animLock = false;
                c._pendingAfterAnim = null;
                c._isFading = false;
                c._t = 0f;
                c._lastDrawTicks = 0;
                c._canvas?.Invalidate();
            }
        }

        // ── Pipeline 数据结构 ─────────────────────────────────────────────────

        private readonly record struct DecodedFrame(byte[] Pixels, int W, int H);

        private sealed record PendingRequest(
            byte[]? Bytes,
            float ContentW, float ContentH,
            bool Shadow, float DpiScale,
            bool IsResize);

        // ── Pipeline 通道 ─────────────────────────────────────────────────────

        private readonly Channel<(DecodedFrame Frame, PendingRequest Req)> _decodeChannel =
            Channel.CreateBounded<(DecodedFrame, PendingRequest)>(
                new BoundedChannelOptions(1)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = true,
                });

        // ── 渲染状态（仅 UI 线程） ────────────────────────────────────────────

        private CanvasControl? _canvas;
        private bool _isResourcesCreated;
        private bool _disposed;

        private CanvasBitmap? _currentBmp;
        private FrameInfo _currentInfo;
        private CanvasBitmap? _nextBmp;
        private FrameInfo _nextInfo;

        private float _t;
        private bool _isFading;
        private long _lastDrawTicks;
        private Rect _contentRect;

        // ── 动画锁（仅 UI 线程读写）──────────────────────────────────────────
        //
        //  【新逻辑】
        //  RequestLoad 收到普通请求：
        //    - _animLock=true 时：只更新 _pendingAfterAnim，完全不进解码流程，直接 return
        //    - _animLock=false 时：通过 dedup 检查后，设 _animLock=true，进入解码
        //
        //  BeginTransition()（UI 线程，解码完成后调用）：
        //    - 启动 _animLockCts，Task.Delay(AnimLockMs) 精确覆盖动画窗口
        //    - CT 到期回 UI 线程执行 UnlockAndCheckPending
        //
        //  UnlockAndCheckPending：
        //    - _animLock = false
        //    - 对比 _pendingAfterAnim 的 hash 与 _currentDisplayHash（屏幕上实际图的 hash）
        //    - 不同则触发下一轮 RequestLoad（此时 _animLock 已解，能正常进入解码）
        //
        //  isResize=true 的请求：绕过 _animLock，直接替换，不播动画，不影响锁状态
        //
        //  _currentDisplayHash：TickAnimation 中 _t≥1（动画结束）时快照 _lastHash，
        //    代表屏幕上实际展示的图片。

        private bool _animLock;
        private byte[]? _pendingAfterAnim;
        private int _currentDisplayHash;
        private CancellationTokenSource _animLockCts = new();

        // resize 防抖 CTS（高频 resize 会触发 GpuBake，防抖有性能意义，保留）
        private CancellationTokenSource _resizeCts = new();

        // ── Pipeline 控制（跨线程字段） ───────────────────────────────────────

        private CancellationTokenSource _pipelineCts = new();
        private PendingRequest? _pendingRequest;  // Interlocked.Exchange 原子替换
        private readonly SemaphoreSlim _decodeSignal = new(0, 1);

        private long _lastLength = -1;
        private int _lastHash;

        // ── 构造 / 模板 ───────────────────────────────────────────────────────

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
            // 设备丢失恢复时可能重复触发，先取消旧 pipeline 再重建
            _pipelineCts.Cancel();
            _pipelineCts.Dispose();
            _pipelineCts = new CancellationTokenSource();

            _isResourcesCreated = true;
            Task.Run(() => DecodeLoopAsync(_pipelineCts.Token));

            if (!IsActive) return;
            RequestLoad(ImageBytes is { Length: > 0 } b ? b : null);
        }

        // resize 防抖保留：高频 resize 每次都需要 GpuBake，防抖有实际性能意义
        private void Canvas_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!_isResourcesCreated || !IsActive) return;

            _resizeCts.Cancel();
            _resizeCts.Dispose();
            _resizeCts = new CancellationTokenSource();
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
                        // isResize=true：绕过 animLock，直接进解码替换，不播动画
                        RequestLoad(ImageBytes is { Length: > 0 } b ? b : null, isResize: true);
                    });
                }
                catch (OperationCanceledException) { }
            });
        }

        private void Canvas_Draw(CanvasControl sender, CanvasDrawEventArgs e)
        {
            // 消费解码通道（GpuBake 必须在能访问 GPU 设备的 UI 线程上执行）
            ConsumeDecodeChannel(sender);

            if (_isFading)
                TickAnimation();

            DrawFrame(e.DrawingSession, sender);

            if (_isFading)
                sender.Invalidate();
        }

        // ── 解码通道消费 ──────────────────────────────────────────────────────

        private void ConsumeDecodeChannel(CanvasControl sender)
        {
            if (!_decodeChannel.Reader.TryRead(out var item)) return;

            var (frame, req) = item;

            float cw = req.ContentW, ch = req.ContentH;
            if (cw <= 0 || ch <= 0)
            {
                ComputeContentRect((float)sender.Size.Width, (float)sender.Size.Height);
                cw = (float)_contentRect.Width;
                ch = (float)_contentRect.Height;
            }
            if (cw <= 0 || ch <= 0) return;

            var bmp = GpuBake(frame, cw, ch, req.Shadow, req.DpiScale, sender);
            if (bmp == null) return;

            float pad = req.Shadow ? ShadowPad : 0f;
            ApplyNewBitmap(bmp, new FrameInfo(frame.W, frame.H, pad), req.IsResize);
        }

        private void ApplyNewBitmap(CanvasBitmap bmp, FrameInfo info, bool isResize)
        {
            if (isResize)
            {
                // resize：立即替换，不播动画，不触发/影响 animLock
                FinishFadeImmediately();
                _currentBmp?.Dispose();
                _currentBmp = bmp;
                _currentInfo = info;
                _canvas?.Invalidate();
                return;
            }

            // 正常图片切换：快进当前动画终态，然后开始新的过渡
            FinishFadeImmediately();
            _nextBmp = bmp;
            _nextInfo = info;
            BeginTransition();
        }

        private void FinishFadeImmediately()
        {
            if (!_isFading) return;
            if (_nextBmp != null)
            {
                _currentBmp?.Dispose();
                _currentBmp = _nextBmp;
                _currentInfo = _nextInfo;
                _nextBmp = null;

                // 关键修复：强行结束动画时，必须同步当前展示的 Hash
                _currentDisplayHash = _lastHash;
            }
            _isFading = false;
            _t = 0f;
            _lastDrawTicks = 0;
        }

        // ── 动画推进 ──────────────────────────────────────────────────────────

        private void TickAnimation()
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            float delta = 0f;
            if (_lastDrawTicks != 0)
            {
                float elapsed = (float)((double)(now - _lastDrawTicks)
                                        / System.Diagnostics.Stopwatch.Frequency);
                delta = Math.Min(elapsed, 0.1f);
            }
            _lastDrawTicks = now;

            _t = Math.Min(1f, _t + delta * FadeSpeed);

            if (_t >= 0.5f && _nextBmp != null)
            {
                _currentBmp?.Dispose();
                _currentBmp = _nextBmp;
                _currentInfo = _nextInfo;
                _nextBmp = null;
            }

            if (_t >= 1f)
            {
                _t = 0f;
                _isFading = false;
                _lastDrawTicks = 0;
                // 动画自然完成：快照此时屏幕上展示的图像 Hash
                _currentDisplayHash = _lastHash;
            }
        }

        // ── 绘制 ──────────────────────────────────────────────────────────────

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
                    DrawBitmap(ds, _currentBmp, _currentInfo, alpha: 1f, scale: 1f);
                return;
            }

            float t = _t;
            if (t < 0.5f)
            {
                float e = EaseIn(t / 0.5f);
                if (_currentBmp != null)
                    DrawBitmap(ds, _currentBmp, _currentInfo,
                        alpha: 1f - e,
                        scale: 1f - (1f - ScaleSmall) * e);
            }
            else
            {
                float e = EaseOut((t - 0.5f) / 0.5f);
                if (_currentBmp != null)
                    DrawBitmap(ds, _currentBmp, _currentInfo,
                        alpha: e,
                        scale: ScaleSmall + (1f - ScaleSmall) * e);
            }
        }

        private void DrawBitmap(CanvasDrawingSession ds, CanvasBitmap bmp,
            FrameInfo info, float alpha, float scale)
        {
            var contentDest = CalcAspectFitRect(info.SrcW, info.SrcH, _contentRect);
            if (contentDest.Width <= 0 || contentDest.Height <= 0) return;

            double cx = contentDest.X + contentDest.Width * 0.5;
            double cy = contentDest.Y + contentDest.Height * 0.5;
            double bmpW = (contentDest.Width + info.Pad * 2) * scale;
            double bmpH = (contentDest.Height + info.Pad * 2) * scale;
            var dest = new Rect(cx - bmpW * 0.5, cy - bmpH * 0.5, bmpW, bmpH);

            ds.DrawImage(bmp, dest, bmp.Bounds, alpha, CanvasImageInterpolation.Linear);
        }

        // ── GPU Bake ──────────────────────────────────────────────────────────

        private static CanvasBitmap? GpuBake(
            DecodedFrame frame,
            float contentW, float contentH,
            bool shadow, float dpiScale,
            CanvasControl sender)
        {
            if (frame.W <= 0 || frame.H <= 0 || contentW <= 0 || contentH <= 0) return null;

            var device = sender.Device;
            float dpi = 96f * dpiScale;

            using var srcBmp = CanvasBitmap.CreateFromBytes(
                device, frame.Pixels, frame.W, frame.H,
                Windows.Graphics.DirectX.DirectXPixelFormat.R8G8B8A8UIntNormalized,
                dpi, CanvasAlphaMode.Premultiplied);

            float aspect = (float)frame.W / frame.H;
            float drawW, drawH;
            if (aspect >= contentW / contentH) { drawW = contentW; drawH = drawW / aspect; }
            else { drawH = contentH; drawW = drawH * aspect; }
            int dstW = Math.Max(1, (int)drawW);
            int dstH = Math.Max(1, (int)drawH);

            using var scaledRt = new CanvasRenderTarget(device, dstW, dstH, dpi,
                Windows.Graphics.DirectX.DirectXPixelFormat.R8G8B8A8UIntNormalized,
                CanvasAlphaMode.Premultiplied);

            using (var ds = scaledRt.CreateDrawingSession())
            {
                ds.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0));
                using var geom = CanvasGeometry.CreateRoundedRectangle(
                    device, new Rect(0, 0, dstW, dstH), CornerRadius, CornerRadius);
                using (ds.CreateLayer(1f, geom))
                {
                    ds.DrawImage(srcBmp,
                        new Rect(0, 0, dstW, dstH),
                        new Rect(0, 0, frame.W, frame.H),
                        1f, CanvasImageInterpolation.MultiSampleLinear);
                }
            }

            float pad = shadow ? ShadowPad : 0f;
            int rtW = dstW + (int)(pad * 2);
            int rtH = dstH + (int)(pad * 2);

            var finalRt = new CanvasRenderTarget(device, rtW, rtH, dpi,
                Windows.Graphics.DirectX.DirectXPixelFormat.R8G8B8A8UIntNormalized,
                CanvasAlphaMode.Premultiplied);

            using (var ds = finalRt.CreateDrawingSession())
            {
                ds.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0));

                if (shadow)
                {
                    using var blur = new GaussianBlurEffect
                    {
                        Source = scaledRt,
                        BlurAmount = 8f,
                        BorderMode = EffectBorderMode.Soft,
                    };
                    using var shadowEffect = new ColorMatrixEffect
                    {
                        Source = blur,
                        ColorMatrix = new Matrix5x4 { M44 = 100f / 255f }
                    };
                    ds.DrawImage(shadowEffect, new Vector2(pad + 2, pad + 3));
                }

                ds.DrawImage(scaledRt, new Vector2(pad, pad));
            }

            return finalRt;
        }

        // ── 加载请求入口 ──────────────────────────────────────────────────────

        /// <summary>
        /// 请求加载新图片。
        ///
        /// isResize=true：绕过 animLock，直接进解码替换，不播动画。
        ///
        /// 普通请求（isResize=false）流程：
        ///   1. _animLock=true → 只更新 _pendingAfterAnim，完全不进解码，直接 return。
        ///      （天然防抖：动画窗口内的所有请求被截流，只保留最新一个）
        ///   2. _animLock=false → dedup 检查，通过后设 _animLock=true，进入解码。
        ///   3. 解码完成 → ConsumeDecodeChannel → BeginTransition 启动 CT 计时。
        ///   4. CT 到期（AnimLockMs 后）→ UnlockAndCheckPending：
        ///      对比 _pendingAfterAnim hash 与 _currentDisplayHash，
        ///      不同则触发下一轮 RequestLoad。
        /// </summary>
        public void RequestLoad(byte[]? bytes, bool isResize = false)
        {
            if (_disposed) return;
            if (!IsActive && _isResourcesCreated) return;

            if (isResize)
            {
                DispatchToDecoder(bytes, isResize: true);
                return;
            }

            // 1. 动画锁期间：严禁修改任何 Hash 记录，只暂存最新请求
            if (_animLock)
            {
                _pendingAfterAnim = bytes;
                return;
            }

            // 2. 此时锁是开的，检查是否与“当前正在处理/显示”的图重复
            if (bytes != null && IsDuplicate(bytes)) return;

            // 3. 确认为新请求：加锁，并立即更新“处理中”的标识
            _animLock = true;
            UpdateLastHash(bytes);

            DispatchToDecoder(bytes, isResize: false);
        }

        /// <summary>将请求打包发送给解码后台线程。</summary>
        private void DispatchToDecoder(byte[]? bytes, bool isResize)
        {
            float cw = 0f, ch = 0f;
            if (_canvas is { } c)
            {
                ComputeContentRect((float)c.Size.Width, (float)c.Size.Height);
                cw = (float)_contentRect.Width;
                ch = (float)_contentRect.Height;
            }

            Interlocked.Exchange(ref _pendingRequest,
                new PendingRequest(bytes, cw, ch, IsShadowEnabled, (float)DpiScale, isResize));

            TrySignalDecode();
        }

        private void TrySignalDecode()
        {
            if (_decodeSignal.CurrentCount == 0)
                _decodeSignal.Release();
        }

        // ── 解码循环（后台线程） ──────────────────────────────────────────────

        private async Task DecodeLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try { await _decodeSignal.WaitAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }

                var req = Interlocked.Exchange(ref _pendingRequest, null);
                if (req == null) continue;

                try
                {
                    DecodedFrame frame;
                    if (req.Bytes is { Length: > 0 } bytes)
                    {
                        var d = await DecodeImageAsync(bytes, ct).ConfigureAwait(false);
                        if (d == null) continue;
                        frame = d.Value;
                    }
                    else
                    {
                        var d = await DecodeDefaultAsync(ct).ConfigureAwait(false);
                        if (d == null) continue;
                        frame = d.Value;
                    }

                    await _decodeChannel.Writer.WriteAsync((frame, req), ct).ConfigureAwait(false);

                    _canvas?.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal,
                        () => _canvas?.Invalidate());
                }
                catch (OperationCanceledException) { break; }
                catch { /* 解码失败静默忽略 */ }
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

            var pd = await decoder.GetPixelDataAsync(
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
            return new DecodedFrame(pd.DetachPixelData(), (int)dstW, (int)dstH);
        }

        private async Task<DecodedFrame?> DecodeDefaultAsync(CancellationToken ct)
        {
            try
            {
                string name = IsDark ? "default_cover_black.png" : "default_cover_white.png";
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", name);
                var file = await Windows.Storage.StorageFile
                                  .GetFileFromPathAsync(path).AsTask(ct).ConfigureAwait(false);
                using var s = await file.OpenReadAsync().AsTask(ct).ConfigureAwait(false);

                var ms = new MemoryStream();
                using (var rs = s.AsStream()) await rs.CopyToAsync(ms, ct).ConfigureAwait(false);

                return await DecodeImageAsync(ms.ToArray(), ct).ConfigureAwait(false);
            }
            catch { return null; }
        }

        // ── 动画控制 ──────────────────────────────────────────────────────────

        /// <summary>
        /// 动画真正开始时调用（在 ConsumeDecodeChannel → ApplyNewBitmap 后）。
        /// 启动 CT 计时，AnimLockMs 后回 UI 线程执行 UnlockAndCheckPending。
        /// </summary>
        private void BeginTransition()
        {
            _t = 0f;
            _isFading = true;
            _lastDrawTicks = 0;

            // 取消上一轮残留计时（防御性）
            CancelAnimLock();
            _animLockCts = new CancellationTokenSource();
            var ct = _animLockCts.Token;
            var dq = _canvas?.DispatcherQueue;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(AnimLockMs, ct).ConfigureAwait(false);
                    dq?.TryEnqueue(DispatcherQueuePriority.Normal, UnlockAndCheckPending);
                }
                catch (OperationCanceledException) { }
            });

            _canvas?.Invalidate();
        }

        // ── 动画锁解除 ────────────────────────────────────────────────────────

        /// <summary>
        /// CT 到期后在 UI 线程执行。
        /// 解锁 _animLock，对比 _pendingAfterAnim 与 _currentDisplayHash：
        ///   - 相同 → 不重复触发，直接丢弃 pending
        ///   - 不同 → 触发新一轮 RequestLoad，此时 _animLock 已解，能正常进入解码
        /// </summary>
        private void UnlockAndCheckPending()
        {
            if (_disposed) return;

            // 1. 解锁
            _animLock = false;

            var pending = _pendingAfterAnim;
            _pendingAfterAnim = null;

            if (pending == null) return;

            // 2. 对比暂存请求与“屏幕上最终展示”的图
            int pendingHash = ToolUtils.ComputeFastHash(pending);

            // 如果暂存的图和现在屏幕上跑完动画的图是一样的，直接丢弃
            if (pending.Length == _lastLength && pendingHash == _currentDisplayHash)
                return;

            // 3. 如果不同，说明在动画期间用户又换图了，触发最后一轮
            // 此时 _animLock 已为 false，RequestLoad 会正常进入加锁流程
            RequestLoad(pending);
        }

        private void CancelAnimLock()
        {
            _animLockCts.Cancel();
            _animLockCts.Dispose();
            // 注意：不在这里 new 新的 CTS，由调用方决定是否需要重建
        }

        // ── 工具函数 ──────────────────────────────────────────────────────────

        private void ComputeContentRect(float cw, float ch)
        {
            float w = cw - Margin * 2, h = ch - Margin * 2;
            _contentRect = (w > 0 && h > 0) ? new Rect(Margin, Margin, w, h) : Rect.Empty;
        }

        private static Rect CalcAspectFitRect(float srcW, float srcH, Rect cr)
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

        private void InvalidateDedup()
        {
            _lastLength = -1;
            _lastHash = 0;
            _currentDisplayHash = -1; // 同时也重置显示快照
        }

        private bool IsDuplicate(byte[] b)
        {
            int hash = ToolUtils.ComputeFastHash(b);
            return b.Length == _lastLength && hash == _lastHash;
        }

        // 新增：专门用于在确定处理新图时更新标识
        private void UpdateLastHash(byte[]? b)
        {
            if (b == null || b.Length == 0)
            {
                _lastLength = -1;
                _lastHash = 0;
                return;
            }
            _lastLength = b.Length;
            _lastHash = ToolUtils.ComputeFastHash(b);
        }

        // ── Dispose ───────────────────────────────────────────────────────────

        public void Dispose() { Dispose(true); GC.SuppressFinalize(this); }

        private void Dispose(bool disposing)
        {
            if (!disposing || _disposed) return;
            _disposed = true;

            _pipelineCts.Cancel();
            _pipelineCts.Dispose();
            _decodeChannel.Writer.TryComplete();
            _decodeSignal.Dispose();

            _animLockCts.Cancel();
            _animLockCts.Dispose();
            _resizeCts.Cancel();
            _resizeCts.Dispose();

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