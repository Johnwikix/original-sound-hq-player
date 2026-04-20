using AnimatedWin2dControls.Controls.AlbumImgControl;
using AnimatedWin2dControls.Utils;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Threading.Tasks;
using Windows.Foundation;

namespace AnimatedWin2dControls.Controls.AlbumImgControl
{
    [TemplatePart(Name = PartCanvas, Type = typeof(CanvasControl))]
    public sealed class AlbumArtControl : Control, IDisposable, ISharedTickable
    {
        private const string PartCanvas = "canvas";

        private readonly BitmapLoader _loader = new();
        private readonly TransitionState _transition = new();
        private readonly BakedRTCache _cache = new();

        private CanvasControl? _canvas;
        private bool _isResourcesCreated;
        private bool _isClockRegistered;
        private bool _disposed;

        // ── 构造 ──────────────────────────────────────────────────────────────

        public AlbumArtControl()
        {
            DefaultStyleKey = typeof(AlbumArtControl);

            // 还原原始 StartTransition 中"incoming → current"时对 cache 的同步
            _transition.IncomingPromotedToCurrent += () =>
            {
                // 更新起始矩形供下一次过渡插值使用
                _transition.CurrentDestRectAtStart = GetCurrentDestRect();
                // cache 同步翻转（与原始 _currentBaked = _incomingBaked 对应）
                _cache.PromoteIncomingToCurrent();
            };

            Unloaded += (_, _) => Dispose(true);
        }

        // ── 模板 ──────────────────────────────────────────────────────────────

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

        private static void OnLayoutChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var ctrl = (AlbumArtControl)d;
            if (!ctrl._isResourcesCreated || !ctrl.IsActive) return;
            // mask 尺寸/形状变化：清空 baked RT，下一帧 EnsureMask 会因参数不同自动重建
            ctrl._cache.InvalidateBaked();
            ctrl._canvas?.Invalidate();
        }

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
                _ = ctrl.LoadAndEnqueueAsync(ctrl.ImageBytes);
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

            // 还原原始 EnsureMaskRenderTarget + _rtInvalidated 逻辑：
            // EnsureMask 检测参数变化 → 清空 baked RT → 回调触发重烘焙
            _cache.EnsureMask(
                sender.Device, contentW, contentH, radius, dpi,
                _transition.CurrentBitmap, _transition.IncomingBitmap,
                IsShadowEnabled,
                onNeedRebake: (bmp, w, h) =>
                {
                    if (_transition.IsStillCurrent(bmp))
                        _ = BakeCurrentAsync(bmp, w, h);
                    else if (_transition.IsStillIncoming(bmp))
                        _ = BakeIncomingAsync(bmp, w, h);
                });

            DrawingRenderer.Draw(
                e.DrawingSession, _transition, _cache,
                cw, ch, padT, padB, padL, padR);
        }

        // ── 加载 ─────────────────────────────────────────────────────────────

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
                try { bmp = await _loader.LoadDefaultAsync(IsDark, _canvas, ct); }
                catch { return; }
            }
            if (bmp == null) return;

            // 还原原始 EnqueueBitmap 之前记录起始矩形的时机
            _transition.CurrentDestRectAtStart = GetCurrentDestRect();
            _transition.Enqueue(bmp);
            StartRenderingLoop();

            // 立即触发 incoming 的异步烘焙（对应原始 TryPreBakeIncomingAsync）
            var (w, h) = GetContentSize();
            if (w > 0 && h > 0)
                _ = BakeIncomingAsync(bmp, w, h);

            _canvas?.Invalidate();
        }

        // ── 烘焙 ─────────────────────────────────────────────────────────────

        private async Task BakeCurrentAsync(CanvasBitmap bitmap, float w, float h)
        {
            if (_canvas == null) return;
            await _cache.BakeCurrentAsync(
                bitmap, _canvas.Device, w, h, IsShadowEnabled, (float)DpiScale,
                isStillValid: () => _transition.IsStillCurrent(bitmap),
                onReady: () => _canvas?.Invalidate());
        }

        private async Task BakeIncomingAsync(CanvasBitmap bitmap, float w, float h)
        {
            if (_canvas == null) return;
            await _cache.BakeIncomingAsync(
                bitmap, _canvas.Device, w, h, IsShadowEnabled, (float)DpiScale,
                isStillValid: () => _transition.IsStillIncoming(bitmap),
                onReady: () => _canvas?.Invalidate());
        }

        // ── 时钟 ─────────────────────────────────────────────────────────────

        public void OnSharedTick(TimeSpan elapsed)
        {
            float delta = Math.Min((float)elapsed.TotalSeconds, 0.1f);
            bool still = _transition.Advance(delta);
            _canvas?.Invalidate();
            if (!still) StopRenderingLoop();
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
            var (w, h) = GetContentSize();
            if (w <= 0 || h <= 0) return Rect.Empty;
            return DrawingRenderer.CalcDestRect(
                _transition.CurrentBitmap,
                (float)MarginLeftRatio, (float)MarginTopRatio, w, h);
        }

        private (float w, float h) GetContentSize()
        {
            if (_canvas == null) return (0, 0);
            float padL = (float)MarginLeftRatio, padR = (float)MarginRightRatio;
            float padT = (float)MarginTopRatio, padB = (float)MarginBottomRatio;
            return ((float)_canvas.Size.Width - padL - padR,
                    (float)_canvas.Size.Height - padT - padB);
        }

        // ── Dispose ───────────────────────────────────────────────────────────

        public void Dispose() { Dispose(true); GC.SuppressFinalize(this); }

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
            _cache.Dispose();       // 先释放 GPU RT（依赖 mask）
            _transition.Dispose();  // 再释放位图
            _loader.Dispose();
        }
    }
}