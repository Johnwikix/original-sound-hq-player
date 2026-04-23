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
        private const float FadeSpeed = 4f;       // t 从 0→1 的速度，总时长 = 1/FadeSpeed 秒
        private const float ScaleSmall = 0.90f;
        private const int ResizeDebounceMs = 200;

        // 动画锁冷却时长 = 动画总时长，在 RequestLoad 入口启动，覆盖整个动画执行窗口
        // 动画总时长：t 从 0 跑到 1，FadeSpeed = 4f → 1000/4 = 250 ms
        private static readonly int AnimLockMs = (int)(1000f * 4 / FadeSpeed);

        // ── 帧描述符 ──────────────────────────────────────────────────────────

        /// <summary>随 bitmap 一起存储的绘制参数，避免多个平行字段失步。</summary>
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
            c.InvalidateDedup(); // default cover 换了，强制绕过 dedup
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
                Interlocked.Exchange(ref c._pendingRequest, null);
                CancelTimer(ref c._animLockTimer);
                CancelTimer(ref c._resizeTimer);
                c._isFading = false;
                c._animLock = false;
                c._pendingAfterAnim = null;
                c._lastDrawTicks = 0;
                c._canvas?.Invalidate();
            }
        }

        // ── Pipeline 数据结构 ─────────────────────────────────────────────────

        /// <summary>解码后的原始像素（后台线程 → UI 线程）。</summary>
        private readonly record struct DecodedFrame(byte[] Pixels, int W, int H);

        /// <summary>
        /// 将 bytes 与所有 bake 参数合并为一个原子单元，
        /// 通过单次 Interlocked.Exchange 存储，消除两个独立字段间的不一致窗口。
        /// </summary>
        private sealed record PendingRequest(
            byte[]? Bytes,
            float ContentW, float ContentH,
            bool Shadow, float DpiScale,
            bool IsResize);

        // ── Pipeline 通道 ─────────────────────────────────────────────────────

        // 容量 1 + DropOldest：解码速度快于 UI 消费时自动丢弃旧帧，始终只保留最新
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

        // ── 动画锁（仅 UI 线程读写，无需 Interlocked） ───────────────────────
        //
        //  逻辑说明：
        //    RequestLoad 入口收到新 bytes 时立即启动 _animLockTimer（时长 = 动画总时长）。
        //    锁期间的后续 bytes 只暂存到 _pendingAfterAnim，不进入解码流程。
        //    Timer 到期（= 动画应已完成）时解锁，检查 _pendingAfterAnim 与当前显示图的
        //    hash，不同则触发下一轮 RequestLoad。
        //
        //    _currentDisplayHash：动画完成时快照的 _lastHash，代表屏幕上实际展示的图片，
        //    用于 timer 到期后的 dedup 对比。

        private bool _animLock;
        private byte[]? _pendingAfterAnim;
        private int _currentDisplayHash;

        private DispatcherQueueTimer? _animLockTimer;
        private DispatcherQueueTimer? _resizeTimer;

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

        private void Canvas_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!_isResourcesCreated || !IsActive) return;
            // 防抖：拖拽期间只响应最后一次稳定尺寸，整个逻辑在 UI 线程，无跨线程开销
            RestartTimer(ref _resizeTimer, ResizeDebounceMs, () =>
            {
                if (_disposed || !IsActive) return;
                RequestLoad(ImageBytes is { Length: > 0 } b ? b : null, isResize: true);
            });
        }

        private void Canvas_Draw(CanvasControl sender, CanvasDrawEventArgs e)
        {
            // 消费解码通道（GpuBake 必须在能访问 GPU 设备的 UI 线程上执行）
            ConsumeDecodeChannel(sender);

            // 推进动画（自驱动 delta time，消除 Rendering→Invalidate→Draw 帧错位）
            if (_isFading)
                TickAnimation();

            DrawFrame(e.DrawingSession, sender);

            if (_isFading)
                sender.Invalidate(); // 动画未结束，请求下一帧
        }

        // ── 解码通道消费 ──────────────────────────────────────────────────────

        private void ConsumeDecodeChannel(CanvasControl sender)
        {
            if (!_decodeChannel.Reader.TryRead(out var item)) return;

            var (frame, req) = item;

            // ContentW/H 在 canvas layout 之前可能为 0，此处用实际 Size 补救
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
                // 尺寸/参数变化：立即替换，不播过渡动画
                FinishFadeImmediately();
                _currentBmp?.Dispose();
                _currentBmp = bmp;
                _currentInfo = info;
                _canvas?.Invalidate();
                return;
            }

            // 若有进行中的动画，先快进到终态，避免 _nextBmp 被替换后仍被 Canvas_Draw 读到
            FinishFadeImmediately();

            _nextBmp = bmp;
            _nextInfo = info;
            BeginTransition();
        }

        /// <summary>将正在进行的动画立即推进到终态。</summary>
        private void FinishFadeImmediately()
        {
            if (!_isFading) return;
            if (_nextBmp != null)
            {
                _currentBmp?.Dispose();
                _currentBmp = _nextBmp;
                _currentInfo = _nextInfo;
                _nextBmp = null;
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
                delta = Math.Min(elapsed, 0.1f); // 防暂停后首帧跳变
            }
            _lastDrawTicks = now;

            _t = Math.Min(1f, _t + delta * FadeSpeed);

            // 过半时将 _nextBmp 升为 _currentBmp，新图从淡入阶段起就已就位
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
                // 动画完成：记录当前显示 hash，供 timer 到期后 dedup 对比
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
            // info.SrcW/H 是图片内容尺寸（不含 pad），用于在 _contentRect 内做 aspect-fit
            var contentDest = CalcAspectFitRect(info.SrcW, info.SrcH, _contentRect);
            if (contentDest.Width <= 0 || contentDest.Height <= 0) return;

            // bitmap 已内嵌 pad（阴影区域），以内容区中心为锚点向外扩展
            double cx = contentDest.X + contentDest.Width * 0.5;
            double cy = contentDest.Y + contentDest.Height * 0.5;
            double bmpW = (contentDest.Width + info.Pad * 2) * scale;
            double bmpH = (contentDest.Height + info.Pad * 2) * scale;
            var dest = new Rect(cx - bmpW * 0.5, cy - bmpH * 0.5, bmpW, bmpH);

            ds.DrawImage(bmp, dest, bmp.Bounds, alpha, CanvasImageInterpolation.Linear);
        }

        // ── GPU Bake ──────────────────────────────────────────────────────────

        /// <summary>
        /// 在 UI 线程上将解码像素上传 GPU，执行缩放 + 圆角裁剪 + 阴影合成，
        /// 返回已含阴影 padding 的 CanvasRenderTarget。
        /// </summary>
        private static CanvasBitmap? GpuBake(
            DecodedFrame frame,
            float contentW, float contentH,
            bool shadow, float dpiScale,
            CanvasControl sender)
        {
            if (frame.W <= 0 || frame.H <= 0 || contentW <= 0 || contentH <= 0) return null;

            var device = sender.Device;
            float dpi = 96f * dpiScale;

            // 1. 上传原始像素
            using var srcBmp = CanvasBitmap.CreateFromBytes(
                device, frame.Pixels, frame.W, frame.H,
                Windows.Graphics.DirectX.DirectXPixelFormat.R8G8B8A8UIntNormalized,
                dpi, CanvasAlphaMode.Premultiplied);

            // 2. 计算 aspect-fit 目标尺寸
            float aspect = (float)frame.W / frame.H;
            float drawW, drawH;
            if (aspect >= contentW / contentH) { drawW = contentW; drawH = drawW / aspect; }
            else { drawH = contentH; drawW = drawH * aspect; }
            int dstW = Math.Max(1, (int)drawW);
            int dstH = Math.Max(1, (int)drawH);

            // 3. 缩放 + 圆角裁剪 → 中间 RT
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

            // 4. 合成阴影 → 最终 RT
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
                    // 模糊 → 压暗成纯黑半透明阴影（RGB 清零，alpha 乘以 ~0.39）
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
        /// 请求加载新图片。isResize = true 时绕过动画锁，直接替换（不播过渡动画）。
        ///
        /// 动画锁逻辑（全部在 UI 线程读写，无竞态）：
        ///   收到新 bytes 时立即启动 _animLockTimer（时长 = 动画总时长 AnimLockMs），
        ///   覆盖整个动画执行窗口。锁期间的后续请求只暂存到 _pendingAfterAnim。
        ///   Timer 到期时解锁，对比 pending 与当前显示 hash，不同则触发下一轮过渡。
        /// </summary>
        public void RequestLoad(byte[]? bytes, bool isResize = false)
        {
            if (_disposed) return;
            if (!IsActive && _isResourcesCreated) return;

            if (_animLock && !isResize)
            {
                _pendingAfterAnim = bytes;
                return;
            }

            if (bytes is { Length: > 0 } && IsDuplicateAndUpdate(bytes)) return;

            // 非 resize 的正常图片切换：在解码开始前就加锁并启动计时
            // 计时长度覆盖解码 + 动画全程，timer 到期时动画应已自然结束
            if (!isResize)
            {
                _animLock = true;
                RestartTimer(ref _animLockTimer, AnimLockMs, UnlockAndCheckPending);
            }

            float cw = 0f, ch = 0f;
            if (_canvas is { } c)
            {
                ComputeContentRect((float)c.Size.Width, (float)c.Size.Height);
                cw = (float)_contentRect.Width;
                ch = (float)_contentRect.Height;
                // canvas 首次 layout 前 Size 为 0，ConsumeDecodeChannel 里会补救
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

                    // DropOldest 保证 UI 线程来不及消费时只保留最新帧
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

        private void BeginTransition()
        {
            _t = 0f;
            _isFading = true;
            _lastDrawTicks = 0;
            _canvas?.Invalidate();
        }

        // ── 动画锁 Timer 回调 ─────────────────────────────────────────────────

        /// <summary>
        /// Timer 到期（= 动画窗口结束）：解锁，若有暂存 bytes 且与当前显示图不同则触发下一轮。
        /// 只在 UI 线程调用（DispatcherQueueTimer tick）。
        /// </summary>
        private void UnlockAndCheckPending()
        {
            _animLock = false;

            var pending = _pendingAfterAnim;
            _pendingAfterAnim = null;
            if (pending is null) return;

            int pendingHash = ToolUtils.ComputeFastHash(pending);
            bool isSame = pending.Length == _lastLength && pendingHash == _currentDisplayHash;
            if (isSame) return;

            InvalidateDedup();
            RequestLoad(pending);
        }

        // ── Timer 工具 ────────────────────────────────────────────────────────

        /// <summary>停止旧 timer，新建单次 timer，到期后在 UI 线程执行 callback。</summary>
        private void RestartTimer(ref DispatcherQueueTimer? field, int delayMs, Action callback)
        {
            CancelTimer(ref field);

            var dq = DispatcherQueue.GetForCurrentThread() ?? _canvas?.DispatcherQueue;
            if (dq is null) { callback(); return; }

            var t = dq.CreateTimer();
            t.Interval = TimeSpan.FromMilliseconds(delayMs);
            t.IsRepeating = false;
            t.Tick += (sender, _) => { sender.Stop(); callback(); };
            t.Start();
            field = t;
        }

        private static void CancelTimer(ref DispatcherQueueTimer? field)
        {
            field?.Stop();
            field = null;
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

        private void InvalidateDedup() { _lastLength = -1; _lastHash = 0; }

        private bool IsDuplicateAndUpdate(byte[] b)
        {
            int hash = ToolUtils.ComputeFastHash(b);
            if (b.Length == _lastLength && hash == _lastHash) return true;
            _lastLength = b.Length;
            _lastHash = hash;
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
            _decodeChannel.Writer.TryComplete();
            _decodeSignal.Dispose();

            CancelTimer(ref _animLockTimer);
            CancelTimer(ref _resizeTimer);

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