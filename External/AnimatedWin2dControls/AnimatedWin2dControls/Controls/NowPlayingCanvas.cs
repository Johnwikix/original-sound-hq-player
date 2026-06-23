using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl;
using AnimatedWin2dControls.Renderer;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using System.Runtime.CompilerServices;
using Windows.Foundation;

namespace AnimatedWin2dControls.Controls
{
    /// <summary>
    /// 合成宿主：单一 <see cref="CanvasAnimatedControl"/> 在同一 DrawingSession 内依次绘制
    /// 流体/雪/雾/雨背景栈与高级歌词，根治多交换链合成竞争导致的掉帧。
    /// </summary>
    [TemplatePart(Name = PartCanvasName, Type = typeof(CanvasAnimatedControl))]
    public sealed class NowPlayingCanvas : Control, IDisposable
    {
        private const string PartCanvasName = "PART_Canvas";

        // ── 背景侧依赖属性 ────────────────────────────────────────────────────

        public static readonly DependencyProperty IsFluidBackgroundEnabledProperty =
            DependencyProperty.Register(nameof(IsFluidBackgroundEnabled), typeof(bool),
                typeof(NowPlayingCanvas), new PropertyMetadata(true, OnEnableFlagChanged));
        public bool IsFluidBackgroundEnabled
        {
            get => (bool)GetValue(IsFluidBackgroundEnabledProperty);
            set => SetValue(IsFluidBackgroundEnabledProperty, value);
        }

        public static readonly DependencyProperty EnableLightWaveProperty =
            DependencyProperty.Register(nameof(EnableLightWave), typeof(bool),
                typeof(NowPlayingCanvas), new PropertyMetadata(false, OnFluidParamChanged));
        public bool EnableLightWave
        {
            get => (bool)GetValue(EnableLightWaveProperty);
            set => SetValue(EnableLightWaveProperty, value);
        }

        public static readonly DependencyProperty UseImageDominantThemeProperty =
            DependencyProperty.Register(nameof(UseImageDominantTheme), typeof(bool),
                typeof(NowPlayingCanvas), new PropertyMetadata(false, OnFluidParamChanged));
        public bool UseImageDominantTheme
        {
            get => (bool)GetValue(UseImageDominantThemeProperty);
            set => SetValue(UseImageDominantThemeProperty, value);
        }

        public static readonly DependencyProperty IsDarkProperty =
            DependencyProperty.Register(nameof(IsDark), typeof(bool),
                typeof(NowPlayingCanvas), new PropertyMetadata(true, OnFluidParamChanged));
        public bool IsDark
        {
            get => (bool)GetValue(IsDarkProperty);
            set => SetValue(IsDarkProperty, value);
        }

        public static readonly DependencyProperty IsFogEnabledProperty =
            DependencyProperty.Register(nameof(IsFogEnabled), typeof(bool),
                typeof(NowPlayingCanvas), new PropertyMetadata(false, OnEnableFlagChanged));
        public bool IsFogEnabled
        {
            get => (bool)GetValue(IsFogEnabledProperty);
            set => SetValue(IsFogEnabledProperty, value);
        }

        public static readonly DependencyProperty IsSnowEnabledProperty =
            DependencyProperty.Register(nameof(IsSnowEnabled), typeof(bool),
                typeof(NowPlayingCanvas), new PropertyMetadata(false, OnEnableFlagChanged));
        public bool IsSnowEnabled
        {
            get => (bool)GetValue(IsSnowEnabledProperty);
            set => SetValue(IsSnowEnabledProperty, value);
        }

        public static readonly DependencyProperty IsRaindropEnabledProperty =
            DependencyProperty.Register(nameof(IsRaindropEnabled), typeof(bool),
                typeof(NowPlayingCanvas), new PropertyMetadata(false, OnEnableFlagChanged));
        public bool IsRaindropEnabled
        {
            get => (bool)GetValue(IsRaindropEnabledProperty);
            set => SetValue(IsRaindropEnabledProperty, value);
        }

