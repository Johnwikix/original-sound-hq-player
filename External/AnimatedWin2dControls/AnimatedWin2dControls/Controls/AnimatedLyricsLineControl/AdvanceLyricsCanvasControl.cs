using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;

namespace AnimatedWin2dControls.Controls.AnimatedLyricsLineControl
{
    /// <summary>
    /// 独立高级歌词控件：自持一个 <see cref="CanvasAnimatedControl"/>，渲染逻辑委托给
    /// <see cref="LyricsRenderCoordinator"/>（与合成宿主共享同一套逻辑）。
    /// </summary>
    public sealed class AdvanceLyricsCanvasControl : Control
    {
        public event EventHandler<TimeSpan>? LyricLineClicked;
        public event EventHandler<Exception>? RenderError;

        private CanvasAnimatedControl? _canvas;
        private readonly LyricsRenderCoordinator _coordinator = new();

        private bool _pausedByVisibility;
        private bool _pausedByParent;
        private bool _pausedByWindow;
        private long _visibilityCallbackToken;
        private bool _shutdown;

        public AdvanceLyricsCanvasControl()
        {
            DefaultStyleKey = typeof(AdvanceLyricsCanvasControl);
            SizeChanged += OnControlSizeChanged;
            Loaded += OnControlLoaded;
            Unloaded += OnControlUnloaded;

            _coordinator.LyricLineClicked += (_, ts) => LyricLineClicked?.Invoke(this, ts);
            _coordinator.RenderError += (_, ex) => RenderError?.Invoke(this, ex);
        }

        public void PrepareForShutdown()
        {
            if (_shutdown) return;
            _shutdown = true;

            UnregisterPropertyChangedCallback(VisibilityProperty, _visibilityCallbackToken);

            if (_canvas != null)
            {
                _canvas.Paused = true;
                DetachCanvasEvents(_canvas);
                _canvas = null;
            }

            _coordinator.PrepareForShutdown();
        }

        public void PauseRendering()
        {
            _pausedByParent = true;
            UpdateCanvasPaused();
        }

        public void ResumeRendering()
        {
            _pausedByParent = false;
            _coordinator.ResetTimeSync();
            UpdateCanvasPaused();
        }

        public void SetWindowPaused(bool paused)
        {
            _pausedByWindow = paused;
            UpdateCanvasPaused();
        }

        private void UpdateCanvasPaused()
        {
            if (_canvas is not null)
                _canvas.Paused = _pausedByVisibility || _pausedByParent || _pausedByWindow;
        }

        private static void OnVisibilityChanged(DependencyObject d, DependencyProperty dp)
        {
            var ctrl = (AdvanceLyricsCanvasControl)d;
            ctrl._pausedByVisibility = ctrl.Visibility != Visibility.Visible;
            ctrl.UpdateCanvasPaused();
        }

        private void OnControlLoaded(object sender, RoutedEventArgs e)
        {
            _coordinator.Attach();
        }

        private void OnControlUnloaded(object sender, RoutedEventArgs e)
        {
            PrepareForShutdown();
        }

        protected override void OnApplyTemplate()
        {
            if (_canvas != null)
                DetachCanvasEvents(_canvas);

            _canvas = GetTemplateChild("PART_Canvas") as CanvasAnimatedControl;

            if (_canvas != null)
            {
                _coordinator.Canvas = _canvas;
                AttachCanvasEvents(_canvas);
                _canvas.TargetElapsedTime = TimeSpan.FromMilliseconds(1000.0 / _coordinator.TargetFrameRate);
                UpdateCanvasPaused();
            }

            _visibilityCallbackToken = RegisterPropertyChangedCallback(VisibilityProperty, OnVisibilityChanged);
        }

        private void AttachCanvasEvents(CanvasAnimatedControl canvas)
        {
            canvas.Update += OnCanvasUpdate;
            canvas.Draw += OnCanvasDraw;
            canvas.CreateResources += OnCanvasCreateResources;
            canvas.PointerWheelChanged += OnCanvasPointerWheelChanged;
            canvas.PointerPressed += OnCanvasPointerPressed;
            canvas.PointerMoved += OnCanvasPointerMoved;
            canvas.PointerReleased += OnCanvasPointerReleased;
            canvas.PointerCanceled += OnCanvasPointerCanceled;
            canvas.PointerExited += OnCanvasPointerExited;
            canvas.PointerEntered += OnCanvasPointerEntered;
            canvas.Tapped += OnCanvasTapped;
        }

        private void DetachCanvasEvents(CanvasAnimatedControl canvas)
        {
            canvas.Update -= OnCanvasUpdate;
            canvas.Draw -= OnCanvasDraw;
            canvas.CreateResources -= OnCanvasCreateResources;
            canvas.PointerWheelChanged -= OnCanvasPointerWheelChanged;
            canvas.PointerPressed -= OnCanvasPointerPressed;
            canvas.PointerMoved -= OnCanvasPointerMoved;
            canvas.PointerReleased -= OnCanvasPointerReleased;
            canvas.PointerCanceled -= OnCanvasPointerCanceled;
            canvas.PointerExited -= OnCanvasPointerExited;
            canvas.PointerEntered -= OnCanvasPointerEntered;
            canvas.Tapped -= OnCanvasTapped;
        }

        private void OnCanvasCreateResources(CanvasAnimatedControl sender, Microsoft.Graphics.Canvas.UI.CanvasCreateResourcesEventArgs args)
            => _coordinator.OnCreateResources();

        private void OnCanvasUpdate(ICanvasAnimatedControl sender, CanvasAnimatedUpdateEventArgs args)
            => _coordinator.OnUpdate(sender, args);

        private void OnCanvasDraw(ICanvasAnimatedControl sender, CanvasAnimatedDrawEventArgs args)
        {
            var ds = args.DrawingSession;
            ds.Clear(Microsoft.UI.Colors.Transparent);
            _coordinator.OnDraw(sender, ds);
        }

        private void OnControlSizeChanged(object sender, SizeChangedEventArgs e) => _coordinator.OnSizeChanged();

        private void OnCanvasPointerWheelChanged(object sender, PointerRoutedEventArgs e) => _coordinator.OnPointerWheelChanged(sender, e);
        private void OnCanvasPointerPressed(object sender, PointerRoutedEventArgs e) => _coordinator.OnPointerPressed(sender, e);
        private void OnCanvasPointerMoved(object sender, PointerRoutedEventArgs e) => _coordinator.OnPointerMoved(sender, e);
        private void OnCanvasPointerReleased(object sender, PointerRoutedEventArgs e) => _coordinator.OnPointerReleased(sender, e);
        private void OnCanvasPointerCanceled(object sender, PointerRoutedEventArgs e) => _coordinator.OnPointerCanceled(sender, e);
        private void OnCanvasPointerExited(object sender, PointerRoutedEventArgs e) => _coordinator.OnPointerExited(sender, e);
        private void OnCanvasPointerEntered(object sender, PointerRoutedEventArgs e) => _coordinator.OnPointerEntered(sender, e);
        private void OnCanvasTapped(object sender, TappedRoutedEventArgs e) => _coordinator.OnTapped(sender, e);
    }
}
