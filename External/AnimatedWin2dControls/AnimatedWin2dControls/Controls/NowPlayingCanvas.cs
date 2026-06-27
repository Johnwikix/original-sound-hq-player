using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl;
using AnimatedWin2dControls.Renderer;
using AnimatedWin2dControls.Renderer.Background;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using System.Runtime.CompilerServices;
using System.Threading;
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

        public static readonly DependencyProperty BackgroundShaderIndexProperty =
            DependencyProperty.Register(nameof(BackgroundShaderIndex), typeof(int),
                typeof(NowPlayingCanvas), new PropertyMetadata(0, OnBackgroundShaderIndexChanged));
        public int BackgroundShaderIndex
        {
            get => (int)GetValue(BackgroundShaderIndexProperty);
            set => SetValue(BackgroundShaderIndexProperty, value);
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
        // 急实例化以保证 SetPalette 在 LoadResources 之前被调用时也能落到实例上 (与原 FluidBackgroundRenderer 行为一致)。
        // volatile: 渲染线程在 OnCanvasUpdate/Draw 中以快照方式读取,UI 线程在 SwapBackgroundRenderer 中整体替换。
        private volatile BaseBackgroundRenderer? _background = CreateBackgroundRenderer(0);
        // 缓存最近一次调色板，shader 切换 / 设备重建后用于把新 renderer 重新染上当前调色板。
        private AnimatedWin2dControls.Impressionist.PaletteResult? _lastPalette;
        private readonly FogRenderer _fog = new();
        private readonly SnowRenderer _snow = new();
        private readonly RaindropRenderer _raindrop = new();
        private readonly LyricsRenderCoordinator _coordinator = new();

        private bool _advanced = true;
        private bool _pausedByVisibility;

        // 背景半帧率：每 (skip+1) 帧才重绘一次不透明合成缓存，其余帧复用。0=全帧率，1=半帧率。
        private int _backgroundFrameSkip = 1;
        private long _frameCounter;
        // 保护 _bgCache 生命周期:Swap/OnCanvasCreateResources/PrepareForShutdown 持锁,DrawBackground 持锁完成 check+create+render+DrawImage。
        private readonly object _cacheGate = new();
        private CanvasRenderTarget? _bgCache;
        private float _bgCacheWidthDip;
        private float _bgCacheHeightDip;

        // 渲染线程热路径只读这些缓存字段（DP 仅能在 UI 线程访问，跨线程读会抛 COMException）
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
            ctrl._background?.RefreshColors();
        }

        private static void OnEnableFlagChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((NowPlayingCanvas)d).SyncStateFromProperties();
        }

        private static void OnBackgroundShaderIndexChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var ctrl = (NowPlayingCanvas)d;
            ctrl.SwapBackgroundRenderer();
        }

        private void SwapBackgroundRenderer()
        {
            bool wasCanvasPaused = _canvas?.Paused ?? true;
            if (_canvas is not null)
                _canvas.Paused = true;

            try
            {
                // 持锁 dispose 缓存,等渲染线程退栈后再释放,杜绝 _bgCache.Dispose 与 ds.DrawImage 并发
                lock (_cacheGate)
                {
                    _bgCache?.Dispose();
                    _bgCache = null;
                }

                // null-first: 渲染线程下一帧看到 null 直接跳过旧 renderer,杜绝 _effect.Dispose 与 Draw 并发
                var oldBg = _background;
                _background = null;
                oldBg?.Dispose();

                var newBg = CreateBackgroundRenderer(BackgroundShaderIndex);
                _background = newBg;
                SyncStateFromProperties();
                if (_lastPalette is not null) newBg.SetPalette(_lastPalette);
                else newBg.RefreshColors();
                newBg.LoadResources();
            }
            finally
            {
                if (_canvas is not null)
                    _canvas.Paused = wasCanvasPaused
                        || _pausedByVisibility || _pausedByParent || _pausedByWindow;
            }
        }

        private static BaseBackgroundRenderer CreateBackgroundRenderer(int index) => index switch
        {
            1 => new PS3XMBBackgroundRenderer(),
            2 => new GradientFlowBackgroundRenderer(),
            3 => new WavyBackgroundRenderer(),
            _ => new FluidBackgroundRenderer(),
        };

        /// <summary>仅在 UI 线程调用：把依赖属性读入普通字段并下推到各渲染模块。</summary>
        private void SyncStateFromProperties()
        {
            _isFogEnabled = IsFogEnabled;
            _isSnowEnabled = IsSnowEnabled;
            _isRaindropEnabled = IsRaindropEnabled;
            _enableLightWave = EnableLightWave;
            _isDark = IsDark;
            _useImageDominantTheme = UseImageDominantTheme;

            if (_background is not null)
            {
                _background.EnableLightWave = _enableLightWave;
                _background.IsDark = _isDark;
                _background.UseImageDominantTheme = _useImageDominantTheme;
            }
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

                _canvas.CreateResources += OnCanvasCreateResources;
                _canvas.Update += OnCanvasUpdate;
                _canvas.Draw += OnCanvasDraw;

                if (_advanced)
                {
                    _canvas.PointerWheelChanged += OnCanvasPointerWheelChanged;
                    _canvas.PointerMoved += OnCanvasPointerMoved;
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
            _canvas.PointerMoved -= OnCanvasPointerMoved;
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
                // 设备重建（含设备丢失/恢复）：丢弃合成缓存，由下一帧按当前设备/尺寸重建
                lock (_cacheGate)
                {
                    _bgCache?.Dispose();
                    _bgCache = null;
                }

                if (_background == null) _background = CreateBackgroundRenderer(BackgroundShaderIndex);
                _background!.EnableLightWave = _enableLightWave;
                _background.IsDark = _isDark;
                _background.UseImageDominantTheme = _useImageDominantTheme;

                _background.LoadResources();
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
            var bg = _background;
            var elapsed = args.Timing.ElapsedTime;

            // 渲染线程：只读已缓存到渲染模块的状态，绝不访问依赖属性
            bg?.Update(elapsed);
            _fog.Update(elapsed.TotalSeconds);
            _snow.Update(elapsed.TotalSeconds);
            _raindrop.Update(elapsed.TotalSeconds);

            if (_advanced)
                _coordinator.OnUpdate(sender, args);
        }

        private void OnCanvasDraw(ICanvasAnimatedControl sender, CanvasAnimatedDrawEventArgs args)
        {
            var ds = args.DrawingSession;

            DrawBackground(sender, ds);

            if (_advanced)
                _coordinator.OnDraw(sender, ds);
        }

        // 背景以 (skip+1) 帧为周期重绘到不透明合成缓存，其余帧复用缓存 blit。
        // 流体恒为满屏不透明，缓存整面覆盖交换链，故无需清屏；歌词层每帧叠加其上。
        private void DrawBackground(ICanvasAnimatedControl sender, CanvasDrawingSession ds)
        {
            var bg = _background;
            _frameCounter++;

            if (_backgroundFrameSkip <= 0)
            {
                bg?.Draw(sender, ds);
                _snow.Draw(sender, ds);
                _fog.Draw(sender, ds);
                _raindrop.Draw(sender, ds);
                return;
            }

            float widthDip = (float)sender.Size.Width;
            float heightDip = (float)sender.Size.Height;
            if (widthDip <= 0f || heightDip <= 0f) return;

            bool renderBg = _frameCounter % (_backgroundFrameSkip + 1) == 0;

            // 持锁贯穿 check+create+render+DrawImage,杜绝 UI 线程在 DrawImage 前插入 _bgCache.Dispose。
            // 拿不到锁就跳过本帧(Swap 间隙毫秒级),下一帧会重建。
            if (!Monitor.TryEnter(_cacheGate, 0)) return;
            try
            {
                if (_bgCache is null || _bgCacheWidthDip != widthDip || _bgCacheHeightDip != heightDip)
                {
                    _bgCache?.Dispose();
                    _bgCache = new CanvasRenderTarget(sender, widthDip, heightDip);
                    _bgCacheWidthDip = widthDip;
                    _bgCacheHeightDip = heightDip;
                    renderBg = true; // 尺寸变化后必须立即重绘缓存
                }

                if (renderBg)
                {
                    using var cds = _bgCache!.CreateDrawingSession();
                    bg?.Draw(sender, cds);
                    _snow.Draw(sender, cds);
                    _fog.Draw(sender, cds);
                    _raindrop.Draw(sender, cds);
                }

                ds.DrawImage(_bgCache);
            }
            finally { Monitor.Exit(_cacheGate); }
        }

        // ── 输入转发 ──────────────────────────────────────────────────────────

        private void OnCanvasPointerWheelChanged(object sender, PointerRoutedEventArgs e) => _coordinator.OnPointerWheelChanged(sender, e);
        private void OnCanvasPointerMoved(object sender, PointerRoutedEventArgs e) => _coordinator.OnPointerMoved(sender, e);
        private void OnCanvasPointerExited(object sender, PointerRoutedEventArgs e) => _coordinator.OnPointerExited(sender, e);
        private void OnCanvasPointerEntered(object sender, PointerRoutedEventArgs e) => _coordinator.OnPointerEntered(sender, e);
        private void OnCanvasTapped(object sender, TappedRoutedEventArgs e) => _coordinator.OnTapped(sender, e);

        // ── 调色板 ────────────────────────────────────────────────────────────

        public void SetPalette(AnimatedWin2dControls.Impressionist.PaletteResult? palette)
        {
            try
            {
                _lastPalette = palette;
                _background?.SetPalette(palette);
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
            _background?.Dispose();
            _background = null;
            _lastPalette = null;
            _fog.Dispose();
            _snow.Dispose();
            _raindrop.Dispose();

            lock (_cacheGate)
            {
                _bgCache?.Dispose();
                _bgCache = null;
            }
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