        // ── 歌词侧依赖属性 ────────────────────────────────────────────────────

        public static readonly DependencyProperty EnableAdvancedLyricsProperty =
            DependencyProperty.Register(nameof(EnableAdvancedLyrics), typeof(bool),
                typeof(NowPlayingCanvas), new PropertyMetadata(true));
        public bool EnableAdvancedLyrics
        {
            get => (bool)GetValue(EnableAdvancedLyricsProperty);
            set => SetValue(EnableAdvancedLyricsProperty, value);
        }

        public static readonly DependencyProperty LyricsRegionProperty =
            DependencyProperty.Register(nameof(LyricsRegion), typeof(Rect),
                typeof(NowPlayingCanvas), new PropertyMetadata(new Rect(), OnLyricsRegionChanged));
        public Rect LyricsRegion
        {
            get => (Rect)GetValue(LyricsRegionProperty);
            set => SetValue(LyricsRegionProperty, value);
        }

        // ── 事件 ──────────────────────────────────────────────────────────────

        public event EventHandler<bool>? ThemeResolved;
        public event EventHandler<Exception>? ExceptionOccurred;
        public event EventHandler<TimeSpan>? LyricLineClicked;
        public event EventHandler<Exception>? RenderError;

        // ── 私有字段 ──────────────────────────────────────────────────────────

        private CanvasAnimatedControl? _canvas;
        private readonly FluidBackgroundRenderer _fluid = new();
        private readonly FogRenderer _fog = new();
        private readonly SnowRenderer _snow = new();
        private readonly RaindropRenderer _raindrop = new();
        private readonly LyricsRenderCoordinator _coordinator = new();

        private bool _advanced = true;
        private bool _pausedByVisibility;

        // 渲染线程热路径只读这些缓存字段（DP 仅能在 UI 线程访问，跨线程读会抛 COMException）
        private bool _isFluidEnabled = true;
        private bool _isFogEnabled;
        private bool _isSnowEnabled;
        private bool _isRaindropEnabled;
        private bool _enableLightWave;
        private bool _isDark = true;
        private bool _useImageDominantTheme;

        private bool _pausedByParent;
        private bool _pausedByWindow;
        private long _visibilityCallbackToken;

        public NowPlayingCanvas()
        {
            DefaultStyleKey = typeof(NowPlayingCanvas);
            Loaded += OnControlLoaded;
            Unloaded += OnControlUnloaded;

            _coordinator.LyricLineClicked += (_, ts) => LyricLineClicked?.Invoke(this, ts);
            _coordinator.RenderError += (_, ex) => RenderError?.Invoke(this, ex);
        }

        // ── DP 回调 ──────────────────────────────────────────────────────────

        private static void OnFluidParamChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var ctrl = (NowPlayingCanvas)d;
            ctrl.SyncStateFromProperties();
            ctrl._fluid.RefreshColors();
        }

