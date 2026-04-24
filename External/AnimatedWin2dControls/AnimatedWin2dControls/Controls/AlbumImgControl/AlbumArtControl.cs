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
        private const int ResizeDebounceMs = 20;

        // 哨兵值：表示"从未初始化"，区别于"上次为 null（-1）"
        private const long NeverInitialized = long.MinValue;
        private static readonly int AnimLockMs = (int)(1000f * 2 / FadeSpeed);

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
            c.InvalidateDedup();
            c.RequestLoad(c.ImageBytes);
        }

        private static void OnResizeTriggerChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var c = (AlbumArtControl)d;
            if (!c._isResourcesCreated) return;
            c.RequestLoad(c.ImageBytes, isResize: true);
        }

        private static void OnIsActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var c = (AlbumArtControl)d;
            if (!c._isResourcesCreated) return;
            if ((bool)e.NewValue)
            {
                c.RequestLoad(c.ImageBytes);
            }
            else
            {
                Interlocked.Exchange(ref c._pendingRequest, null);
                c.CancelAnimLock();
                c.CancelSequenceEnd();
                c._animLock = false;
                c._sequenceActive = false;
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
            bool IsResize,
            bool IsDark);

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

        // ── contentRect 缓存（避免每帧重复计算） ─────────────────────────────
        private Rect _contentRect;
        private float _cachedContentW = -1f;
        private float _cachedContentH = -1f;

        // ── 动画锁与序列状态（仅 UI 线程） ───────────────────────────────────

        private bool _animLock;
        private bool _sequenceActive;
        private byte[]? _pendingAfterAnim;
        private int _currentDisplayHash;

        // 用 Interlocked.Exchange 做无锁安全替换，避免 Cancel/Dispose 竞态
        private CancellationTokenSource _animLockCts = new();
        private CancellationTokenSource _sequenceEndCts = new();
        private CancellationTokenSource _resizeCts = new();

        // ── Pipeline 控制（跨线程字段） ───────────────────────────────────────

        private CancellationTokenSource _pipelineCts = new();
        private PendingRequest? _pendingRequest;
        private readonly SemaphoreSlim _decodeSignal = new(0, 1);

        // 用 NeverInitialized 哨兵替代额外的 _initialized 布尔字段
        private long _lastLength = NeverInitialized;
        private int _lastHash;

        // ── 默认封面缓存（避免重复 IO + 解码） ───────────────────────────────
        // key: IsDark；value: 解码后的像素数据（只读，可安全跨请求共享）
        private static DecodedFrame? _cachedDefaultDark;
        private static DecodedFrame? _cachedDefaultLight;
        private static readonly SemaphoreSlim _defaultCacheLock = new(1, 1);

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
            // 取消旧 pipeline，重置内容缓存
            var oldCts = Interlocked.Exchange(ref _pipelineCts, new CancellationTokenSource());
            oldCts.Cancel();
            oldCts.Dispose();

            // 失效 contentRect 缓存（设备/DPI 可能已变）
            _cachedContentW = -1f;
            _cachedContentH = -1f;

            _isResourcesCreated = true;
            Task.Run(() => DecodeLoopAsync(_pipelineCts.Token));

            if (!IsActive) return;
            RequestLoad(ImageBytes);
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
                        RequestLoad(ImageBytes, isResize: true);
                    });
                }
                catch (OperationCanceledException) { }
            });
        }

        private void Canvas_Draw(CanvasControl sender, CanvasDrawEventArgs e)
        {
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
            if (cw <= 0 || ch <= 0)
            {
                ReleaseAnimLockIfNeeded(req.IsResize);
                return;
            }

            var bmp = GpuBake(frame, cw, ch, req.Shadow, req.DpiScale, sender);
            if (bmp == null)
            {
                ReleaseAnimLockIfNeeded(req.IsResize);
                return;
            }

            float pad = req.Shadow ? ShadowPad : 0f;
            ApplyNewBitmap(bmp, new FrameInfo(frame.W, frame.H, pad), req.IsResize);
        }

        private void ReleaseAnimLockIfNeeded(bool isResize)
        {
            if (isResize || !_animLock) return;
            _animLock = false;
            CheckPendingAfterUnlock();
        }

        private void ApplyNewBitmap(CanvasBitmap bmp, FrameInfo info, bool isResize)
        {
            if (isResize)
            {
                FinishFadeImmediately();
                _currentBmp?.Dispose();
                _currentBmp = bmp;
                _currentInfo = info;
                _canvas?.Invalidate();
                return;
            }

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
                // 修复：强制结束时同步 _currentDisplayHash，防止 dedup 误判
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

            // scaledRt 的生命周期须延伸到 finalRt 的 DrawImage 调用之后，
            // 因为 shadowEffect → blur → scaledRt 形成引用链，提前 Dispose 会崩溃。
            // 所以不使用 using，改为手动 Dispose。
            CanvasRenderTarget? scaledRt = null;
            try
            {
                scaledRt = new CanvasRenderTarget(device, dstW, dstH, dpi,
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
                        // blur 和 shadowEffect 必须在 scaledRt dispose 之前 DrawImage 完成
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
            finally
            {
                scaledRt?.Dispose();
            }
        }

        // ── 加载请求入口 ──────────────────────────────────────────────────────

        /// <summary>
        /// 请求加载新图片。
        ///
        /// isResize=true：绕过所有锁，直接替换，不播动画。
        ///
        /// 普通请求流程（序列防抖）：
        ///
        ///   阶段 A — _animLock=false 且 _sequenceActive=false（空闲状态）：
        ///     dedup 通过后 → 设 _animLock=true → 进解码 → 播第一帧动画
        ///     这是"序列开始"动画。
        ///
        ///   阶段 B — _animLock=true（动画正在播放）：
        ///     只更新 _pendingAfterAnim，不进解码。
        ///     动画播完 → UnlockAndCheckPending：
        ///       有 pending → 进入阶段 C（序列检测）
        ///       无 pending → 回到阶段 A
        ///
        ///   阶段 C — _sequenceActive=true（序列结束检测窗口）：
        ///     启动 _sequenceEndCts 等待 AnimLockMs。
        ///     等待期间收到新请求 → 只更新 _pendingAfterAnim，重置计时。
        ///     等待期满无新请求 → SequenceEndFire：
        ///       触发 RequestLoad(pending) → 播最后一帧动画 → 回到阶段 A/B。
        ///
        ///   对外效果：连续快速切换只播两次动画（第一张 + 最后一张），中间静默跳过。
        /// </summary>
        public void RequestLoad(byte[]? bytes, bool isResize = false)
        {
            if (_disposed) return;
            if (!IsActive && _isResourcesCreated) return;

            byte[]? targetBytes = (bytes is { Length: > 0 }) ? bytes : null;

            // isResize 绕过所有状态，直接进解码
            if (isResize)
            {
                DispatchToDecoder(targetBytes, isResize: true);
                return;
            }

            // 阶段 B：动画正在播放，暂存请求
            if (_animLock)
            {
                _pendingAfterAnim = targetBytes;
                return;
            }

            // 阶段 C：序列检测窗口，重置计时并暂存
            if (_sequenceActive)
            {
                _pendingAfterAnim = targetBytes;
                RestartSequenceEndTimer();
                return;
            }

            // 阶段 A：空闲，dedup 检查后进解码
            if (IsDuplicate(targetBytes)) return;

            UpdateLastHash(targetBytes);
            _animLock = true;

            DispatchToDecoder(targetBytes, isResize: false);
        }

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
                new PendingRequest(bytes, cw, ch, IsShadowEnabled, (float)DpiScale, isResize, IsDark));

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
                string path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", name);
                var file = await Windows.Storage.StorageFile
                    .GetFileFromPathAsync(path).AsTask(ct).ConfigureAwait(false);

                using var s = await file.OpenReadAsync().AsTask(ct).ConfigureAwait(false);
                var ms = new MemoryStream();
                using (var rs = s.AsStream())
                    await rs.CopyToAsync(ms, ct).ConfigureAwait(false);

                var frame = await DecodeImageAsync(ms.ToArray(), ct).ConfigureAwait(false);
                if (frame == null) return null;

                if (isDark) _cachedDefaultDark = frame;
                else _cachedDefaultLight = frame;

                return frame;
            }
            catch { return null; }
            finally { _defaultCacheLock.Release(); }
        }

        // ── 动画控制 ──────────────────────────────────────────────────────────

        private void BeginTransition()
        {
            _t = 0f;
            _isFading = true;
            _lastDrawTicks = 0;

            // 先构造新 CTS，再取消旧的，避免 double-dispose
            var newCts = new CancellationTokenSource();
            var oldCts = Interlocked.Exchange(ref _animLockCts, newCts);
            oldCts.Cancel();
            oldCts.Dispose();

            var ct = newCts.Token;
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

        // ── 动画锁解除与序列检测 ──────────────────────────────────────────────

        private void UnlockAndCheckPending()
        {
            if (_disposed) return;

            _animLock = false;

            var pending = _pendingAfterAnim;
            _pendingAfterAnim = null;

            if (pending == null) return;

            _sequenceActive = true;
            _pendingAfterAnim = pending;
            RestartSequenceEndTimer();
        }

        private void RestartSequenceEndTimer()
        {
            // 先构造新 CTS，再取消旧的，避免 double-dispose
            var newCts = new CancellationTokenSource();
            var oldCts = Interlocked.Exchange(ref _sequenceEndCts, newCts);
            oldCts.Cancel();
            oldCts.Dispose();

            var ct = newCts.Token;
            var dq = _canvas?.DispatcherQueue;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(AnimLockMs, ct).ConfigureAwait(false);
                    dq?.TryEnqueue(DispatcherQueuePriority.Normal, SequenceEndFire);
                }
                catch (OperationCanceledException) { }
            });
        }

        private void SequenceEndFire()
        {
            if (_disposed) return;

            _sequenceActive = false;

            var pending = _pendingAfterAnim;
            _pendingAfterAnim = null;

            if (pending == null) return;

            int pendingHash = ToolUtils.ComputeFastHash(pending);
            if (pending.Length == _lastLength && pendingHash == _currentDisplayHash)
                return;

            RequestLoad(pending);
        }

        private void CheckPendingAfterUnlock()
        {
            UnlockAndCheckPending();
        }

        private void CancelAnimLock()
        {
            // 替换为已取消的 dummy CTS，确保旧 CTS 只被 Cancel+Dispose 一次
            var oldCts = Interlocked.Exchange(ref _animLockCts, new CancellationTokenSource());
            oldCts.Cancel();
            oldCts.Dispose();
        }

        private void CancelSequenceEnd()
        {
            var oldCts = Interlocked.Exchange(ref _sequenceEndCts, new CancellationTokenSource());
            oldCts.Cancel();
            oldCts.Dispose();
        }

        // ── 工具函数 ──────────────────────────────────────────────────────────

        /// <summary>
        /// 计算内容区域，结果缓存至 _contentRect。
        /// 仅当画布尺寸变化时重新计算。
        /// </summary>
        private void ComputeContentRect(float cw, float ch)
        {
            // 尺寸未变化则直接复用缓存
            // ReSharper disable once CompareOfFloatsByEqualityOperator
            if (cw == _cachedContentW && ch == _cachedContentH) return;

            _cachedContentW = cw;
            _cachedContentH = ch;

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
            _lastLength = NeverInitialized;
            _lastHash = 0;
            _currentDisplayHash = -1;
        }

        private bool IsDuplicate(byte[]? b)
        {
            // 从未初始化（NeverInitialized 哨兵），不判重
            if (_lastLength == NeverInitialized) return false;

            if (b == null || b.Length == 0)
                return _lastLength == -1;

            int hash = ToolUtils.ComputeFastHash(b);
            return b.Length == _lastLength && hash == _lastHash;
        }

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
            _isResourcesCreated = false; // 防止 dispose 后回调进入逻辑

            var pCts = Interlocked.Exchange(ref _pipelineCts, new CancellationTokenSource());
            pCts.Cancel();
            pCts.Dispose();

            _decodeChannel.Writer.TryComplete();
            _decodeSignal.Dispose();

            CancelAnimLock();
            CancelSequenceEnd();

            var rCts = Interlocked.Exchange(ref _resizeCts, new CancellationTokenSource());
            rCts.Cancel();
            rCts.Dispose();

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