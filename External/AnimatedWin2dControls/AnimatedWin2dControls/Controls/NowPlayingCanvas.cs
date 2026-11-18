#nullable enable annotations
using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl;
using AnimatedWin2dControls.Renderer;
using AnimatedWin2dControls.Interop;
using AnimatedWin2dControls.Renderer.Background;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Serilog;
using System;
using System.Runtime.CompilerServices;
using System.Threading;
using Windows.Foundation;
using Windows.Graphics.DirectX;

namespace AnimatedWin2dControls.Controls
{
    /// <summary>
    /// 合成宿主：单一 <see cref="CanvasAnimatedControl"/> 在同一 DrawingSession 内依次绘制
    /// 流体/雪/雾/雨背景栈与高级歌词，根治多交换链合成竞争导致的掉帧。
    /// </summary>
    [TemplatePart(Name = PartCanvasName, Type = typeof(CanvasAnimatedControl))]
    [TemplatePart(Name = HdrPanelPart, Type = typeof(CanvasSwapChainPanel))]
    public sealed partial class NowPlayingCanvas : Control, IDisposable
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

        public static readonly DependencyProperty IsActiveProperty =
            DependencyProperty.Register(nameof(IsActive), typeof(bool),
                typeof(NowPlayingCanvas), new PropertyMetadata(true, OnActiveChanged));
        public bool IsActive
        {
            get => (bool)GetValue(IsActiveProperty);
            set => SetValue(IsActiveProperty, value);
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

        public static readonly DependencyProperty UseImageBackgroundProperty =
            DependencyProperty.Register(nameof(UseImageBackground), typeof(bool),
                typeof(NowPlayingCanvas), new PropertyMetadata(false, OnImageBackgroundChanged));
        public bool UseImageBackground
        {
            get => (bool)GetValue(UseImageBackgroundProperty);
            set => SetValue(UseImageBackgroundProperty, value);
        }

        public static readonly DependencyProperty BackgroundImagePathProperty =
            DependencyProperty.Register(nameof(BackgroundImagePath), typeof(string),
                typeof(NowPlayingCanvas), new PropertyMetadata(string.Empty, OnImageBackgroundChanged));
        public string BackgroundImagePath
        {
            get => (string)GetValue(BackgroundImagePathProperty);
            set => SetValue(BackgroundImagePathProperty, value);
        }

        public static readonly DependencyProperty BackgroundImageBlurAmountProperty =
            DependencyProperty.Register(nameof(BackgroundImageBlurAmount), typeof(double),
                typeof(NowPlayingCanvas), new PropertyMetadata(20d, OnImageBackgroundChanged));
        public double BackgroundImageBlurAmount
        {
            get => (double)GetValue(BackgroundImageBlurAmountProperty);
            set => SetValue(BackgroundImageBlurAmountProperty, value);
        }

        // ── 歌词侧依赖属性 ────────────────────────────────────────────────────

        public static readonly DependencyProperty EnableAdvancedLyricsProperty =
            DependencyProperty.Register(nameof(EnableAdvancedLyrics), typeof(bool),
                typeof(NowPlayingCanvas), new PropertyMetadata(true, OnAdvancedLyricsChanged));
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
        private FrameEventSubscription? _updateSubscription;
        private FrameEventSubscription? _drawSubscription;
        // 渲染器只在画布激活且设备资源可用时创建；调色板/封面先缓存在宿主。
        // volatile: 渲染线程在 OnCanvasUpdate/Draw 中以快照方式读取,UI 线程在 SwapBackgroundRenderer 中整体替换。
        private volatile BaseBackgroundRenderer? _background;
        // ComputeSharp.D2D1 3.2.0 的 D2D1ResourceTextureManager 只有终结器，
        // 没有可调用的 Dispose。复用旋转网格实例把切换过程中的原生资源数量限制为一份，
        // 并在宿主真正关闭时统一释放其效果和位图。
        private RotatingMeshBackgroundRenderer? _rotatingMeshBackground;
        private bool _rotatingMeshResourcesInitialized;
        // 图片解码和 CanvasBitmap 也按模式复用。反复开关图片背景时只保留一个
        // 可取消的解码循环和一组位图，避免分配器把每次切换的峰值长期留在进程工作集。
        private ImageBackgroundRenderer? _imageBackground;
        private bool _imageBackgroundResourcesInitialized;
        // 缓存最近一次调色板，shader 切换 / 设备重建后用于把新 renderer 重新染上当前调色板。
        private AnimatedWin2dControls.Impressionist.PaletteResult? _lastPalette;
        // 缓存最近一次封面像素，shader 切换后把新 renderer 重新染上当前封面（RotatingMesh 用）。
        private AnimatedWin2dControls.Impressionist.ArtworkPixelData? _lastArtwork;
        private readonly FogRenderer _fog = new();
        private readonly SnowRenderer _snow = new();
        private readonly RaindropRenderer _raindrop = new();
        private readonly LyricsRenderCoordinator _coordinator = new();

        private bool _advanced = true;
        private bool _isActive = true;
        private bool _coordinatorAttached;
        private bool _pausedByVisibility;
        private bool _canvasResourcesReady;
        private bool _rendererResourcesLoaded;
        private CanvasDevice? _rendererDevice;
        private CanvasDevice? _sdrDevice;

        // 背景半帧率：每 (skip+1) 帧才重绘一次不透明合成缓存，其余帧复用。0=全帧率，1=半帧率。
        private int _backgroundFrameSkip = 1;
        private long _frameCounter;
        // 保护 _bgCache 生命周期:Swap/OnCanvasCreateResources/PrepareForShutdown 持锁,DrawBackground 持锁完成 check+create+render+DrawImage。
        private readonly object _cacheGate = new();
        private CanvasRenderTarget? _bgCache;
        private float _bgCacheWidthDip;
        private float _bgCacheHeightDip;
        // 缓存 DPI 必须参与重建判定：跨屏拖动时 WinUI 保持窗口 DIP 尺寸不变、物理尺寸随
        // DPI 缩放，若只比较 DIP 尺寸，bgCache 会话会滞留旧 DPI——而渲染器已按新 DPI
        // 重建效果输入，DPI 失配会触发 ComputeSharp 的 DpiCompensation 节点，与 complex
        // 输入组合被 D2D1 判为无效图（D2DERR_INVALID_GRAPH_CONFIGURATION）→ 闪退。
        private float _bgCacheDpi;

        // 渲染线程热路径只读这些缓存字段（DP 仅能在 UI 线程访问，跨线程读会抛 COMException）
        private bool _isFogEnabled;
        private bool _isSnowEnabled;
        private bool _isRaindropEnabled;
        private bool _enableLightWave;
        private bool _isDark = true;
        private bool _useImageDominantTheme;
        private bool _useImageBackground;
        private string _backgroundImagePath = string.Empty;
        private double _backgroundImageBlurAmount = 20;

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
            lock (ctrl._cacheGate)
            {
                ctrl.SyncStateFromProperties();
                ctrl._background?.RefreshColors();
            }
        }

