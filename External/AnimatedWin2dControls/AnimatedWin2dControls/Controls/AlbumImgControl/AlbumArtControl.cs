using AnimatedWin2dControls.Utils;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;

namespace AnimatedWin2dControls.Controls.AlbumImgControl
{
    /// <summary>
    /// 专辑封面显示控件（Win2D）。
    /// <para>
    /// 职责仅限于：
    /// <list type="bullet">
    ///   <item>声明 DependencyProperty；</item>
    ///   <item>响应属性变化并协调四个子系统（<see cref="BitmapLoader"/>、
    ///         <see cref="TransitionState"/>、<see cref="BakedRTCache"/>、<see cref="DrawingRenderer"/>）；</item>
    ///   <item>驱动 <see cref="SharedAnimationClock"/>。</item>
    /// </list>
    /// 所有 GPU 资源生命周期由 <see cref="BakedRTCache"/> 统一管理；
    /// 所有位图生命周期由 <see cref="TransitionState"/> 统一管理。
    /// </para>
    /// </summary>
    [TemplatePart(Name = PartCanvas, Type = typeof(CanvasControl))]
    public sealed class AlbumArtControl : Control, IDisposable, ISharedTickable
    {
        private const string PartCanvas = "canvas";

        // ── 子系统 ────────────────────────────────────────────────────────────

        private readonly BitmapLoader _loader = new();
        private readonly TransitionState _transition = new();
        private readonly BakedRTCache _cache = new();

        // ── Win2D canvas ──────────────────────────────────────────────────────

        private CanvasControl? _canvas;
        private bool _isResourcesCreated;
        private bool _isClockRegistered;
        private bool _disposed;

        // ── 构造 & 模板 ───────────────────────────────────────────────────────

        public AlbumArtControl()
        {
            DefaultStyleKey = typeof(AlbumArtControl);

            // incoming 提升为 current 时，同步更新 cache 并记录新的 current 起始矩形
            _transition.OnIncomingPromotedToCurrent += () =>
            {
                _transition.CurrentDestRectAtStart = GetCurrentDestRect();
                _cache.PromoteIncomingToCurrent();
            };

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

        // ── DependencyProperty ────────────────────────────────────────────────

        public static readonly DependencyProperty DpiScaleProperty =
            DependencyProperty.Register(nameof(DpiScale), typeof(double),
                typeof(AlbumArtControl), new PropertyMetadata(1.0, OnRebakeParamChanged));
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
                typeof(AlbumArtControl), new PropertyMetadata(true, OnRebakeParamChanged));
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

        // ── 属性变化处理 ──────────────────────────────────────────────────────

        private static void OnImageBytesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var ctrl = (AlbumArtControl)d;
            if (!ctrl._isResourcesCreated || !ctrl.IsActive) return;

            var newBytes = e.NewValue as byte[];
            if (ctrl._loader.IsDuplicate(newBytes)) return;

            _ = ctrl.LoadAndEnqueueAsync(newBytes);
        }

