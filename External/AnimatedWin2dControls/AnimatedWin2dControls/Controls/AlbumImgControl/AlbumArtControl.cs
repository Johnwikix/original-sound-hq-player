using AnimatedWin2dControls.Utils;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Graphics.Imaging;

namespace AnimatedWin2dControls.Controls.AlbumImgControl
{
    [TemplatePart(Name = PartCanvas, Type = typeof(CanvasControl))]
    public sealed class AlbumArtControl : Control, IDisposable, ISharedTickable
    {
        private const string PartCanvas = "canvas";

        // ── 依赖属性 ──────────────────────────────────────────────────────────
        // (DpiScale / ImageBytes / IsDark / Margin*Ratio / ArtCornerRadius /
        //  IsShadowEnabled / IsActive 保持不变，略)

        // ── 依赖属性（保持不变） ──────────────────────────────────────────────

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

        public static readonly DependencyProperty IsActiveProperty =
            DependencyProperty.Register(nameof(IsActive), typeof(bool),
                typeof(AlbumArtControl), new PropertyMetadata(false, OnIsActiveChanged));
        public bool IsActive
        {
            get => (bool)GetValue(IsActiveProperty);
            set => SetValue(IsActiveProperty, value);
        }
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
            c.InvalidateDedup();
            // IsDark 变化需要重新加载默认封面或重新解码（重新 bake 颜色不变，只有默认封面受影响）
            if (c.ImageBytes is { Length: > 0 } b) c.RequestLoad(b);
            else c.RequestLoad(null);
        }

        private static void OnDpiScaleChanged(DependencyObject d,
            DependencyPropertyChangedEventArgs e)
        {
            var c = (AlbumArtControl)d;
            if (!c._isResourcesCreated) return;
            // DpiScale 变化：重新 bake，当前 bitmap 字节不变
            if (c.ImageBytes is { Length: > 0 } b) c.RequestLoad(b, isResize: true);
            else c.RequestLoad(null, isResize: true);
        }

