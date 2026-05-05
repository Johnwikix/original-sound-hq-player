using AnimatedWin2dControls.Utils;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Threading;
using System.Threading.Channels;
using Windows.Foundation;

namespace AnimatedWin2dControls.Controls.AlbumImgControl
{
    /// <summary>
    /// 专辑封面控件（Win2D）。
    ///
    /// 文件拆分结构：
    ///   AlbumArtControl.cs              — 核心定义：常量、依赖属性、字段、构造/模板
    ///   AlbumArtControl.Pipeline.cs     — Pipeline 解码：后台循环、图像解码、默认封面缓存
    ///   AlbumArtControl.LoadScheduler.cs— 加载调度：RequestLoad 序列防抖状态机
    ///   AlbumArtControl.Animation.cs    — 动画控制：过渡动画、动画锁、序列计时器
    ///   AlbumArtControl.Render.cs       — 渲染绘制：Canvas_Draw、DrawFrame、GpuBake
    ///   AlbumArtControl.Utils.cs        — 工具 & Dispose：几何计算、哈希、释放
    /// </summary>
    [TemplatePart(Name = PartCanvas, Type = typeof(CanvasControl))]
    public sealed partial class AlbumArtControl : Control, IDisposable
    {
        // ── 布局与渲染常量 ────────────────────────────────────────────────────

        private const string PartCanvas = "canvas";
        private const float Margin = 20f;
        private const float CornerRadius = 16f;
        private const float ShadowPad = 34f;
        private const float HardMaxSize = 1280f;
        private const float FadeSpeed = 4f;
        private const float ScaleSmall = 0.90f;
        private const int ResizeDebounceMs = 20;

        /// <summary>哨兵值：表示"从未初始化"，区别于"上次为 null（-1）"。</summary>
        private const long NeverInitialized = long.MinValue;

        /// <summary>动画锁持续时间（毫秒），与 FadeSpeed 联动。</summary>
        private static readonly int AnimLockMs = (int)(1000f * 2 / FadeSpeed);

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
            float ContentW,
            float ContentH,
            bool Shadow,
            bool IsResize,
            bool IsDark);

        private readonly record struct FrameInfo(float SrcW, float SrcH, float Pad);

        // ── Pipeline 通道 ─────────────────────────────────────────────────────

        /// <summary>
        /// 解码完成后送往 UI 线程的有界通道（容量 1，DropOldest）。
        /// </summary>
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

        private Windows.Foundation.Rect _contentRect;
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
        private Size _desiredSize = new Size(200, 200); // fallback
        private float _aspectRatio = 1f; // W / H

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

        protected override Size MeasureOverride(Size availableSize)
        {
            double width = _desiredSize.Width;
            double height = _desiredSize.Height;

            if (!double.IsInfinity(availableSize.Width))
            {
                width = availableSize.Width;
                height = width / _aspectRatio;
            }

            if (!double.IsInfinity(availableSize.Height))
            {
                height = Math.Min(height, availableSize.Height);
                width = height * _aspectRatio;
            }

            return new Size(width, height);
        }
    }
}