        private static void OnIsDarkChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var ctrl = (AlbumArtControl)d;
            if (!ctrl._isResourcesCreated || !ctrl.IsActive) return;
            ctrl._loader.InvalidateDedup();
            _ = ctrl.LoadAndEnqueueAsync(ctrl.ImageBytes);
        }

        /// <summary>
        /// 布局参数（margin / corner radius）变化：mask 尺寸或形状变化，
        /// 需要重建 mask 并重新烘焙两张 RT。
        /// 通过在下一帧 Draw 时调用 EnsureMask 来触发，不在此处直接操作 GPU 资源。
        /// </summary>
        private static void OnLayoutChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var ctrl = (AlbumArtControl)d;
            if (!ctrl._isResourcesCreated || !ctrl.IsActive) return;
            // 强制 EnsureMask 在下一帧重建（通过将 mask 参数标记为脏）
            ctrl._cache.InvalidateBaked(); // 清空旧 RT，下帧 EnsureMask 因 w/h/radius 不同会重建 mask
            ctrl._canvas?.Invalidate();
        }

        /// <summary>
        /// Shadow / DpiScale 变化：mask 本身不需要重建，只需重新烘焙 RT。
        /// </summary>
        private static void OnRebakeParamChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var ctrl = (AlbumArtControl)d;
            if (!ctrl._isResourcesCreated) return;
            ctrl._cache.InvalidateBaked();
            ctrl._canvas?.Invalidate();
        }

        private static void OnIsActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var ctrl = (AlbumArtControl)d;
            if (!ctrl._isResourcesCreated) return;

            if ((bool)e.NewValue)
            {
                _ = ctrl.LoadAndEnqueueAsync(ctrl.ImageBytes);
            }
            else
            {
                ctrl._loader.CancelCurrent();
                ctrl._canvas?.Invalidate();
            }
        }

        // ── Canvas 事件 ───────────────────────────────────────────────────────

        private void Canvas_CreateResources(CanvasControl sender,
            Microsoft.Graphics.Canvas.UI.CanvasCreateResourcesEventArgs e)
        {
            _isResourcesCreated = true;
            if (!IsActive) return;
            e.TrackAsyncAction(LoadAndEnqueueAsync(ImageBytes).AsAsyncAction());
        }

        private void Canvas_Draw(CanvasControl sender, CanvasDrawEventArgs e)
        {
            if (!IsActive) return;

            float cw = (float)sender.Size.Width;
            float ch = (float)sender.Size.Height;
            float padL = (float)MarginLeftRatio, padR = (float)MarginRightRatio;
            float padT = (float)MarginTopRatio, padB = (float)MarginBottomRatio;

            float contentW = cw - padL - padR;
            float contentH = ch - padT - padB;
            if (contentW <= 0 || contentH <= 0) return;

            float radius = (float)ArtCornerRadius;
            float dpi = 96f * (float)DpiScale;

            // EnsureMask 若检测到参数变化，会内部 InvalidateBaked + 触发 onNeedRebake
            _cache.EnsureMask(
                sender.Device, contentW, contentH, radius, dpi,
                _transition.CurrentBitmap, _transition.IncomingBitmap,
                IsShadowEnabled,
                onNeedRebake: (bmp, w, h) => TriggerRebake(bmp, w, h));

            DrawingRenderer.Draw(
                e.DrawingSession,
                _transition, _cache,
                cw, ch, padT, padB, padL, padR);
        }

        // ── 加载协调 ─────────────────────────────────────────────────────────

        private async Task LoadAndEnqueueAsync(byte[]? bytes)
        {
            if (_canvas == null) return;

            var ct = _loader.RenewCancellation();

            CanvasBitmap? bmp = null;
            try
            {
                bmp = (bytes is { Length: > 0 })
                    ? await _loader.LoadAsync(bytes, _canvas, ct)
                    : await _loader.LoadDefaultAsync(IsDark, _canvas, ct);
            }
            catch (OperationCanceledException) { return; }
            catch
            {
                _loader.InvalidateDedup();
                try
                {
                    bmp = await _loader.LoadDefaultAsync(IsDark, _canvas, ct);
                }
                catch { return; }
            }

            if (bmp == null) return;

            // 记录过渡起始矩形（在 UI 线程上，canvas 尺寸此时已知）
            _transition.CurrentDestRectAtStart = GetCurrentDestRect();

            _transition.Enqueue(bmp);
            StartRenderingLoop();

            // 立即触发异步烘焙（如果 canvas 尺寸已知）
            TriggerRebakeIncoming(bmp);
            _canvas?.Invalidate();
        }

        // ── 烘焙触发 ─────────────────────────────────────────────────────────

        /// <summary>
        /// 响应 EnsureMask 的 onNeedRebake 回调，判断是 current 还是 incoming 并触发对应烘焙。
        /// </summary>
        private void TriggerRebake(CanvasBitmap bmp, float w, float h)
        {
            if (ReferenceEquals(bmp, _transition.CurrentBitmap))
                _ = RebakeCurrentAsync(bmp, w, h);
            else if (ReferenceEquals(bmp, _transition.IncomingBitmap))
                _ = RebakeIncomingAsync(bmp, w, h);
        }

        private void TriggerRebakeIncoming(CanvasBitmap bmp)
        {
            var (w, h) = GetContentSize();
            if (w <= 0 || h <= 0) return;
            _ = RebakeIncomingAsync(bmp, w, h);
        }

        private async Task RebakeCurrentAsync(CanvasBitmap bitmap, float w, float h)
        {
            if (_canvas == null) return;
            await _cache.BakeCurrentAsync(
                bitmap, _canvas.Device, w, h, IsShadowEnabled, (float)DpiScale,
                isBitmapStillCurrent: () => _transition.IsCurrentStillPending(bitmap),
                onReady: () => _canvas?.Invalidate());
        }

        private async Task RebakeIncomingAsync(CanvasBitmap bitmap, float w, float h)
        {
            if (_canvas == null) return;
            await _cache.BakeIncomingAsync(
                bitmap, _canvas.Device, w, h, IsShadowEnabled, (float)DpiScale,
                isBitmapStillIncoming: () => _transition.IsIncomingStillPending(bitmap),
                onReady: () => _canvas?.Invalidate());
        }

        // ── SharedAnimationClock ──────────────────────────────────────────────

        public void OnSharedTick(TimeSpan elapsed)
        {
            float delta = Math.Min((float)elapsed.TotalSeconds, 0.1f);
            bool stillRunning = _transition.Advance(delta);
            _canvas?.Invalidate();

            if (!stillRunning)
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

        // ── 辅助 ─────────────────────────────────────────────────────────────

        private Rect GetCurrentDestRect()
        {
            if (_transition.CurrentBitmap == null || _canvas == null) return Rect.Empty;

            float cw = (float)_canvas.Size.Width;
            float ch = (float)_canvas.Size.Height;
            if (cw <= 0 || ch <= 0) return Rect.Empty;

            float padL = (float)MarginLeftRatio, padR = (float)MarginRightRatio;
            float padT = (float)MarginTopRatio, padB = (float)MarginBottomRatio;
            float contentW = cw - padL - padR;
            float contentH = ch - padT - padB;
            if (contentW <= 0 || contentH <= 0) return Rect.Empty;

            return DrawingRenderer.CalcDestRect(
                _transition.CurrentBitmap, padL, padT, contentW, contentH);
        }

        private (float w, float h) GetContentSize()
        {
            if (_canvas == null) return (0, 0);
            float cw = (float)_canvas.Size.Width;
            float ch = (float)_canvas.Size.Height;
            float padL = (float)MarginLeftRatio, padR = (float)MarginRightRatio;
            float padT = (float)MarginTopRatio, padB = (float)MarginBottomRatio;
            return (cw - padL - padR, ch - padT - padB);
        }

        // ── IDisposable ───────────────────────────────────────────────────────

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            if (!disposing || _disposed) return;
            _disposed = true;

            _loader.CancelCurrent();
            StopRenderingLoop();

            if (_canvas != null)
            {
                _canvas.CreateResources -= Canvas_CreateResources;
                _canvas.Draw -= Canvas_Draw;
                _canvas = null;
            }

            // 先 Dispose cache（GPU 资源），再 Dispose transition（位图）
            // 顺序很重要：cache 中的 RT 持有对 mask 的引用，mask 必须在 RT 之后才释放
            _cache.Dispose();
            _transition.Dispose();
            _loader.Dispose();
        }
    }
}