        private static void OnLayoutChanged(DependencyObject d,
            DependencyPropertyChangedEventArgs e)
        {
            var c = (AlbumArtControl)d;
            if (!c._isResourcesCreated || !c.IsActive) return;
            c.OnContentSizeChanged();  // 走 isResize=true 路径
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
                // 停用时：取消 pending，清空动画状态
                Interlocked.Exchange(ref c._pendingLoad, null);
                c._isFading = false;
                c.StopRenderingLoop();
                c._canvas?.Invalidate();
            }
        }

        // ── Pipeline 数据结构 ─────────────────────────────────────────────────
        private float _bakedRadius;
        private bool _bakedShadowEnabled;
        private float _bakedDpiScale;
        /// <summary>Stage 1 → Stage 2：解码后的原始像素</summary>
        private readonly record struct RawFrame(
            byte[] Pixels, int W, int H, int SrcW, int SrcH);

        /// <summary>Stage 2 → Stage 3：CPU 合成完毕的像素（含阴影/圆角）</summary>
        private readonly record struct BakedFrame(
            byte[] Pixels, int W, int H,      // RT 尺寸（含 pad）
            float Pad,
            float SrcW, float SrcH,           // 原图宽高，用于 letterbox
            bool IsResize);                   // 是否跳过动画

        // ── Pipeline 管道 ─────────────────────────────────────────────────────

        // capacity=1，WriteAsync 时若满则先丢弃旧值再写入
        private readonly Channel<RawFrameWithParams> _rawChannel;
        private readonly record struct RawFrameWithParams(RawFrame Raw, BakeParams Params);
        private readonly Channel<BakedFrame> _bakedChannel;

        // ── 渲染状态（仅 UI 线程访问） ────────────────────────────────────────
        private CanvasControl? _canvas;
        private bool _isResourcesCreated;
        private bool _disposed;

        private CanvasBitmap? _currentBmp;   // 当前显示的 bitmap（已上传 GPU）
        private CanvasBitmap? _nextBmp;      // 等待切入的 bitmap

        private float _srcWCur, _srcHCur;   // 当前帧原图尺寸
        private float _srcWNext, _srcHNext;
        private float _padCur, _padNext;

        // Scale-fade 动画
        private float _t;
        private bool _isFading;
        private const float FadeSpeed = 4f;
        private const float ScaleSmall = 0.90f;

        private Rect _contentRect;
        private bool _isClockRegistered;

        // ── Pipeline 控制 ────────────────────────────────────────────────────
        private CancellationTokenSource _pipelineCts = new();
        private Task _decodeLoop = Task.CompletedTask;
        private Task _bakeLoop = Task.CompletedTask;

        // 解码请求队列（只保留最新一条）
        private PendingLoad? _pendingLoad;
        private readonly SemaphoreSlim _decodeSignal = new(0, 1);

        private bool _hasEverLoaded;
        private long _lastLength = -1;
        private int _lastHash;
        private const float HardMaxSize = 1280f;
        private int _debounceSeq;

        /// <summary>UI 线程写，Bake loop 读，用 Interlocked 保证原子替换</summary>
        private sealed class BakeParams
        {
            public readonly float ContentW;
            public readonly float ContentH;
            public readonly float Radius;
            public readonly bool Shadow;
            public readonly float DpiScale;
            public readonly bool IsResize;
            public BakeParams(float cw, float ch, float r, bool s, float d, bool resize)
            { ContentW = cw; ContentH = ch; Radius = r; Shadow = s; DpiScale = d; IsResize = resize; }
        }
        // 字段声明（替换三个 volatile）：
        private BakeParams? _pendingBakeParams;  // Interlocked.Exchange 访问

        private sealed record PendingLoad(byte[]? Bytes, bool IsResize);

        // ── 构造 ──────────────────────────────────────────────────────────────

        public AlbumArtControl()
        {
            DefaultStyleKey = typeof(AlbumArtControl);

            // capacity=1 的 bounded channel，满时 WriteAsync 会等待
            // 我们用 DropOldest 策略：写前先 TryRead 丢弃
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
            StartPipelineLoops();
            if (!IsActive) return;
            e.TrackAsyncAction(CreateResourcesAsync().AsAsyncAction());
        }

        private async Task CreateResourcesAsync()
        {
            // 启动 pipeline loops（如果还没启动）
            if (_decodeLoop.IsCompleted) StartPipelineLoops();

            if (ImageBytes is { Length: > 0 } b) RequestLoad(b);
            else RequestLoad(null);
            await Task.CompletedTask;
        }

        // ── Draw（Stage 3 入口） ──────────────────────────────────────────────

        private void Canvas_Draw(CanvasControl sender, CanvasDrawEventArgs e)
        {
            // 消费 baked channel：上传 GPU，准备动画
            ConsumeBakedChannel(sender);
            DrawFrame(e.DrawingSession, sender);
        }

        private void ConsumeBakedChannel(CanvasControl sender)
        {
            // 只取最新一帧（channel 满时 bake loop 自动丢弃了旧的）
            if (!_bakedChannel.Reader.TryRead(out var frame)) return;

            // GPU 上传：在 Draw 回调里做，此时设备锁已持有，无竞争
            var bmp = CanvasBitmap.CreateFromBytes(
                        sender,
                        frame.Pixels,
                        frame.W, frame.H,
                        // 明确告知 Win2D 数据是预乘格式，不做额外转换
                        Windows.Graphics.DirectX.DirectXPixelFormat.R8G8B8A8UIntNormalized,
                        96f * (float)DpiScale,  // dpi
                        CanvasAlphaMode.Premultiplied);  // ← 新增

            if (frame.IsResize)
            {
                // resize 重建：直接替换，不播动画
                _currentBmp?.Dispose();
                _currentBmp = bmp;
                _srcWCur = frame.SrcW; _srcHCur = frame.SrcH;
                _padCur = frame.Pad;
                _nextBmp?.Dispose();
                _nextBmp = null;
                _isFading = false;
                _canvas?.Invalidate();
            }
            else
            {
                // 正常切换：把当前动画的 next 替换掉
                _nextBmp?.Dispose();
                _nextBmp = bmp;
                _srcWNext = frame.SrcW; _srcHNext = frame.SrcH;
                _padNext = frame.Pad;

                if (!_isFading)
                    BeginTransition();
                // 若已在 fading：_nextBmp 已就绪，OnSharedTick 里 t>=0.5 时自然切入
            }
        }

        // ── Stage 1：Decode Loop ──────────────────────────────────────────────

        private void StartPipelineLoops()
        {
            _pipelineCts = new CancellationTokenSource();
            var token = _pipelineCts.Token;
            _decodeLoop = Task.Run(() => DecodeLoopAsync(token), token);
            _bakeLoop = Task.Run(() => BakeLoopAsync(token), token);
        }

        private async Task DecodeLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await _decodeSignal.WaitAsync(ct).ConfigureAwait(false);
                }
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

                    // 把 bakeParams 和 raw 一起写入 channel
                    await _rawChannel.Writer.WriteAsync(
                        new RawFrameWithParams(raw, bakeP), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch { /* 静默 */ }
            }
        }

        // ── Stage 2：Bake Loop ────────────────────────────────────────────────

        private async Task BakeLoopAsync(CancellationToken ct)
        {
            await foreach (var item in _rawChannel.Reader.ReadAllAsync(ct))
            {
                try
                {
                    var raw = item.Raw;
                    var p = item.Params;  // 完全从快照读，不碰 DependencyProperty

                    if (p.ContentW <= 0 || p.ContentH <= 0) continue;

                    var baked = await Task.Run(() =>
                        CpuBake(raw, p.ContentW, p.ContentH,
                                p.Radius, p.Shadow, p.DpiScale, p.IsResize), ct)
                        .ConfigureAwait(false);

                    if (baked == null) continue;

                    await _bakedChannel.Writer.WriteAsync(baked.Value, ct)
                        .ConfigureAwait(false);

                    _canvas?.Invalidate();
                }
                catch (OperationCanceledException) { break; }
                catch { }
            }
        }

        // ── CPU Bake（纯软件，无 D2D） ────────────────────────────────────────

        private static BakedFrame? CpuBake(
    RawFrame raw,
    float contentW, float contentH,
    float radius, bool shadow, float dpiScale,
    bool isResize)
        {
            float aspect = (float)raw.SrcW / raw.SrcH;
            float drawW, drawH;
            if (aspect >= contentW / contentH) { drawW = contentW; drawH = drawW / aspect; }
            else { drawH = contentH; drawW = drawH * aspect; }

            int dstW = Math.Max(1, (int)drawW);
            int dstH = Math.Max(1, (int)drawH);

            // 1. 缩放
            var scaled = BilinearScale(raw.Pixels, raw.W, raw.H, dstW, dstH);

            // 2. 圆角 mask —— 输入是预乘，输出也是预乘，RGB+A 同步缩放
            ApplyRoundedCornerMask(scaled, dstW, dstH, radius);

            int pad = shadow ? 34 : 0;
            int rtW = dstW + pad * 2;
            int rtH = dstH + pad * 2;
            var result = new byte[rtW * rtH * 4];  // 全透明黑底

            if (shadow)
            {
                // 3a. 生成阴影层（已是预乘格式：RGB=0, A=模糊alpha*shadowAlpha）
                var shadowLayer = GenerateShadow(scaled, dstW, dstH, rtW, rtH,
                    pad, blurRadius: 10, shadowAlpha: 100, offsetX: 2, offsetY: 3);

                // 3b. 先把阴影 blend 到 result
                BlendOver(result, shadowLayer, rtW, rtH);

                // 3c. 再把图像层（预乘）blend 到 result 上方
                //     不能用 PasteBitmap（直接覆盖会踩掉阴影），改用 BlendOver
                var imageLayer = new byte[rtW * rtH * 4];  // 全透明
                PasteBitmap(imageLayer, rtW, scaled, dstW, dstH, pad, pad);
                BlendOver(result, imageLayer, rtW, rtH);
            }
            else
            {
                // 无阴影：直接贴，pad=0 所以 imageLayer 就是 result
                PasteBitmap(result, rtW, scaled, dstW, dstH, pad, pad);
            }

            return new BakedFrame(result, rtW, rtH, pad,
                raw.SrcW, raw.SrcH, isResize);
        }

        // ── CPU 图像工具函数 ──────────────────────────────────────────────────

        private static byte[] BilinearScale(byte[] src, int srcW, int srcH,
                                             int dstW, int dstH)
        {
            var dst = new byte[dstW * dstH * 4];
            float xRatio = (float)(srcW - 1) / Math.Max(1, dstW - 1);
            float yRatio = (float)(srcH - 1) / Math.Max(1, dstH - 1);

            for (int dy = 0; dy < dstH; dy++)
            {
                float fy = dy * yRatio;
                int y0 = (int)fy, y1 = Math.Min(y0 + 1, srcH - 1);
                float yw = fy - y0;

                for (int dx = 0; dx < dstW; dx++)
                {
                    float fx = dx * xRatio;
                    int x0 = (int)fx, x1 = Math.Min(x0 + 1, srcW - 1);
                    float xw = fx - x0;

                    int s00 = (y0 * srcW + x0) * 4;
                    int s10 = (y0 * srcW + x1) * 4;
                    int s01 = (y1 * srcW + x0) * 4;
                    int s11 = (y1 * srcW + x1) * 4;

                    int d = (dy * dstW + dx) * 4;
                    for (int c = 0; c < 4; c++)
                    {
                        float v = src[s00 + c] * (1 - xw) * (1 - yw)
                                + src[s10 + c] * xw * (1 - yw)
                                + src[s01 + c] * (1 - xw) * yw
                                + src[s11 + c] * xw * yw;
                        dst[d + c] = (byte)Math.Clamp((int)v, 0, 255);
                    }
                }
            }
            return dst;
        }

        private static void ApplyRoundedCornerMask(byte[] pixels,
                                             int w, int h, float r)
        {
            r = Math.Min(r, Math.Min(w, h) * 0.5f);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float cx = Math.Max(0, Math.Max(r - x, x - (w - r - 1)));
                    float cy = Math.Max(0, Math.Max(r - y, y - (h - r - 1)));
                    float dist = MathF.Sqrt(cx * cx + cy * cy);
                    float alpha = Math.Clamp(r - dist + 0.5f, 0f, 1f);

                    if (alpha >= 1f) continue; // 完全不透明，跳过

                    int idx = (y * w + x) * 4;
                    // 预乘格式：RGB 和 A 必须同比例缩放
                    pixels[idx] = (byte)(pixels[idx] * alpha);
                    pixels[idx + 1] = (byte)(pixels[idx + 1] * alpha);
                    pixels[idx + 2] = (byte)(pixels[idx + 2] * alpha);
                    pixels[idx + 3] = (byte)(pixels[idx + 3] * alpha);
                }
        }

        private static byte[] GenerateShadow(
            byte[] src, int srcW, int srcH,
            int rtW, int rtH,
            int pad, int blurRadius,
            byte shadowAlpha, int offsetX, int offsetY)
        {
            // 1. 把 src alpha 通道扩展到 rtW×rtH，偏移 (pad+offsetX, pad+offsetY)
            var alpha = new float[rtW * rtH];
            for (int y = 0; y < srcH; y++)
                for (int x = 0; x < srcW; x++)
                {
                    int dx = x + pad + offsetX;
                    int dy = y + pad + offsetY;
                    if ((uint)dx < (uint)rtW && (uint)dy < (uint)rtH)
                        alpha[dy * rtW + dx] = src[(y * srcW + x) * 4 + 3] / 255f;
                }

            // 2. 分离式高斯模糊（水平 + 垂直）
            alpha = GaussianBlur1D(alpha, rtW, rtH, blurRadius, horizontal: true);
            alpha = GaussianBlur1D(alpha, rtW, rtH, blurRadius, horizontal: false);

            // 3. 生成阴影像素（纯黑 + 模糊后的 alpha）
            var shadow = new byte[rtW * rtH * 4];
            for (int i = 0; i < rtW * rtH; i++)
            {
                int a = (int)(alpha[i] * shadowAlpha);
                shadow[i * 4 + 3] = (byte)Math.Clamp(a, 0, 255);
                // R/G/B = 0（黑色阴影）
            }
            return shadow;
        }

        private static float[] GaussianBlur1D(
            float[] src, int w, int h, int radius, bool horizontal)
        {
            // 构建高斯核
            int size = radius * 2 + 1;
            float sigma = radius / 2f;
            float[] kernel = new float[size];
            float sum = 0;
            for (int i = 0; i < size; i++)
            {
                int x = i - radius;
                kernel[i] = MathF.Exp(-x * x / (2 * sigma * sigma));
                sum += kernel[i];
            }
            for (int i = 0; i < size; i++) kernel[i] /= sum;

            var dst = new float[w * h];
            if (horizontal)
            {
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        float v = 0;
                        for (int k = 0; k < size; k++)
                        {
                            int sx = Math.Clamp(x + k - radius, 0, w - 1);
                            v += src[y * w + sx] * kernel[k];
                        }
                        dst[y * w + x] = v;
                    }
            }
            else
            {
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        float v = 0;
                        for (int k = 0; k < size; k++)
                        {
                            int sy = Math.Clamp(y + k - radius, 0, h - 1);
                            v += src[sy * w + x] * kernel[k];
                        }
                        dst[y * w + x] = v;
                    }
            }
            return dst;
        }

        private static void BlendOver(byte[] dst, byte[] src, int w, int h)
        {
            for (int i = 0; i < w * h * 4; i += 4)
            {
                float invA = (255 - src[i + 3]) / 255f;  // (1 - src_alpha)
                dst[i] = (byte)Math.Clamp(src[i] + dst[i] * invA, 0, 255);
                dst[i + 1] = (byte)Math.Clamp(src[i + 1] + dst[i + 1] * invA, 0, 255);
                dst[i + 2] = (byte)Math.Clamp(src[i + 2] + dst[i + 2] * invA, 0, 255);
                dst[i + 3] = (byte)Math.Clamp(src[i + 3] + dst[i + 3] * invA, 0, 255);
            }
        }

        private static void PasteBitmap(byte[] dst, int dstW,
                                         byte[] src, int srcW, int srcH,
                                         int offX, int offY)
        {
            for (int y = 0; y < srcH; y++)
            {
                int dRow = ((y + offY) * dstW + offX) * 4;
                int sRow = y * srcW * 4;
                Buffer.BlockCopy(src, sRow, dst, dRow, srcW * 4);
            }
        }

        // ── 加载请求接口 ──────────────────────────────────────────────────────

        public void RequestLoad(byte[]? bytes, bool isResize = false)
        {
            if (_disposed) return;
            if (!IsActive && _isResourcesCreated) return;

            if (bytes is { Length: > 0 })
            {
                if (IsDuplicateAndUpdate(bytes)) return;
            }

            // 在 UI 线程打包好所有参数，后台线程只读这一个对象
            float cw = 0, ch = 0;
            if (_canvas is { } c)
            {
                ComputeContentRect((float)c.Size.Width, (float)c.Size.Height);
                cw = (float)_contentRect.Width;
                ch = (float)_contentRect.Height;
            }

            var p = new BakeParams(cw, ch,
                (float)ArtCornerRadius, IsShadowEnabled, (float)DpiScale, isResize);
            Interlocked.Exchange(ref _pendingBakeParams, p);

            bool firstLoad = !_hasEverLoaded;
            _hasEverLoaded = true;

            // 原子替换 pending load
            Interlocked.Exchange(ref _pendingLoad,
                new PendingLoad(bytes, isResize));

            int delay = firstLoad ? 0 : 300;
            if (delay == 0)
            {
                // 首次：直接触发
                TrySignalDecode();
            }
            else
            {
                // 防抖：用 sequence 号避免旧闭包误触发
                int seq = Interlocked.Increment(ref _debounceSeq);
                _ = Task.Run(async () =>
                {
                    await Task.Delay(delay).ConfigureAwait(false);
                    // 序列号没被新请求覆盖才触发
                    if (Interlocked.CompareExchange(ref _debounceSeq, seq, seq) == seq)
                        TrySignalDecode();
                });
            }
        }

        private void TrySignalDecode()
        {
            // SemaphoreSlim 最多 1，避免信号堆积
            if (_decodeSignal.CurrentCount == 0)
                _decodeSignal.Release();
        }

        private async Task<(byte[] Pixels, uint W, uint H)?> DecodeImageAsync(
            byte[] bytes, CancellationToken ct)
        {
            using var mem = new MemoryStream(bytes, writable: false);
            using var ras = mem.AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(ras).AsTask(ct)
                              .ConfigureAwait(false);

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

                // 读全部字节后走统一解码路径
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

        // ── 动画（OnSharedTick / DrawFrame 保持原有逻辑） ─────────────────────

        private void BeginTransition()
        {
            _t = 0f;
            _isFading = true;
            StartRenderingLoop();
        }

        public void OnSharedTick(TimeSpan elapsed)
        {
            if (!_isFading) return;

            float delta = Math.Min((float)elapsed.TotalSeconds, 0.1f);
            _t = Math.Min(1f, _t + delta * FadeSpeed);

            if (_t >= 0.5f && _nextBmp != null)
            {
                _currentBmp?.Dispose();
                _currentBmp = _nextBmp; _nextBmp = null;
                _srcWCur = _srcWNext; _srcHCur = _srcHNext;
                _padCur = _padNext;
            }

            if (_t >= 1f)
            {
                _t = 0f;
                _isFading = false;
                StopRenderingLoop();
            }

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
    float srcW, float srcH, float pad,
    float alpha, float scale)
        {
            // destRect 是图像内容区（不含 pad）的 letterbox 矩形
            var destRect = CalcDestRectFromSize(srcW, srcH, _contentRect);
            if (destRect.Width <= 0 || destRect.Height <= 0) return;

            // 中心点
            double cx = destRect.X + destRect.Width * 0.5;
            double cy = destRect.Y + destRect.Height * 0.5;

            // RT 实际尺寸 = 内容尺寸 + pad*2，scale 作用于整体
            // bmp.Bounds 是 RT 的物理像素矩形（含 pad），
            // 但我们要把它映射到 destRect+pad 扩展后的区域
            double dw = (destRect.Width + pad * 2) * scale;
            double dh = (destRect.Height + pad * 2) * scale;

            // dest 是以内容中心为锚点的扩展矩形（阴影自然溢出内容区域）
            var dest = new Rect(cx - dw * 0.5, cy - dh * 0.5, dw, dh);

            // DrawImage 第三参数 sourceRect 必须是 bmp 的完整 Bounds，
            // 这样 RT 里的 pad 区域（阴影）才会被画出来
            ds.DrawImage(bmp, dest, bmp.Bounds, alpha,
                         CanvasImageInterpolation.Linear);
        }

        // ── 尺寸变化处理 ──────────────────────────────────────────────────────

        // 在 SizeChanged 或 layout 变化时调用（替换原来的 _rtInvalidated 路径）
        private void OnContentSizeChanged()
        {
            if (!_isResourcesCreated || !IsActive) return;
            if (ImageBytes is { Length: > 0 } b) RequestLoad(b, isResize: true);
            else RequestLoad(null, isResize: true);
        }

        // ── 工具函数 ──────────────────────────────────────────────────────────

        private void ComputeContentRect(float cw, float ch)
        {
            float x = (float)MarginLeftRatio, y = (float)MarginTopRatio;
            float w = cw - x - (float)MarginRightRatio;
            float h = ch - y - (float)MarginBottomRatio;
            _contentRect = w > 0 && h > 0 ? new Rect(x, y, w, h) : Rect.Empty;
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
            if (b is not { Length: > 0 }) { bool e = _lastLength == 0; _lastLength = 0; _lastHash = 0; return e; }
            int hash = ToolUtils.ComputeFastHash(b);
            if (b.Length == _lastLength && hash == _lastHash) return true;
            _lastLength = b.Length; _lastHash = hash; return false;
        }
        public void InvalidateDedup() { _lastLength = -1; _lastHash = 0; }

        private void StartRenderingLoop()
        {
            if (_isClockRegistered) return;
            SharedAnimationClock.Register(this); _isClockRegistered = true;
        }
        private void StopRenderingLoop()
        {
            if (!_isClockRegistered) return;
            SharedAnimationClock.Unregister(this); _isClockRegistered = false;
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
            StopRenderingLoop();

            if (_canvas != null)
            {
                _canvas.CreateResources -= Canvas_CreateResources;
                _canvas.Draw -= Canvas_Draw;
                _canvas = null;
            }

            _currentBmp?.Dispose();
            _nextBmp?.Dispose();
        }
    }
}