        private static void OnActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
            => ((NowPlayingCanvas)d).UpdateActiveState((bool)e.NewValue);

        private static void OnEnableFlagChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var ctrl = (NowPlayingCanvas)d;
            lock (ctrl._cacheGate)
            {
                ctrl.SyncStateFromProperties();
            }
        }

        private static void OnBackgroundShaderIndexChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var ctrl = (NowPlayingCanvas)d;
            ctrl.SwapBackgroundRenderer();
        }

        private static void OnImageBackgroundChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var ctrl = (NowPlayingCanvas)d;
            bool wasImageBackground = ctrl._useImageBackground;
            ctrl._useImageBackground = ctrl.UseImageBackground;
            ctrl._backgroundImagePath = ctrl.BackgroundImagePath ?? string.Empty;
            ctrl._backgroundImageBlurAmount = ctrl.BackgroundImageBlurAmount;

            if (wasImageBackground != ctrl._useImageBackground)
            {
                ctrl.SwapBackgroundRenderer();
            }
            else if (ctrl._background is ImageBackgroundRenderer image)
            {
                image.SetSource(ctrl._backgroundImagePath, ctrl._backgroundImageBlurAmount);
            }
        }

        private static void OnAdvancedLyricsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var ctrl = (NowPlayingCanvas)d;
            ctrl.UpdateAdvancedLyricsState((bool)e.NewValue);
        }

        private void UpdateAdvancedLyricsState(bool enabled)
        {
            _advanced = enabled;
            if (_canvas is null) return;

            _canvas.IsHitTestVisible = enabled && _isActive;
            IsHitTestVisible = enabled && _isActive;
            if (enabled)
            {
                _canvas.PointerWheelChanged -= OnCanvasPointerWheelChanged;
                _canvas.PointerMoved -= OnCanvasPointerMoved;
                _canvas.PointerExited -= OnCanvasPointerExited;
                _canvas.PointerEntered -= OnCanvasPointerEntered;
                _canvas.Tapped -= OnCanvasTapped;
                _canvas.PointerWheelChanged += OnCanvasPointerWheelChanged;
                _canvas.PointerMoved += OnCanvasPointerMoved;
                _canvas.PointerExited += OnCanvasPointerExited;
                _canvas.PointerEntered += OnCanvasPointerEntered;
                _canvas.Tapped += OnCanvasTapped;
                _canvas.TargetElapsedTime = TimeSpan.FromMilliseconds(1000.0 / _coordinator.TargetFrameRate);
                if (IsLoaded && !_coordinatorAttached)
                {
                    _coordinator.Attach();
                    _coordinatorAttached = true;
                }
            }
            else
            {
                _canvas.PointerWheelChanged -= OnCanvasPointerWheelChanged;
                _canvas.PointerMoved -= OnCanvasPointerMoved;
                _canvas.PointerExited -= OnCanvasPointerExited;
                _canvas.PointerEntered -= OnCanvasPointerEntered;
                _canvas.Tapped -= OnCanvasTapped;
                if (_coordinatorAttached)
                {
                    _coordinator.Detach();
                    _coordinatorAttached = false;
                }
            }
        }

        private void UpdateActiveState(bool active)
        {
            _isActive = active;
            Opacity = active ? 1 : 0;
            IsHitTestVisible = active && _advanced;

            if (!active)
            {
                if (_canvas is not null)
                    _canvas.Paused = true;
                if (_coordinatorAttached)
                {
                    _coordinator.Detach();
                    _coordinatorAttached = false;
                }
                if (_background is ImageBackgroundRenderer image)
                    image.SetActive(false);
            }
            else if (_advanced && IsLoaded && !_coordinatorAttached)
            {
                _coordinator.Attach();
                _coordinatorAttached = true;
            }

            UpdateCanvasPaused();
            if (active)
            {
                if ((_sdrFrameEnabled || CanPresentHdr) && _background is ImageBackgroundRenderer image)
                    image.SetActive(true);
                _canvas?.Invalidate();
            }
        }

        private void SwapBackgroundRenderer()
        {
            if (!_isActive)
                _rendererResourcesLoaded = false;

            bool wasCanvasPaused = _canvas?.Paused ?? true;
            if (_canvas is not null)
                _canvas.Paused = true;

            try
            {
                // Keep renderer disposal and cache replacement in the same critical
                // section as drawing. Nulling the field alone cannot protect an
                // already-entered Draw callback that holds a local renderer reference.
                lock (_cacheGate)
                {
                    ReleaseBackgroundCache();

                    var oldBg = _background;
                    _background = null;
                    if (oldBg is ImageBackgroundRenderer oldImage)
                        oldImage.SetActive(false);
                    if (oldBg is not RotatingMeshBackgroundRenderer &&
                        oldBg is not ImageBackgroundRenderer)
                        oldBg?.Dispose();

                    var newBg = CreateActiveBackgroundRenderer();
                    _background = newBg;
                    SyncStateFromProperties();
                    if (_lastPalette is not null) newBg.SetPalette(_lastPalette);
                    else newBg.RefreshColors();
                    if (_lastArtwork is not null) newBg.SetArtwork(_lastArtwork);
                    bool needsResources = newBg switch
                    {
                        RotatingMeshBackgroundRenderer => !_rotatingMeshResourcesInitialized,
                        ImageBackgroundRenderer => !_imageBackgroundResourcesInitialized,
                        _ => true
                    };
                    if (needsResources && _rendererResourcesLoaded && _isActive)
                    {
                        newBg.LoadResources();
                        if (newBg is RotatingMeshBackgroundRenderer)
                            _rotatingMeshResourcesInitialized = true;
                        else if (newBg is ImageBackgroundRenderer)
                            _imageBackgroundResourcesInitialized = true;
                    }
                }
            }
            finally
            {
                if (_canvas is not null)
                    _canvas.Paused = wasCanvasPaused
                        || _pausedByVisibility || _pausedByParent || _pausedByWindow || !_sdrAllowed || _hdrOwnsFrame;
            }
        }

        private BaseBackgroundRenderer CreateBackgroundRenderer(int index) => index switch
        {
            1 => new PS3XMBBackgroundRenderer(),
            2 => _rotatingMeshBackground ??= new RotatingMeshBackgroundRenderer(),
            3 => new LiquidFlowBackgroundRenderer(),
            4 => new GradientFlowBackgroundRenderer(),
            5 => new WavyBackgroundRenderer(),
            6 => new ChromaticResonanceBackgroundRenderer(),
            _ => new FluidBackgroundRenderer(),
        };

        private BaseBackgroundRenderer CreateActiveBackgroundRenderer()
        {
            if (!_useImageBackground)
                return CreateBackgroundRenderer(BackgroundShaderIndex);

            _imageBackground ??= new ImageBackgroundRenderer(
                _backgroundImagePath, _backgroundImageBlurAmount);
            _imageBackground.SetSource(_backgroundImagePath, _backgroundImageBlurAmount);
            _imageBackground.SetActive(_isActive);
            return _imageBackground;
        }

        private void LoadActiveResources()
        {
            if (!_isActive)
                return;

            SyncStateFromProperties();
            if (_background is null)
                _background = CreateActiveBackgroundRenderer();

            var background = _background;
            if (background is ImageBackgroundRenderer image)
                image.SetActive(true);
            background.EnableLightWave = _enableLightWave;
            background.IsDark = _isDark;
            background.UseImageDominantTheme = _useImageDominantTheme;
            if (_lastPalette is not null)
                background.SetPalette(_lastPalette);
            else
                background.RefreshColors();
            background.SetArtwork(_lastArtwork);

            LoadRendererResources();
        }

        private void LoadRendererResources()
        {
            var background = _background!;
            background.LoadResources();
            if (background is RotatingMeshBackgroundRenderer)
                _rotatingMeshResourcesInitialized = true;
            else if (background is ImageBackgroundRenderer)
                _imageBackgroundResourcesInitialized = true;
            _fog.LoadResources();
            _snow.LoadResources();
            _raindrop.LoadResources();
            if (_advanced)
                _coordinator.OnCreateResources();
            _rendererResourcesLoaded = true;
        }

        // The SDR game loop and the UI-thread HDR panel have different devices.
        // Preserve animation and lyric state while invalidating device-bound drawings.
        private bool PrepareSceneResources(CanvasDevice device)
        {
            if (ReferenceEquals(_rendererDevice, device)) return false;
            if (_rendererDevice is not null)
                LoadRendererResources();

            ReleaseBackgroundCache();
            _coordinator.ResetDeviceResources();
            _rendererDevice = device;
            return true;
        }

        private void ReleaseBackgroundCache()
        {
            _bgCache?.Dispose();
            _bgCache = null;
            _bgCacheHdr = default;
        }

        /// <summary>仅在 UI 线程调用：把依赖属性读入普通字段并下推到各渲染模块。</summary>
        private void SyncStateFromProperties()
        {
            _isFogEnabled = IsFogEnabled;
            _isSnowEnabled = IsSnowEnabled;
            _isRaindropEnabled = IsRaindropEnabled;
            _enableLightWave = EnableLightWave;
            _isDark = IsDark;
            _useImageDominantTheme = UseImageDominantTheme;
            _useImageBackground = UseImageBackground;
            _backgroundImagePath = BackgroundImagePath ?? string.Empty;
            _backgroundImageBlurAmount = BackgroundImageBlurAmount;

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
            DetachHdrPanel();
            _canvasResourcesReady = false;
            _sdrDevice = null;

            if (_visibilityCallbackToken != 0)
            {
                UnregisterPropertyChangedCallback(VisibilityProperty, _visibilityCallbackToken);
                _visibilityCallbackToken = 0;
            }

            _advanced = EnableAdvancedLyrics;
            SyncStateFromProperties();
            _canvas = GetTemplateChild(PartCanvasName) as CanvasAnimatedControl;
            _hdrPanel = GetTemplateChild(HdrPanelPart) as CanvasSwapChainPanel;

            if (_canvas != null)
            {
                _coordinator.Canvas = _canvas;
                _coordinator.LyricsRegion = LyricsRegion;

                _canvas.CreateResources += OnCanvasCreateResources;
                AttachFrameEvents();

                // 简单歌词模式：宿主对指针透明，让上方 XAML 歌词覆层接管输入。
                UpdateAdvancedLyricsState(_advanced);

                UpdateCanvasPaused();
            }

            UpdateActiveState(_isActive);

            _visibilityCallbackToken = RegisterPropertyChangedCallback(VisibilityProperty, OnVisibilityChanged);
        }

        private void DetachCanvasEvents()
        {
            if (_canvas is null) return;
            _canvas.Paused = true;
            if (_coordinatorAttached)
            {
                _coordinator.Detach();
                _coordinatorAttached = false;
            }
            _canvas.CreateResources -= OnCanvasCreateResources;
            DetachFrameEvents();
            _canvas.PointerWheelChanged -= OnCanvasPointerWheelChanged;
            _canvas.PointerMoved -= OnCanvasPointerMoved;
            _canvas.PointerExited -= OnCanvasPointerExited;
            _canvas.PointerEntered -= OnCanvasPointerEntered;
            _canvas.Tapped -= OnCanvasTapped;
        }

        private void AttachFrameEvents()
        {
            if (_canvas is null) return;
            _updateSubscription ??= FrameEventSubscription.SubscribeUpdate(_canvas, OnCanvasUpdate);
            try { _drawSubscription ??= FrameEventSubscription.SubscribeDraw(_canvas, OnCanvasDraw); }
            catch
            {
                var update = _updateSubscription;
                _updateSubscription = null;
                update?.Dispose();
                throw;
            }
        }

        private void DetachFrameEvents()
        {
            var draw = _drawSubscription;
            var update = _updateSubscription;
            _drawSubscription = null;
            _updateSubscription = null;
            try { draw?.Dispose(); }
            finally { update?.Dispose(); }
        }

        private void OnControlLoaded(object sender, RoutedEventArgs e)
        {
            AttachFrameEvents();
            UpdateActiveState(_isActive);
        }

        private void OnControlUnloaded(object sender, RoutedEventArgs e)
        {
            _sdrFrameEnabled = false;
            if (_canvas is not null)
                _canvas.Paused = true;
            bool wasPresented = StopHdrPresentation(releaseDevice: true);
            DetachFrameEvents();
            // Keep the template for reattach. Its GPU resources can be rebuilt on demand.
            lock (_cacheGate)
            {
                if (_rendererResourcesLoaded)
                    ReleaseSceneResources();
            }
            if (_coordinatorAttached)
            {
                _coordinator.Detach();
                _coordinatorAttached = false;
            }
            if (wasPresented)
                HdrPresentationChanged?.Invoke(false, false);
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
            if (_pausedByWindow == paused) return;
            _pausedByWindow = paused;
            RetryHdrPresentation();
        }

        private void UpdateCanvasPaused(bool replaceHdr = false)
        {
            bool canPresentHdr = CanPresentHdr;
            bool canPresentSdr = CanRenderScene && _sdrAllowed && !canPresentHdr;
            if (!canPresentSdr)
                _sdrFrameEnabled = false;
            // Navigation and feature settings end the HDR session. A minimized window
            // can resume the same device after its presentation clock has stopped.
            bool releaseHdr = !_hdrColor.IsEnabled || _hdrFailed || !_isActive || _pausedByParent || _pausedByVisibility;
            bool stoppedHdr = (replaceHdr || !canPresentHdr) && StopHdrPresentation(releaseHdr);
            if (canPresentHdr)
                _hdrClock ??= FrameEventSubscription.SubscribeRendering(RenderHdrFrame);

            if (_canvas is { } canvas)
            {
                if (canPresentSdr && _canvasResourcesReady && !_rendererResourcesLoaded)
                {
                    lock (_cacheGate)
                        LoadActiveResources();
                }

                _sdrFrameEnabled = canPresentSdr;
                // A paused swap chain retains its last frame. HDR-only image mode needs
                // the window's image to show through until the new panel has presented.
                canvas.Opacity = _sdrAllowed ? 1 : 0;
                canvas.Paused = !canPresentSdr;
            }

            if (stoppedHdr)
                HdrPresentationChanged?.Invoke(false, false);
        }

        private static void OnVisibilityChanged(DependencyObject d, DependencyProperty dp)
        {
            var ctrl = (NowPlayingCanvas)d;
            ctrl._pausedByVisibility = ctrl.Visibility != Visibility.Visible;
            ctrl.UpdateCanvasPaused();
        }

        // ── 资源 / 热路径 ─────────────────────────────────────────────────────

        // The caller holds _cacheGate so neither presentation can use the retiring resources.
        private void ReleaseSceneResources()
        {
            ReleaseBackgroundResources();
            _coordinator.ResetDeviceResources();
        }

        private void ReleaseBackgroundResources()
        {
            var background = _background;
            _background = null;
            if (background is ImageBackgroundRenderer image)
                    image.SetActive(false);
            background?.Dispose();

            if (_rotatingMeshBackground is not null &&
                    !ReferenceEquals(background, _rotatingMeshBackground))
                    _rotatingMeshBackground.Dispose();
            if (_imageBackground is not null &&
                    !ReferenceEquals(background, _imageBackground))
                    _imageBackground.Dispose();

            _rotatingMeshBackground = null;
            _rotatingMeshResourcesInitialized = false;
            _imageBackground = null;
            _imageBackgroundResourcesInitialized = false;
            _rendererResourcesLoaded = false;
            _fog.Dispose();
            _snow.Dispose();
            _raindrop.Dispose();
            ReleaseBackgroundCache();
            _rendererDevice = null;
        }

        private void OnCanvasCreateResources(CanvasAnimatedControl sender, CanvasCreateResourcesEventArgs args)
        {
            try
            {
                _sdrDevice = sender.Device;
                _canvasResourcesReady = true;
                if (_hdrOwnsFrame || (_hdrDevice is not null && ReferenceEquals(_rendererDevice, _hdrDevice))) return;

                lock (_cacheGate)
                {
                    _rendererResourcesLoaded = false;
                    _rendererDevice = null;
                    ReleaseBackgroundCache();
                    _rotatingMeshResourcesInitialized = false;
                    _imageBackgroundResourcesInitialized = false;
                    if (_sdrFrameEnabled)
                        LoadActiveResources();
                }
            }
            catch (Exception ex) { RaiseException(ex); }
        }

        private void OnCanvasUpdate(TimeSpan elapsed)
        {
            var canvas = _canvas;
            if (canvas is null || !_sdrFrameEnabled || _hdrOwnsFrame || !Monitor.TryEnter(_cacheGate)) return;
            try
            {
                if (_hdrOwnsFrame || !_sdrFrameEnabled || !_rendererResourcesLoaded || _sdrDevice is not { } device) return;
                PrepareSceneResources(device);
                UpdateScene(canvas, elapsed);
            }
            catch (Exception ex)
            {
#if DEBUG
                Log.ForContext<NowPlayingCanvas>().Error(ex, "Playback scene update failed");
#endif
            }
            finally { Monitor.Exit(_cacheGate); }
        }

        private void UpdateScene(ICanvasResourceCreator creator, TimeSpan elapsed)
        {
            _background?.Update(elapsed);
            _fog.Update(elapsed.TotalSeconds);
            _snow.Update(elapsed.TotalSeconds);
            _raindrop.Update(elapsed.TotalSeconds);
            if (_advanced)
                _coordinator.OnUpdate(creator, elapsed);
        }

        private void OnCanvasDraw(CanvasDrawContext ds)
        {
            var canvas = _canvas;
            if (canvas is null || !_sdrFrameEnabled || _hdrOwnsFrame || !Monitor.TryEnter(_cacheGate)) return;
            try
            {
                if (_hdrOwnsFrame || !_sdrFrameEnabled || !_rendererResourcesLoaded) return;
                var frame = new PlaybackCanvasFrame(canvas);
                if (PrepareSceneResources(frame.Device))
                    UpdateScene(frame.Device, TimeSpan.Zero);
                DrawScene(frame, ds);
            }
            catch (Exception ex)
            {
                _bgCacheWidthDip = -1f;
#if DEBUG
                Log.ForContext<NowPlayingCanvas>().Error(ex, "Playback scene draw failed");
#endif
            }
            finally { Monitor.Exit(_cacheGate); }
        }

        private void DrawScene(PlaybackCanvasFrame frame, CanvasDrawContext ds)
        {
            DrawBackground(frame, ds);
            if (_advanced)
                _coordinator.OnDraw(frame.Device, ds, frame.Hdr, consumePendingLyrics: !frame.Hdr.IsEnabled);
        }

        private void DrawBackground(PlaybackCanvasFrame frame, CanvasDrawContext ds)
        {
            _frameCounter++;
            if (_backgroundFrameSkip <= 0 || _useImageBackground && frame.Hdr.IsEnabled)
            {
                DrawBackgroundLayers(frame, ds);
                return;
            }

            float width = (float)frame.Size.Width;
            float height = (float)frame.Size.Height;
            if (width <= 0f || height <= 0f) return;

            var format = frame.Hdr.IsEnabled
                ? DirectXPixelFormat.R16G16B16A16Float
                : DirectXPixelFormat.B8G8R8A8UIntNormalized;
            bool render = _frameCounter % (_backgroundFrameSkip + 1) == 0 || _bgCacheHdr != frame.Hdr;
            if (_bgCache is null || _bgCacheWidthDip != width || _bgCacheHeightDip != height
                || _bgCacheDpi != frame.Dpi || _bgCache.Format != format)
            {
                _bgCache?.Dispose();
                _bgCache = new CanvasRenderTarget(frame.Device, width, height, frame.Dpi, format,
                    CanvasAlphaMode.Premultiplied);
                _bgCacheWidthDip = width;
                _bgCacheHeightDip = height;
                _bgCacheDpi = frame.Dpi;
                render = true;
            }

            if (render)
            {
                if (frame.Hdr.IsEnabled)
                {
                    using var drawing = _bgCache.CreateDrawingSession();
                    drawing.Clear(Colors.Transparent);
                    drawing.EffectBufferPrecision = CanvasBufferPrecision.Precision16Float;
                    using var session = CanvasDrawingSessionScope.Borrow(drawing);
                    DrawBackgroundLayers(frame, session.Context);
                }
                else
                {
                    using var session = CanvasDrawingSessionScope.Create(_bgCache);
                    DrawBackgroundLayers(frame, session.Context);
                }
                _bgCacheHdr = frame.Hdr;
            }

            ds.DrawImage(_bgCache);
        }

        private void DrawBackgroundLayers(PlaybackCanvasFrame frame, CanvasDrawContext ds)
        {
            _background?.Draw(frame, ds);
            _snow.Draw(frame, ds);
            _fog.Draw(frame, ds);
            _raindrop.Draw(frame, ds);
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
                lock (_cacheGate)
                {
                    _background?.SetPalette(palette);
                }
                if (UseImageDominantTheme && palette is not null)
                    ThemeResolved?.Invoke(this, palette.PaletteIsDark);
            }
            catch (Exception ex) { RaiseException(ex); }
        }

        // ── 封面 ─────────────────────────────────────────────────────────────

        /// <summary>
        /// 注入当前曲目封面像素（仅封面驱动型着色器如 RotatingMesh 消费；
        /// 传 null 表示当前曲目无封面，着色器回退到调色板渐变）。
        /// </summary>
        public void SetArtwork(AnimatedWin2dControls.Impressionist.ArtworkPixelData? artwork)
        {
            try
            {
                _lastArtwork = artwork;
                lock (_cacheGate)
                {
                    _background?.SetArtwork(artwork);
                }
            }
            catch (Exception ex) { RaiseException(ex); }
        }

        // ── 释放 ──────────────────────────────────────────────────────────────

        public void PrepareForShutdown()
        {
            _hdrShuttingDown = true;
            _sdrFrameEnabled = false;
            DetachHdrPanel();
            _sceneTitle = null;
            if (_canvas is not null)
                _canvas.Paused = true;
            if (_visibilityCallbackToken != 0)
            {
                UnregisterPropertyChangedCallback(VisibilityProperty, _visibilityCallbackToken);
                _visibilityCallbackToken = 0;
            }
            var canvas = _canvas;
            DetachCanvasEvents();
            // Win2D controls contain native event sources. Removing the
            // CanvasAnimatedControl from the visual tree breaks the XAML/C++
            // reference cycle when this host is actually unloaded.
            canvas?.RemoveFromVisualTree();
            _canvas = null;

            _coordinatorAttached = false;
            _coordinator.PrepareForShutdown();
            lock (_cacheGate)
                ReleaseBackgroundResources();
            _sdrDevice = null;
            _canvasResourcesReady = false;
            _lastPalette = null;
            _lastArtwork = null;
        }

        public void Dispose()
        {
            PrepareForShutdown();
            ThemeResolved = null;
            ExceptionOccurred = null;
            LyricLineClicked = null;
            RenderError = null;
            HdrPresentationChanged = null;
            DrawHdrForeground = null;
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