        private static void OnEnableFlagChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((NowPlayingCanvas)d).SyncStateFromProperties();
        }

        /// <summary>仅在 UI 线程调用：把依赖属性读入普通字段并下推到各渲染模块。</summary>
        private void SyncStateFromProperties()
        {
            _isFluidEnabled = IsFluidBackgroundEnabled;
            _isFogEnabled = IsFogEnabled;
            _isSnowEnabled = IsSnowEnabled;
            _isRaindropEnabled = IsRaindropEnabled;
            _enableLightWave = EnableLightWave;
            _isDark = IsDark;
            _useImageDominantTheme = UseImageDominantTheme;

            _fluid.IsEnabled = _isFluidEnabled;
            _fluid.EnableLightWave = _enableLightWave;
            _fluid.IsDark = _isDark;
            _fluid.UseImageDominantTheme = _useImageDominantTheme;
            _fog.IsEnabled = _isFogEnabled;
            _snow.IsEnabled = _isSnowEnabled;
            _raindrop.IsEnabled = _isRaindropEnabled;
        }

        private static void OnLyricsRegionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var ctrl = (NowPlayingCanvas)d;
            ctrl._coordinator.LyricsRegion = (Rect)e.NewValue;
            ctrl._coordinator.OnSizeChanged();
        }

        // ── 模板 ──────────────────────────────────────────────────────────────

        protected override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            DetachCanvasEvents();

            _advanced = EnableAdvancedLyrics;
            SyncStateFromProperties();
            _canvas = GetTemplateChild(PartCanvasName) as CanvasAnimatedControl;

            if (_canvas != null)
            {
                _coordinator.Canvas = _canvas;
                _coordinator.LyricsRegion = LyricsRegion;

                // 显式确保透明（不依赖模板 XAML，且设备重建后亦由 CreateResources 重申）
                _canvas.ClearColor = Colors.Transparent;

                _canvas.CreateResources += OnCanvasCreateResources;
                _canvas.Update += OnCanvasUpdate;
                _canvas.Draw += OnCanvasDraw;

                if (_advanced)
                {
                    _canvas.PointerWheelChanged += OnCanvasPointerWheelChanged;
                    _canvas.PointerPressed += OnCanvasPointerPressed;
                    _canvas.PointerMoved += OnCanvasPointerMoved;
                    _canvas.PointerReleased += OnCanvasPointerReleased;
                    _canvas.PointerCanceled += OnCanvasPointerCanceled;
                    _canvas.PointerExited += OnCanvasPointerExited;
                    _canvas.PointerEntered += OnCanvasPointerEntered;
                    _canvas.Tapped += OnCanvasTapped;
                    _canvas.TargetElapsedTime = TimeSpan.FromMilliseconds(1000.0 / _coordinator.TargetFrameRate);
                }

                // 简单歌词模式：宿主对指针透明，让上方 XAML 歌词覆层接管输入
                _canvas.IsHitTestVisible = _advanced;
                IsHitTestVisible = _advanced;

                UpdateCanvasPaused();
            }

            _visibilityCallbackToken = RegisterPropertyChangedCallback(VisibilityProperty, OnVisibilityChanged);
        }

        private void DetachCanvasEvents()
        {
            if (_canvas is null) return;
            _canvas.CreateResources -= OnCanvasCreateResources;
            _canvas.Update -= OnCanvasUpdate;
            _canvas.Draw -= OnCanvasDraw;
            _canvas.PointerWheelChanged -= OnCanvasPointerWheelChanged;
            _canvas.PointerPressed -= OnCanvasPointerPressed;
            _canvas.PointerMoved -= OnCanvasPointerMoved;
            _canvas.PointerReleased -= OnCanvasPointerReleased;
            _canvas.PointerCanceled -= OnCanvasPointerCanceled;
            _canvas.PointerExited -= OnCanvasPointerExited;
            _canvas.PointerEntered -= OnCanvasPointerEntered;
            _canvas.Tapped -= OnCanvasTapped;
        }

        private void OnControlLoaded(object sender, RoutedEventArgs e)
        {
            if (_advanced)
                _coordinator.Attach();
        }

        private void OnControlUnloaded(object sender, RoutedEventArgs e)
        {
            PrepareForShutdown();
        }

        // ── 暂停管理 ──────────────────────────────────────────────────────────

        public void PauseRendering() { _pausedByParent = true; UpdateCanvasPaused(); }
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
            var ctrl = (NowPlayingCanvas)d;
            ctrl._pausedByVisibility = ctrl.Visibility != Visibility.Visible;
            ctrl.UpdateCanvasPaused();
        }

        // ── 资源 / 热路径 ─────────────────────────────────────────────────────

        private void OnCanvasCreateResources(CanvasAnimatedControl sender, CanvasCreateResourcesEventArgs args)
        {
            try
            {
                // 设备重建（含设备丢失/恢复）时重申透明，保证交换链以透明 alpha 模式重配
                sender.ClearColor = Colors.Transparent;

                _fluid.EnableLightWave = _enableLightWave;
                _fluid.IsDark = _isDark;
                _fluid.UseImageDominantTheme = _useImageDominantTheme;

                _fluid.LoadResources();
                _fog.LoadResources();
                _snow.LoadResources();
                _raindrop.LoadResources();

                if (_advanced)
                    _coordinator.OnCreateResources();
            }
            catch (Exception ex) { RaiseException(ex); }
        }

        private void OnCanvasUpdate(ICanvasAnimatedControl sender, CanvasAnimatedUpdateEventArgs args)
        {
            var elapsed = args.Timing.ElapsedTime;

            // 渲染线程：只读已缓存到渲染模块的状态，绝不访问依赖属性
            _fluid.Update(elapsed);
            _fog.Update(elapsed.TotalSeconds);
            _snow.Update(elapsed.TotalSeconds);
            _raindrop.Update(elapsed.TotalSeconds);

            if (_advanced)
                _coordinator.OnUpdate(sender, args);
        }

        private void OnCanvasDraw(ICanvasAnimatedControl sender, CanvasAnimatedDrawEventArgs args)
        {
            var ds = args.DrawingSession;

            // 不手动清屏：依赖 ClearColor=Transparent 由 Win2D 在每帧前清屏并维护透明交换链。
            // 流体开启时由满屏不透明着色器覆盖；关闭时透出页面/窗口背景。
            _fluid.Draw(sender, ds);
            _snow.Draw(sender, ds);
            _fog.Draw(sender, ds);
            _raindrop.Draw(sender, ds);

            if (_advanced)
                _coordinator.OnDraw(sender, ds);
        }

        // ── 输入转发 ──────────────────────────────────────────────────────────

        private void OnCanvasPointerWheelChanged(object sender, PointerRoutedEventArgs e) => _coordinator.OnPointerWheelChanged(sender, e);
        private void OnCanvasPointerPressed(object sender, PointerRoutedEventArgs e) => _coordinator.OnPointerPressed(sender, e);
        private void OnCanvasPointerMoved(object sender, PointerRoutedEventArgs e) => _coordinator.OnPointerMoved(sender, e);
        private void OnCanvasPointerReleased(object sender, PointerRoutedEventArgs e) => _coordinator.OnPointerReleased(sender, e);
        private void OnCanvasPointerCanceled(object sender, PointerRoutedEventArgs e) => _coordinator.OnPointerCanceled(sender, e);
        private void OnCanvasPointerExited(object sender, PointerRoutedEventArgs e) => _coordinator.OnPointerExited(sender, e);
        private void OnCanvasPointerEntered(object sender, PointerRoutedEventArgs e) => _coordinator.OnPointerEntered(sender, e);
        private void OnCanvasTapped(object sender, TappedRoutedEventArgs e) => _coordinator.OnTapped(sender, e);

        // ── 调色板 ────────────────────────────────────────────────────────────

        public void SetPalette(AnimatedWin2dControls.Impressionist.PaletteResult? palette)
        {
            try
            {
                _fluid.SetPalette(palette);
                if (UseImageDominantTheme && palette is not null)
                    ThemeResolved?.Invoke(this, palette.PaletteIsDark);
            }
            catch (Exception ex) { RaiseException(ex); }
        }

        // ── 释放 ──────────────────────────────────────────────────────────────

        public void PrepareForShutdown()
        {
            if (_canvas is not null)
                _canvas.Paused = true;
            UnregisterPropertyChangedCallback(VisibilityProperty, _visibilityCallbackToken);
            DetachCanvasEvents();
            _canvas = null;

            _coordinator.PrepareForShutdown();
            _fluid.Dispose();
            _fog.Dispose();
            _snow.Dispose();
            _raindrop.Dispose();
        }

        public void Dispose()
        {
            PrepareForShutdown();
            ThemeResolved = null;
            ExceptionOccurred = null;
            LyricLineClicked = null;
            RenderError = null;
            GC.SuppressFinalize(this);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void RaiseException(Exception ex)
        {
            try { ExceptionOccurred?.Invoke(this, ex); }
            catch { }
        }
    }
}
