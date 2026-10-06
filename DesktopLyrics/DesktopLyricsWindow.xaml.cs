using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl;
using AnimatedWin2dControls.Messages;
using BassPlayerIpc.Shared;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Graphics;
using WinUIEx;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.ViewModel;
using Windows.UI;

namespace WinUIMusicPlayer.DesktopLyrics
{
    public sealed class DesktopLyricsInputGrid : Grid
    {
        public void SetCursor(InputCursor? cursor) => ProtectedCursor = cursor;
    }

    /// <summary>
    /// 桌面歌词悬浮窗：透明、置顶、不进任务栏/Alt-Tab。
    /// 基类与 spectrum 一致使用 WinUIEx.WindowEx。
    /// 解锁态：标准窗口（标题栏 + 可调整大小），按住内容区任意位置拖动；
    /// 悬浮模式锁定态：GWL_STYLE 移除标题栏/边框位 + OR-in WS_POPUP（WinUIEx ToggleWindowStyle，
    /// 含 SWP_FRAMECHANGED）+ 整窗点击穿透常开（WS_EX_LAYERED 进锁定态一次性设置常驻，
    /// 运行期只切 WS_EX_TRANSPARENT）；鼠标悬停窗口时仅"显示"右上角按钮组，
    /// 光标移到按钮上才临时取消穿透供点击（游标轮询两档：悬停窗口期 50ms 快轮询保证跟手，
    /// 其余 200ms 慢轮询只做进窗检测与自愈，BetterLyrics OverlayInputHelper 思路），
    /// 慢轮询附带自愈：窗口被前后台切换偶发置为不可见/最小化时无焦点拉回并重申置顶。
    /// </summary>
    public sealed partial class DesktopLyricsWindow : WinUIEx.WindowEx, IDisposable
    {
        private const int DefaultWidth = 1800;
        private const int DefaultHeight = 280;
        private const int BottomMargin = 60;
        private const double HoverPollingIntervalMs = 50;    // 悬停窗口期间：按钮组显隐/穿透切换要跟手
        private const double IdlePollingIntervalMs = 200;    // 锁定态静默期：进窗检测 + 自愈
        private const double ControlPanelHoverMargin = 6.0;
        private const double AdaptiveSamplingIntervalMs = 1000;   // 环境取色轮询周期（BetterLyrics 同款 1s）
        private const double AdaptiveSwitchThreshold = 128;       // 环境 YIQ 亮度中位数阈值：低于视为暗背景（白字）
        private const double AdaptiveHysteresis = 16;             // 切换滞回带：边界附近的采样抖动不引起黑白来回闪

        private IDesktopLyricsRenderer? _renderer;
        private readonly IntPtr _hwnd;
        private readonly DesktopLyricsMode _mode;
        private ThemeStyleHelper? _themeStyleHelper;

        /// <summary>桌面歌词状态源（锁定图标绑定 / 按钮处理 / 边界与样式读写）。</summary>
        public DesktopLyricsViewModel ViewModel { get; } = App.Services.GetRequiredService<DesktopLyricsViewModel>();
        /// <summary>任务栏模式媒体信息来源。播放命令仍由现有 PlaybackCommands 统一守卫。</summary>
        public AppViewModel AppViewModel { get; } = App.Services.GetRequiredService<AppViewModel>();
        public PlaybackCommands Playback { get; } = App.Services.GetRequiredService<PlaybackCommands>();
        private BassPlayerCommandService PlayerService { get; } = App.Services.GetRequiredService<BassPlayerCommandService>();
        private bool _locked = true;
        private bool _clickThrough;              // 当前穿透样式状态（false = 尚未设置）
        private bool _cursorOverPanel;
        private DispatcherQueueTimer? _hoverTimer;   // 50ms，仅光标悬停窗口期间运行
        private DispatcherQueueTimer? _idleTimer;    // 200ms，锁定态常驻：进窗检测 + 自愈
        private WindowStyle? _originalWindowStyle;   // 首次锁定前缓存的解锁态样式
        private bool _disposed;
        private bool _isOverlayVisible = true;
        private CancellationTokenSource? _taskbarCoverCts;
        private Music? _taskbarCoverMusic;
        private IDesktopLyricsBoundsHost? _taskbarHost;
        private bool _isTaskbarManipulating;
        private TaskbarResizeEdge _taskbarResizeEdge;
        private InputSystemCursorShape? _taskbarCursorShape;
        private InputCursor? _taskbarCursor;
        private WindowHelper.POINT _taskbarStartCursor;
        private TaskbarWindowBounds _taskbarStartBounds;

        private bool _isDragging;
        private WindowHelper.POINT _dragStartCursor;
        private PointInt32 _dragStartWindowPos;
        private RectInt32? _panelScreenRectCache;   // 按钮组屏幕矩形缓存（含悬停外扩）；窗口位置/尺寸变化时失效
        private DispatcherQueueTimer? _adaptiveColorTimer;
        private bool? _adaptiveIsDarkBackground;    // 上次明暗判定（null=未判定），滞回切换的基准
        private Color? _lastAdaptiveTextColor;      // 当前应用的取色文字色（判定不变则跳过重绘）
        private readonly Rectangle[] _taskbarSpectrumBars = new Rectangle[12];
        private readonly float[] _taskbarSpectrumTarget = new float[12];
        private readonly float[] _taskbarSpectrumDisplayed = new float[12];
        private DispatcherQueueTimer? _taskbarSpectrumTimer;
        private double _taskbarSpectrumReference;
        private long _taskbarLastFftSequence;
        private bool _taskbarFftRequested;

        [Flags]
        private enum TaskbarResizeEdge
        {
            None = 0,
            Left = 1,
            Top = 2,
            Right = 4,
            Bottom = 8,
        }

        public DesktopLyricsWindow(DesktopLyricsMode mode = DesktopLyricsMode.Floating)
        {
            _mode = mode is DesktopLyricsMode.Floating or DesktopLyricsMode.Taskbar
                ? mode
                : DesktopLyricsMode.Floating;
            InitializeComponent();
            // Window 的 x:Bind 默认等到 Activated 才初始化；歌词窗口以 Show(false)
            // 显示，任务栏模式还会成为 WS_CHILD，必须主动连接命令、图标和双向绑定。
            Bindings.Initialize();
            if (_mode == DesktopLyricsMode.Taskbar)
                InitializeTaskbarSpectrum();
            if (_mode == DesktopLyricsMode.Taskbar)
            {
                // 任务栏客户区高度很小，媒体信息和歌词共用一行时不再额外占用上下边距。
                RootGrid.Padding = new Thickness(0);
            }
            ConfigureTaskbarToolTips();
            _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

            // 渲染器按"逐字效果"开关选择：CanvasLyricsRenderer（Win2D 逐字扫光）或
            // TextBlockLyricsRenderer（文本描边）。开关变化经 PropertyChanged 热切换（EnsureRenderer）。
            // 任务栏模式只保留静态歌词，逐字动画会占用额外高度并降低任务栏刷新稳定性。
            EnsureRenderer(_mode != DesktopLyricsMode.Taskbar && ViewModel.IsKaraokeEnabled);

            // 复用 WinUIEx 自带的完全透明背景（与主程序"透明"样式同源）
            SystemBackdrop = new TransparentTintBackdrop();
            ConfigureWindow();
            UpdateAdaptiveColorMode();

            // 跟随主程序明暗主题（MusicDetailsWindow 同款）：只转发 themeChanged，
            // 不调用 SetAppStyle——覆盖窗口需保持全透明背景，不能被换成亚克力/云母
            _themeStyleHelper = new ThemeStyleHelper(this, AppWindow);
            _themeStyleHelper.SetAppTheme();
            if (App.MainWindow is not null)
            {
                App.MainWindow.themeChanged += MainWindow_themeChanged;
            }

            UILyricsBus.Changed += OnUILyricsChanged;
            TimeProgressBus.CurrentPlayingTimeChanged += OnTimeProgressChanged;
            OffsetMsBus.Changed += OnOffsetChanged;
            IsPlayingBus.Changed += OnIsPlayingChanged;
            AppWindow.Changed += OnAppWindowChanged;
            ViewModel.PropertyChanged += OnViewModelPropertyChanged;
            AppViewModel.PropertyChanged += OnAppViewModelPropertyChanged;
            Closed += OnWindowClosed;

            // 拉取全量歌词/进度/样式状态（AppViewModel.SendFullLyricsSync）
            LyricsSyncRequestBus.Request();
            UpdateTaskbarMedia();
            UpdateTaskbarSpectrumState();
        }

        private void InitializeTaskbarSpectrum()
        {
            TaskbarSpectrumCanvas.Children.Clear();
            for (int i = 0; i < _taskbarSpectrumBars.Length; i++)
            {
                _taskbarSpectrumBars[i] = CreateSpectrumBar(i);
                TaskbarSpectrumCanvas.Children.Add(_taskbarSpectrumBars[i]);
            }
            _taskbarSpectrumTimer = DispatcherQueue.CreateTimer();
            _taskbarSpectrumTimer.Interval = TimeSpan.FromMilliseconds(33);
            _taskbarSpectrumTimer.Tick += (_, _) => UpdateTaskbarSpectrumVisual();
            _taskbarSpectrumTimer.Start();
        }

        private void SetTaskbarSpectrumTimerRunning(bool running)
        {
            if (_mode != DesktopLyricsMode.Taskbar) return;
            if (running)
                _taskbarSpectrumTimer?.Start();
            else
                _taskbarSpectrumTimer?.Stop();
        }

        private Rectangle CreateSpectrumBar(int index)
        {
            var bar = new Rectangle
            {
                Width = 2,
                Height = 2,
                RadiusX = 1,
                RadiusY = 1,
                Fill = ResolveTaskbarSpectrumBrush(),
                Opacity = 0.92,
            };
            Canvas.SetLeft(bar, 1 + index * 3);
            Canvas.SetTop(bar, 14);
            return bar;
        }

        private Brush ResolveTaskbarSpectrumBrush() =>
            TaskbarTitleText.Foreground
            ?? Application.Current.Resources["TextFillColorPrimaryBrush"] as Brush
            ?? new SolidColorBrush(Colors.White);

        private void UpdateTaskbarSpectrumBrush()
        {
            if (_mode != DesktopLyricsMode.Taskbar) return;
            Brush brush = ResolveTaskbarSpectrumBrush();
            foreach (Rectangle bar in _taskbarSpectrumBars) bar.Fill = brush;
        }

        private void ApplyTaskbarSpectrum(FftSnapshot snapshot)
        {
            if (_disposed || !_taskbarFftRequested) return;
            if (!snapshot.IsAvailable || snapshot.BinCount < 2)
            {
                _taskbarSpectrumReference = 0;
                Array.Clear(_taskbarSpectrumTarget);
                return;
            }

            int bins = Math.Min(snapshot.BinCount, Math.Min(snapshot.Left.Length, snapshot.Right.Length));
            if (snapshot.SampleRate <= 0 || snapshot.FftSize <= 0 || bins < 2)
            {
                _taskbarSpectrumReference = 0;
                Array.Clear(_taskbarSpectrumTarget);
                return;
            }
            double peak = 0;
            for (int i = 1; i < bins; i++)
                peak = Math.Max(peak, ToMonoMagnitude(snapshot.Left[i], snapshot.Right[i]));
            if (!(peak > 0) || !double.IsFinite(peak))
            {
                _taskbarSpectrumReference = 0;
                Array.Clear(_taskbarSpectrumTarget);
                return;
            }
            _taskbarSpectrumReference = Math.Max(peak, _taskbarSpectrumReference * 0.92);
            // 传输协议保留双声道；任务栏将两侧功率平均为一组频带，再统一归一化。
            for (int band = 0; band < _taskbarSpectrumTarget.Length; band++)
            {
                double lowHz = 45 * Math.Pow(20000d / 45, band / (double)_taskbarSpectrumTarget.Length);
                double highHz = 45 * Math.Pow(20000d / 45, (band + 1) / (double)_taskbarSpectrumTarget.Length);
                int first = Math.Clamp((int)Math.Floor(lowHz * snapshot.FftSize / snapshot.SampleRate), 1, bins - 1);
                int last = Math.Clamp((int)Math.Ceiling(highHz * snapshot.FftSize / snapshot.SampleRate), first, bins - 1);
                double magnitude = 0;
                for (int bin = first; bin <= last; bin++)
                {
                    magnitude = Math.Max(magnitude, ToMonoMagnitude(snapshot.Left[bin], snapshot.Right[bin]));
                }
                _taskbarSpectrumTarget[band] = ToSpectrumLevel(magnitude, _taskbarSpectrumReference);
            }
        }

        private static float ToSpectrumLevel(double magnitude, double reference)
        {
            if (!(magnitude > 0) || !(reference > 0)) return 0;
            double db = 20 * Math.Log10(magnitude / reference);
            return (float)Math.Clamp((db + 40) / 40, 0, 1);
        }

        private static double ToMonoMagnitude(float left, float right) =>
            Math.Sqrt(((double)left * left + (double)right * right) * 0.5);

        private void UpdateTaskbarSpectrumVisual()
        {
            if (_disposed || _mode != DesktopLyricsMode.Taskbar) return;
            if (_taskbarFftRequested && PlayerService.CurrentFftSnapshot is { } snapshot &&
                snapshot.Sequence != _taskbarLastFftSequence)
            {
                _taskbarLastFftSequence = snapshot.Sequence;
                ApplyTaskbarSpectrum(snapshot);
            }
            for (int i = 0; i < _taskbarSpectrumBars.Length; i++)
            {
                _taskbarSpectrumDisplayed[i] += (_taskbarSpectrumTarget[i] - _taskbarSpectrumDisplayed[i]) * 0.35f;
                SetSpectrumBar(_taskbarSpectrumBars[i], _taskbarSpectrumDisplayed[i]);
            }
        }

        private static void SetSpectrumBar(Rectangle bar, float level)
        {
            double height = 2 + Math.Clamp(level, 0, 1) * 26;
            bar.Height = height;
            Canvas.SetTop(bar, (30 - height) / 2);
        }

        private void UpdateTaskbarSpectrumState()
        {
            if (_mode != DesktopLyricsMode.Taskbar) return;
            // Keep the request armed while a local track is selected. The
            // command service replays it after an audio-process reconnect, and
            // AudioPlayer emits frames only while a playable PCM stream is
            // rendering, so paused/non-PCM sources do not produce FFT frames.
            bool enabled = _isOverlayVisible &&
                AppViewModel.CurrentPlayingMusic is { IsRemote: false };
            if (_taskbarFftRequested == enabled) return;
            _taskbarFftRequested = enabled;
            PlayerService.SetFftEnabled(enabled);
            if (!enabled)
            {
                _taskbarSpectrumReference = 0;
                _taskbarLastFftSequence = 0;
                Array.Clear(_taskbarSpectrumTarget);
            }
        }

        private void SetTaskbarMediaHover(bool isOver)
        {
            TaskbarSongMetadata.Visibility = isOver ? Visibility.Collapsed : Visibility.Visible;
            TaskbarSongControls.Visibility = isOver ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SetTaskbarCoverHover(bool isOver)
        {
            TaskbarCoverPlayIcon.Opacity = isOver ? 1 : 0;
        }

        private void TaskbarSongInfoPanel_PointerEntered(object sender, PointerRoutedEventArgs e) => SetTaskbarMediaHover(true);
        private void TaskbarSongInfoPanel_PointerExited(object sender, PointerRoutedEventArgs e) => SetTaskbarMediaHover(false);
        private void TaskbarCoverButtonHost_PointerEntered(object sender, PointerRoutedEventArgs e) => SetTaskbarCoverHover(true);
        private void TaskbarCoverButtonHost_PointerExited(object sender, PointerRoutedEventArgs e) => SetTaskbarCoverHover(false);
        private void TaskbarCoverPlayPauseButton_PointerEntered(object sender, PointerRoutedEventArgs e) => SetTaskbarCoverHover(true);
        private void TaskbarCoverPlayPauseButton_PointerExited(object sender, PointerRoutedEventArgs e) => SetTaskbarCoverHover(false);

        private void TaskbarVolumeSlider_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            AppViewModel.IsMouseOverVolumeSlider = true;
        }

        private void TaskbarVolumeSlider_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            AppViewModel.IsMouseOverVolumeSlider = false;
        }

        private void TaskbarVolumeSlider_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            if (!AppViewModel.IsMouseOverVolumeSlider) return;
            int delta = e.GetCurrentPoint(TaskbarVolumeSlider).Properties.MouseWheelDelta;
            if (delta > 0)
                AppViewModel.AdjustVolume(1);
            else if (delta < 0)
                AppViewModel.AdjustVolume(-1);
            e.Handled = true;
        }

        internal void AttachTaskbarHost(IDesktopLyricsBoundsHost host) => _taskbarHost = host;

        private void ConfigureTaskbarToolTips()
        {
            if (_mode != DesktopLyricsMode.Taskbar) return;
            ToolTipService.SetToolTip(TaskbarPreviousButton, ToolUtils.GetString("DesktopLyricsPreviousButtonTooltip"));
            ToolTipService.SetToolTip(TaskbarCoverPlayPauseButton, ToolUtils.GetString("DesktopLyricsPlayPauseButtonTooltip"));
            ToolTipService.SetToolTip(TaskbarNextButton, ToolUtils.GetString("DesktopLyricsNextButtonTooltip"));
        }

        /// <summary>复用窗口和渲染器；隐藏时停止采样、自愈及渲染，恢复时重拉状态。</summary>
        public void SetOverlayVisible(bool visible)
        {
            if (_disposed || _isOverlayVisible == visible) return;
            _isOverlayVisible = visible;
            if (!visible)
            {
                StopHoverTimer();
                StopIdleTimer();
                StopAdaptiveColorTimer();
                _renderer?.SetSuspended(true);
                _isDragging = false;
                _isTaskbarManipulating = false;
                _taskbarResizeEdge = TaskbarResizeEdge.None;
                RootGrid.ReleasePointerCaptures();
                if (_mode == DesktopLyricsMode.Taskbar)
                {
                    SetTaskbarMediaHover(false);
                    SetTaskbarCoverHover(false);
                    SetTaskbarSpectrumTimerRunning(false);
                    WindowHelper.ShowWindow(_hwnd, WindowHelper.SW_HIDE);
                }
                else
                    AppWindow.Hide();
                UpdateTaskbarSpectrumState();
                return;
            }

            LyricsSyncRequestBus.Request();
            _renderer?.SetSuspended(false);
            if (_mode == DesktopLyricsMode.Taskbar)
            {
                SetTaskbarSpectrumTimerRunning(true);
                WindowHelper.ShowWindow(_hwnd, WindowHelper.SW_SHOWNOACTIVATE);
            }
            else
            {
                WindowHelper.RestoreOverlay(_hwnd);
                ApplyLock(ViewModel.IsLocked);
            }
            UpdateAdaptiveColorMode();
            UpdateTaskbarSpectrumState();
        }

        /// <summary>窗口创建后由 Manager 以 VM 初值调用一次；后续锁定变化经 ViewModel.PropertyChanged 触发（幂等）。</summary>
        public void ApplyLock(bool locked)
        {
            _locked = locked;
            if (_mode == DesktopLyricsMode.Taskbar)
            {
                // 任务栏宿主始终保持锁定布局；媒体控制组仍需接收点击，因此不启用整窗穿透。
                WindowHelper.SetClickThrough(_hwnd, false);
                _clickThrough = false;
                _isTaskbarManipulating = false;
                _taskbarResizeEdge = TaskbarResizeEdge.None;
                RootGrid.ReleasePointerCaptures();
                UpdateControlPanelVisual();
                return;
            }
            // LAYERED 常驻且只在进锁定态时设置一次（穿透开关只切 TRANSPARENT）：
            // 运行期反复增删 LAYERED 会与 DWM 分层合成竞态，前后台切换时偶发整窗隐身
            if (locked) WindowHelper.EnsureLayered(_hwnd);
            ApplyClickThrough(locked);
            if (locked)
            {
                // GWL_STYLE：移除标题栏/边框位 + OR-in WS_POPUP（均含 SWP_FRAMECHANGED 立即重算）
                _originalWindowStyle ??= this.GetWindowStyle();
                this.ToggleWindowStyle(false, WindowStyle.Caption | WindowStyle.ThickFrame);
                this.ToggleWindowStyle(true, WindowStyle.Popup | WindowStyle.Visible);
                // presenter 意图同步为无边框，防止其簿记在后续事件中重放 WS_THICKFRAME
                if (AppWindow.Presenter is OverlappedPresenter presenter)
                    presenter.SetBorderAndTitleBar(false, false);
            }
            else
            {
                if (_originalWindowStyle is { } style)
                    this.SetWindowStyle(style);
                // presenter 意图同步回解锁基线（与 ConfigureWindow 一致）
                if (AppWindow.Presenter is OverlappedPresenter presenter)
                    presenter.SetBorderAndTitleBar(true, false);
                _originalWindowStyle = null;   // spectrum 同款：还原后清除缓存，下次锁定重新缓存
            }
            InvalidatePanelScreenRect();   // 边框样式切换可能改变客户区原点
            UpdateControlPanelVisual();
            // 切锁定样式会写入 WS_VISIBLE；隐藏策略优先，避免托盘操作意外显示。
            if (!_isOverlayVisible) AppWindow.Hide();
        }

    /// <summary>
    /// 把有效样式推给渲染器：自定义颜色覆盖开启时用样式原色，
    /// 否则用环境取色结果（黑/白）覆盖样式颜色（悬浮窗默认跟随背景）。
    /// </summary>
    private void ApplyEffectiveStyle()
    {
        if (_renderer is null) return;
        DesktopLyricsStyle style = ViewModel.Style;
        if (_mode == DesktopLyricsMode.Taskbar)
            style = style with { FontSize = GetTaskbarFontSize(style) };
        if (!style.UseCustomColor && _lastAdaptiveTextColor is { } adaptive)
        {
            style = style with { Color = adaptive };
        }
        _renderer.SetStyle(style);
    }

    /// <summary>
    /// 任务栏高度通常只有一行文字的空间。主歌词、翻译、发音会各占一行，
    /// 因此按实际启用的行数压缩字号，并保留用户设置的较小字号。
    /// </summary>
    private static double GetTaskbarFontSize(DesktopLyricsStyle style)
    {
        int lineCount = 1 + (style.ShowTranslation ? 1 : 0) + (style.ShowPronunciation ? 1 : 0);
        double maximum = lineCount switch
        {
            3 => 10,
            2 => 13,
            _ => 18,
        };
        return Math.Min(style.FontSize, maximum);
    }

    /// <summary>按样式快照的自定义颜色覆盖开关启停环境取色轮询；任何样式变化都全量推送渲染器。
    /// 注意自适应模式下不能只刷新取色：字号/字重/阴影强度等非颜色样式改动若不显式推送，
    /// 要等到下一次黑白翻转或渲染器切换才生效（曾表现为阴影滑块拖动无效）。</summary>
    private void UpdateAdaptiveColorMode()
    {
        if (!_isOverlayVisible || ViewModel.Style.UseCustomColor)
        {
            StopAdaptiveColorTimer();
            _adaptiveIsDarkBackground = null;
            _lastAdaptiveTextColor = null;
        }
        else
        {
            StartAdaptiveColorTimer();
        }

        // 无条件全量推送（幂等、轻量）：自适应模式下颜色由 _lastAdaptiveTextColor 覆盖
        ApplyEffectiveStyle();

        if (_isOverlayVisible && !ViewModel.Style.UseCustomColor)
            RefreshAdaptiveColor();
    }

    private void StartAdaptiveColorTimer()
    {
        if (_adaptiveColorTimer is null)
        {
            _adaptiveColorTimer = DispatcherQueue.CreateTimer();
            _adaptiveColorTimer.Interval = TimeSpan.FromMilliseconds(AdaptiveSamplingIntervalMs);
            _adaptiveColorTimer.Tick += (_, _) => RefreshAdaptiveColor();
        }
        _adaptiveColorTimer.Start();
    }

    private void StopAdaptiveColorTimer() => _adaptiveColorTimer?.Stop();

    /// <summary>采样歌词实际绘制区域（渲染器上报边界，环带 36px）周围的环境亮度，
    /// 无文本时回退窗口外圈；经滞回判定黑/白文字色，判定不变则跳过重绘。</summary>
    private void RefreshAdaptiveColor()
    {
        if (!_isOverlayVisible || ViewModel.Style.UseCustomColor) return;

        bool sampled;
        double luminance = 0;
        if (_renderer?.LastTextBounds is { } bounds && bounds.Width > 1 && bounds.Height > 1)
        {
            (int X, int Y, int Width, int Height) screen = MapToScreen(bounds);
            sampled = screen.Width > 0 &&
                DesktopLyricsAdaptiveColor.TrySampleRing(screen.X, screen.Y, screen.Width, screen.Height, out luminance);
        }
        else
        {
            sampled = DesktopLyricsAdaptiveColor.TrySampleBackgroundLuminance(_hwnd, out luminance);
        }
        if (!sampled) return;

        bool isDark;
        if (_adaptiveIsDarkBackground is { } previous)
        {
            // 滞回：只有明确越过阈值±滞回带才翻转，边界值每秒重采的抖动不切换
            isDark = previous
                ? luminance < AdaptiveSwitchThreshold + AdaptiveHysteresis
                : luminance < AdaptiveSwitchThreshold - AdaptiveHysteresis;
        }
        else
        {
            isDark = luminance < AdaptiveSwitchThreshold;
        }

        if (_adaptiveIsDarkBackground == isDark) return;
        _adaptiveIsDarkBackground = isDark;
        _lastAdaptiveTextColor = isDark ? Colors.White : Colors.Black;
        ApplyEffectiveStyle();
    }

    /// <summary>元素坐标（DIP，相对窗口内容根）→ 屏幕物理像素矩形。
    /// 与 ControlPanel 命中测试同款换算：ClientToScreen 客户区原点 + XamlRoot 光栅化缩放。</summary>
    private (int X, int Y, int Width, int Height) MapToScreen(Rect elementBounds)
    {
        double scale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
        var origin = new WindowHelper.POINT();
        if (!WindowHelper.ClientToScreen(_hwnd, ref origin)) return default;
        return (
            origin.X + (int)Math.Round(elementBounds.X * scale),
            origin.Y + (int)Math.Round(elementBounds.Y * scale),
            (int)Math.Ceiling(elementBounds.Width * scale),
            (int)Math.Ceiling(elementBounds.Height * scale));
    }

    /// <summary>
    /// 按逐字效果开关选择/热切换渲染器。切换时旧渲染器销毁（内容从可视树摘除并释放 Win2D 资源），
    /// 新渲染器应用当前样式；歌词/进度快照由调用方经 LyricsSyncRequestBus.Request() 重拉。
    /// </summary>
    private void EnsureRenderer(bool karaoke)
    {
        if (_renderer is not null && (_renderer is CanvasLyricsRenderer) == karaoke) return;

        if (_renderer is not null)
        {
            RendererHost.Content = null;
            _renderer.Dispose();
        }
        _renderer = karaoke ? new CanvasLyricsRenderer() : new TextBlockLyricsRenderer();
        _renderer.SetSuspended(!_isOverlayVisible);
        RendererHost.Content = _renderer.Content;
        ApplyEffectiveStyle();
    }

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(DesktopLyricsViewModel.IsLocked):
                    if (_isOverlayVisible) ApplyLock(ViewModel.IsLocked);
                    break;
                case nameof(DesktopLyricsViewModel.IsKaraokeEnabled):
                    if (_mode != DesktopLyricsMode.Taskbar)
                    {
                        EnsureRenderer(ViewModel.IsKaraokeEnabled);
                        LyricsSyncRequestBus.Request();   // 新渲染器重拉歌词/进度全量快照
                    }
                    break;
                case nameof(DesktopLyricsViewModel.Style):
                    UpdateAdaptiveColorMode();
                    break;
            }
        }

        private void OnAppViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_mode != DesktopLyricsMode.Taskbar) return;
            if (e.PropertyName == nameof(AppViewModel.DesktopLyricsTaskbarLyricsWidth))
            {
                if (DispatcherQueue.HasThreadAccess)
                    UpdateTaskbarLyricsWidth();
                else
                    DispatcherQueue.TryEnqueue(UpdateTaskbarLyricsWidth);
                return;
            }
            if (e.PropertyName is nameof(AppViewModel.CurrentPlayingMusic)
                or nameof(AppViewModel.IsPlaying)
                or nameof(AppViewModel.IsPlaybackEngineReady))
            {
                if (DispatcherQueue.HasThreadAccess)
                    UpdateTaskbarMedia();
                else
                    DispatcherQueue.TryEnqueue(UpdateTaskbarMedia);
                UpdateTaskbarSpectrumState();
            }
        }

        private void UpdateTaskbarLyricsWidth()
        {
            if (_mode != DesktopLyricsMode.Taskbar || _disposed) return;
            _taskbarHost?.SetLyricsWidth(AppViewModel.DesktopLyricsTaskbarLyricsWidth);
        }

        private void UpdateTaskbarMedia()
        {
            if (_mode != DesktopLyricsMode.Taskbar) return;

            Music? music = AppViewModel.CurrentPlayingMusic;
            bool visible = music is not null;
            TaskbarMediaPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (!visible)
            {
                TaskbarTitleText.Text = string.Empty;
                TaskbarArtistText.Text = string.Empty;
                RefreshTaskbarCover(null);
                SetTaskbarMediaHover(false);
                SetTaskbarCoverHover(false);
                return;
            }

            TaskbarTitleText.Text = music!.Title;
            TaskbarArtistText.Text = string.IsNullOrWhiteSpace(music.Author)
                ? music.Album
                : string.IsNullOrWhiteSpace(music.Album)
                    ? music.Author
                    : $"{music.Author} · {music.Album}";
            RefreshTaskbarCover(music);
        }

        private void RefreshTaskbarCover(Music? music)
        {
            if (_mode != DesktopLyricsMode.Taskbar || ReferenceEquals(_taskbarCoverMusic, music)) return;

            _taskbarCoverCts?.Cancel();
            _taskbarCoverCts?.Dispose();
            _taskbarCoverCts = null;
            _taskbarCoverMusic = music;
            ReplaceTaskbarCover(null);
            if (music is null) return;

            var cts = new CancellationTokenSource();
            _taskbarCoverCts = cts;
            _ = LoadTaskbarCoverAsync(music, cts);
        }

        private async Task LoadTaskbarCoverAsync(Music music, CancellationTokenSource cts)
        {
            try
            {
                ImageSource? source = await CoverLoadQueue.EnqueueAsync(music, cts.Token);
                if (cts.IsCancellationRequested || !ReferenceEquals(_taskbarCoverCts, cts)
                    || !ReferenceEquals(_taskbarCoverMusic, music) || _disposed)
                {
                    (source as IDisposable)?.Dispose();
                    return;
                }
                if (DispatcherQueue.HasThreadAccess)
                {
                    ReplaceTaskbarCover(source);
                }
                else if (!DispatcherQueue.TryEnqueue(() =>
                    {
                        if (cts.IsCancellationRequested || !ReferenceEquals(_taskbarCoverCts, cts)
                            || !ReferenceEquals(_taskbarCoverMusic, music) || _disposed)
                            (source as IDisposable)?.Dispose();
                        else
                            ReplaceTaskbarCover(source);
                    }))
                {
                    (source as IDisposable)?.Dispose();
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DesktopLyricsWindow] taskbar cover load failed: {ex}");
            }
        }

        private void ReplaceTaskbarCover(ImageSource? source)
        {
            (TaskbarCoverImage.Source as IDisposable)?.Dispose();
            TaskbarCoverImage.Source = source;
            TaskbarCoverPlaceholder.Visibility = source is null ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>恢复默认尺寸并置于主屏工作区底部居中（重置按钮调用）。</summary>
        public void ApplyDefaultBounds()
        {
            if (_mode == DesktopLyricsMode.Taskbar)
            {
                _taskbarHost?.ResetUserBounds();
                ViewModel.PersistBounds();
                return;
            }
            var bounds = ViewModel.BoundsState;
            var work = DisplayArea.Primary.WorkArea;
            int x = work.X + (work.Width - DefaultWidth) / 2;
            int y = work.Y + work.Height - DefaultHeight - BottomMargin;
            WindowSizeHelper.MoveAndResizeExact(AppWindow, x, y, DefaultWidth, DefaultHeight);
            bounds.HasBounds = true;
            bounds.X = x;
            bounds.Y = y;
            bounds.Width = DefaultWidth;
            bounds.Height = DefaultHeight;
            ViewModel.PersistBounds();
        }

        public void Dispose()
        {
            if (!_disposed) Close();
        }

        private void ConfigureWindow()
        {
            // MainWindow 同款组合：内容延伸进标题栏 + SetBorderAndTitleBar(true,false) 移除
            // 系统标题栏与右上角系统按钮，保留边框与边缘调整大小。此为解锁态基线，
            // 锁定时在其上做 GWL_STYLE 切换（见 ApplyLock）。
            AppWindow.TitleBar.PreferredTheme = TitleBarTheme.UseDefaultAppMode;
            AppWindow.TitleBar.ExtendsContentIntoTitleBar = true;
            AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
            this.SetTitleBarBackgroundColors(Colors.Transparent);
            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.SetBorderAndTitleBar(true, false);
                presenter.IsAlwaysOnTop = _mode == DesktopLyricsMode.Floating;
            }
            AppWindow.IsShownInSwitchers = false;

            if (_mode == DesktopLyricsMode.Taskbar)
            {
                // 任务栏宿主是客户区子窗口，不能让 WinUI 的自定义标题栏命中测试覆盖媒体按钮。
                // 宿主稍后还会移除 WS_CAPTION/WS_BORDER；这里同步清掉 XAML 标题栏意图。
                AppWindow.TitleBar.ExtendsContentIntoTitleBar = false;
                if (AppWindow.Presenter is OverlappedPresenter taskbarPresenter)
                    taskbarPresenter.SetBorderAndTitleBar(false, false);
                return;
            }

            var bounds = ViewModel.BoundsState;
            int width = bounds.Width > 0 ? bounds.Width : DefaultWidth;
            int height = bounds.Height > 0 ? bounds.Height : DefaultHeight;
            if (bounds.HasBounds &&
                WindowSizeHelper.IsBoundsOnScreen(bounds.X, bounds.Y, width, height))
            {
                // 多显示器：IsBoundsOnScreen 遍历所有显示器 WorkArea 校验可见性。
                // 跨 DPI 显示器还原需先 Move 后 Resize,见 WindowSizeHelper.MoveAndResizeExact
                WindowSizeHelper.MoveAndResizeExact(AppWindow, bounds.X, bounds.Y, width, height);
            }
            else
            {
                ApplyDefaultBounds();
            }
        }

        private void ApplyClickThrough(bool enable)
        {
            if (_clickThrough == enable) return;
            _clickThrough = enable;
            WindowHelper.SetClickThrough(_hwnd, enable);
        }

        private void UpdateControlPanelVisual()
        {
            if (_mode == DesktopLyricsMode.Taskbar)
            {
                StopHoverTimer();
                StopIdleTimer();
                ControlPanel.Opacity = _locked ? 0 : 1;
                ControlPanel.IsHitTestVisible = !_locked;
                return;
            }
            if (_locked && _isOverlayVisible)
            {
                _cursorOverPanel = false;
                ControlPanel.Opacity = 0;
                ControlPanel.IsHitTestVisible = false;
                StartIdleTimer();
            }
            else
            {
                StopHoverTimer();
                StopIdleTimer();
                ControlPanel.Opacity = 1;
                ControlPanel.IsHitTestVisible = true;
            }
        }

        private void StartHoverTimer()
        {
            if (_hoverTimer is null)
            {
                _hoverTimer = DispatcherQueue.CreateTimer();
                _hoverTimer.Interval = TimeSpan.FromMilliseconds(HoverPollingIntervalMs);
                _hoverTimer.Tick += OnHoverTimerTick;
            }
            _hoverTimer.Start();
        }

        private void StopHoverTimer()
        {
            _hoverTimer?.Stop();
        }

        private void StartIdleTimer()
        {
            if (_idleTimer is null)
            {
                _idleTimer = DispatcherQueue.CreateTimer();
                _idleTimer.Interval = TimeSpan.FromMilliseconds(IdlePollingIntervalMs);
                _idleTimer.Tick += OnIdleTimerTick;
            }
            _idleTimer.Start();
        }

        private void StopIdleTimer()
        {
            _idleTimer?.Stop();
        }

        /// <summary>锁定态静默期轮询（200ms）：自愈 + 进窗检测；一旦发现光标悬停窗口即切入 50ms 快轮询。</summary>
        private void OnIdleTimerTick(DispatcherQueueTimer sender, object args)
        {
            if (_mode == DesktopLyricsMode.Taskbar)
            {
                sender.Stop();
                return;
            }
            if (!_locked || !_isOverlayVisible)
            {
                sender.Stop();
                return;
            }
            // 自愈：被前后台切换偶发置为不可见/最小化的穿透窗口不进任务栏、无恢复入口（表现为歌词消失），
            // 检测到即无焦点拉回并重申置顶
            if (!WindowHelper.IsWindowVisible(_hwnd) || WindowHelper.IsIconic(_hwnd))
            {
                WindowHelper.RestoreOverlay(_hwnd);
            }
            if (WindowHelper.GetCursorPos(out WindowHelper.POINT cursor) && IsCursorOverWindow(cursor))
            {
                ControlPanel.Opacity = 1.0;   // 悬停窗口 = 仅显示按钮组（穿透保持，绝不因进入窗口而取消）
                ControlPanel.IsHitTestVisible = true;
                StartHoverTimer();
            }
        }

        private void OnHoverTimerTick(DispatcherQueueTimer sender, object args)
        {
            // 离开窗口：还原按钮组与穿透，停快轮询，回到慢速自愈轮询
            if (!_isOverlayVisible || !_locked || !WindowHelper.GetCursorPos(out WindowHelper.POINT cursor) || !IsCursorOverWindow(cursor))
            {
                sender.Stop();
                _cursorOverPanel = false;
                ControlPanel.Opacity = 0;
                ControlPanel.IsHitTestVisible = false;
                ApplyClickThrough(true);
                return;
            }
            // 光标移到按钮上 = 临时取消穿透供点击；离开按钮立即恢复穿透
            bool overPanel = IsCursorOverControlPanel(cursor);
            if (overPanel != _cursorOverPanel)
            {
                _cursorOverPanel = overPanel;
                ApplyClickThrough(!overPanel);
            }
        }

        /// <summary>游标是否悬停在本窗口上（AppWindow.Position/Size 与 GetCursorPos 同为物理像素）。</summary>
        private bool IsCursorOverWindow(WindowHelper.POINT cursor)
        {
            PointInt32 pos = AppWindow.Position;
            SizeInt32 size = AppWindow.Size;
            return cursor.X >= pos.X && cursor.X < pos.X + size.Width
                && cursor.Y >= pos.Y && cursor.Y < pos.Y + size.Height;
        }

        /// <summary>
        /// 游标是否悬停在右上角按钮组上。屏幕矩形按窗口位置/尺寸变化缓存
        /// （见 <see cref="InvalidatePanelScreenRect"/>）：锁定态轮询期间窗口静止，
        /// 命中缓存时纯数值比较，避免每 tick 的 TransformToVisual 分配与 XAML 调用。
        /// </summary>
        private bool IsCursorOverControlPanel(WindowHelper.POINT cursor)
        {
            if (_panelScreenRectCache is { } cached)
            {
                return cursor.X >= cached.X && cursor.X <= cached.X + cached.Width
                    && cursor.Y >= cached.Y && cursor.Y <= cached.Y + cached.Height;
            }
            if (ControlPanel.ActualWidth <= 0 || RootGrid.XamlRoot is null) return false;
            double scale = RootGrid.XamlRoot.RasterizationScale;
            Rect bounds = ControlPanel.TransformToVisual(null)
                .TransformBounds(new Rect(0, 0, ControlPanel.ActualWidth, ControlPanel.ActualHeight));
            var origin = new WindowHelper.POINT();
            if (!WindowHelper.ClientToScreen(_hwnd, ref origin)) return false;
            int left = origin.X + (int)((bounds.X - ControlPanelHoverMargin) * scale);
            int top = origin.Y + (int)((bounds.Y - ControlPanelHoverMargin) * scale);
            int right = origin.X + (int)((bounds.X + bounds.Width + ControlPanelHoverMargin) * scale);
            int bottom = origin.Y + (int)((bounds.Y + bounds.Height + ControlPanelHoverMargin) * scale);
            _panelScreenRectCache = new RectInt32(left, top, right - left, bottom - top);
            return cursor.X >= left && cursor.X <= right && cursor.Y >= top && cursor.Y <= bottom;
        }

        /// <summary>按钮组屏幕矩形依赖窗口位置/尺寸/DPI，变化后需重算。</summary>
        private void InvalidatePanelScreenRect() => _panelScreenRectCache = null;

        private void LockButton_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.IsLocked = !ViewModel.IsLocked;
        }

        private void ResetButton_Click(object sender, RoutedEventArgs e)
        {
            DesktopLyricsManager.ResetWindowBounds();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.IsEnabled = false;
        }

        // ==== 手动拖动：按住任意位置拖动（GetCursorPos 与 AppWindow.Position 均为物理像素） ====

        private void RootGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (_mode == DesktopLyricsMode.Taskbar)
            {
                if (_locked || !e.GetCurrentPoint(RootGrid).Properties.IsLeftButtonPressed
                    || IsTaskbarInteractiveSource(e.OriginalSource as DependencyObject)
                    || _taskbarHost is null || !_taskbarHost.TryGetBounds(out _taskbarStartBounds))
                    return;
                if (!WindowHelper.GetCursorPos(out _taskbarStartCursor)) return;
                _taskbarResizeEdge = GetTaskbarResizeEdge(e.GetCurrentPoint(RootGrid).Position.X,
                    e.GetCurrentPoint(RootGrid).Position.Y);
                _isTaskbarManipulating = true;
                RootGrid.CapturePointer(e.Pointer);
                e.Handled = true;
                return;
            }
            if (_locked) return;
            if (!e.GetCurrentPoint(RootGrid).Properties.IsLeftButtonPressed) return;
            if (!WindowHelper.GetCursorPos(out _dragStartCursor)) return;
            _dragStartWindowPos = AppWindow.Position;
            _isDragging = true;
            RootGrid.CapturePointer(e.Pointer);
        }

        private void RootGrid_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_mode == DesktopLyricsMode.Taskbar)
            {
                if (_isTaskbarManipulating)
                {
                    if (WindowHelper.GetCursorPos(out WindowHelper.POINT cursor))
                    {
                        int dx = cursor.X - _taskbarStartCursor.X;
                        int dy = cursor.Y - _taskbarStartCursor.Y;
                        _taskbarHost?.SetUserBounds(AdjustTaskbarBounds(dx, dy));
                    }
                    e.Handled = true;
                    return;
                }
                if (!_locked && !IsTaskbarInteractiveSource(e.OriginalSource as DependencyObject))
                {
                    var point = e.GetCurrentPoint(RootGrid).Position;
                    SetTaskbarCursor(GetTaskbarResizeEdge(point.X, point.Y));
                }
                else if (IsTaskbarInteractiveSource(e.OriginalSource as DependencyObject))
                {
                    SetTaskbarCursor(TaskbarResizeEdge.None);
                }
                return;
            }
            if (!_isDragging || !WindowHelper.GetCursorPos(out WindowHelper.POINT floatingCursor)) return;
            AppWindow.Move(new PointInt32(
                _dragStartWindowPos.X + floatingCursor.X - _dragStartCursor.X,
                _dragStartWindowPos.Y + floatingCursor.Y - _dragStartCursor.Y));
        }

        private void RootGrid_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_mode == DesktopLyricsMode.Taskbar && _isTaskbarManipulating)
            {
                EndTaskbarManipulation(e);
                return;
            }
            EndDrag(e);
        }

        private void RootGrid_PointerCanceled(object sender, PointerRoutedEventArgs e)
        {
            if (_mode == DesktopLyricsMode.Taskbar && _isTaskbarManipulating)
            {
                EndTaskbarManipulation(e);
                return;
            }
            EndDrag(e);
        }

        private void RootGrid_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            _isDragging = false;
            if (_mode == DesktopLyricsMode.Taskbar)
            {
                if (_isTaskbarManipulating)
                    ViewModel.PersistBounds();
                _isTaskbarManipulating = false;
                _taskbarResizeEdge = TaskbarResizeEdge.None;
                SetTaskbarCursor(TaskbarResizeEdge.None);
            }
        }

        private TaskbarWindowBounds AdjustTaskbarBounds(int dx, int dy)
        {
            var bounds = _taskbarStartBounds;
            if ((_taskbarResizeEdge & TaskbarResizeEdge.Left) != 0)
            {
                bounds = bounds with { X = bounds.X + dx, Width = bounds.Width - dx };
            }
            else if ((_taskbarResizeEdge & TaskbarResizeEdge.Right) != 0)
            {
                bounds = bounds with { Width = bounds.Width + dx };
            }
            else if (_taskbarResizeEdge == TaskbarResizeEdge.None)
            {
                bounds = bounds with { X = bounds.X + dx, Y = bounds.Y + dy };
            }

            if ((_taskbarResizeEdge & TaskbarResizeEdge.Top) != 0)
                bounds = bounds with { Y = bounds.Y + dy, Height = bounds.Height - dy };
            else if ((_taskbarResizeEdge & TaskbarResizeEdge.Bottom) != 0)
                bounds = bounds with { Height = bounds.Height + dy };
            return bounds;
        }

        private TaskbarResizeEdge GetTaskbarResizeEdge(double x, double y)
        {
            const double edge = 10;
            TaskbarResizeEdge result = TaskbarResizeEdge.None;
            if (x <= edge) result |= TaskbarResizeEdge.Left;
            else if (x >= RootGrid.ActualWidth - edge) result |= TaskbarResizeEdge.Right;
            if (y <= edge) result |= TaskbarResizeEdge.Top;
            else if (y >= RootGrid.ActualHeight - edge) result |= TaskbarResizeEdge.Bottom;
            return result;
        }

        private void SetTaskbarCursor(TaskbarResizeEdge edge)
        {
            if (_locked || edge == TaskbarResizeEdge.None)
            {
                _taskbarCursorShape = null;
                _taskbarCursor = null;
                RootGrid.SetCursor(null);
                return;
            }
            InputSystemCursorShape shape = edge switch
            {
                TaskbarResizeEdge.Left or TaskbarResizeEdge.Right => InputSystemCursorShape.SizeWestEast,
                TaskbarResizeEdge.Top or TaskbarResizeEdge.Bottom => InputSystemCursorShape.SizeNorthSouth,
                TaskbarResizeEdge.Left | TaskbarResizeEdge.Top
                    or TaskbarResizeEdge.Right | TaskbarResizeEdge.Bottom => InputSystemCursorShape.SizeNorthwestSoutheast,
                _ => InputSystemCursorShape.SizeNortheastSouthwest,
            };
            if (_taskbarCursorShape != shape)
            {
                _taskbarCursorShape = shape;
                _taskbarCursor = InputSystemCursor.Create(shape);
            }
            RootGrid.SetCursor(_taskbarCursor);
        }

        private static bool IsTaskbarInteractiveSource(DependencyObject? source)
        {
            while (source is not null)
            {
                if (source is ButtonBase or RangeBase) return true;
                source = VisualTreeHelper.GetParent(source);
            }
            return false;
        }

        private void EndTaskbarManipulation(PointerRoutedEventArgs e)
        {
            ViewModel.PersistBounds();
            _isTaskbarManipulating = false;
            _taskbarResizeEdge = TaskbarResizeEdge.None;
            RootGrid.ReleasePointerCapture(e.Pointer);
            SetTaskbarCursor(TaskbarResizeEdge.None);
            e.Handled = true;
        }

        private void EndDrag(PointerRoutedEventArgs e)
        {
            if (!_isDragging) return;
            _isDragging = false;
            RootGrid.ReleasePointerCapture(e.Pointer);
        }

        // ==== 数据总线转发 ====

        private void OnUILyricsChanged(IList<LyricLine>? value)
        {
            if (_isOverlayVisible) _renderer?.SetLyrics(value);
        }

        private void OnTimeProgressChanged(long totalMs)
        {
            if (_isOverlayVisible) _renderer?.SetPlaybackTime(totalMs);
        }

        private void OnOffsetChanged(double value)
        {
            if (_isOverlayVisible) _renderer?.SetOffset(value);
        }

        private void OnIsPlayingChanged(bool value)
        {
            if (_isOverlayVisible) _renderer?.SetIsPlaying(value);
        }

        private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
        {
            if (_mode == DesktopLyricsMode.Taskbar) return;
            // z 序变动后若被挤出置顶层（其他置顶窗口切换可致），幂等重申，防"被盖住"表现为消失
            if (_isOverlayVisible && args.DidZOrderChange) WindowHelper.EnsureTopmost(_hwnd);
            if (!args.DidPositionChange && !args.DidSizeChange) return;
            InvalidatePanelScreenRect();
            var bounds = ViewModel.BoundsState;
            if (args.DidPositionChange)
            {
                PointInt32 pos = sender.Position;
                bounds.HasBounds = true;
                bounds.X = pos.X;
                bounds.Y = pos.Y;
            }
            if (args.DidSizeChange)
            {
                SizeInt32 size = sender.Size;
                bounds.Width = size.Width;
                bounds.Height = size.Height;
            }
            // 不在此处落盘：按约定仅在关闭窗口 / ApplyDefaultBounds（HasBounds 建立）/ 退出时记录
        }

        private void MainWindow_themeChanged(object? sender, EventArgs e)
        {
            _themeStyleHelper?.SetAppTheme();
            UpdateTaskbarSpectrumBrush();
        }

        private void OnWindowClosed(object sender, WindowEventArgs args)
        {
            Bindings?.StopTracking();
            if (App.MainWindow is not null)
            {
                App.MainWindow.themeChanged -= MainWindow_themeChanged;
            }
            UILyricsBus.Changed -= OnUILyricsChanged;
            TimeProgressBus.CurrentPlayingTimeChanged -= OnTimeProgressChanged;
            OffsetMsBus.Changed -= OnOffsetChanged;
            IsPlayingBus.Changed -= OnIsPlayingChanged;
            AppWindow.Changed -= OnAppWindowChanged;
            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            AppViewModel.PropertyChanged -= OnAppViewModelPropertyChanged;
            if (_taskbarFftRequested)
            {
                _taskbarFftRequested = false;
                PlayerService.SetFftEnabled(false);
            }
            _taskbarSpectrumTimer?.Stop();
            _taskbarSpectrumTimer = null;
            Closed -= OnWindowClosed;
            StopHoverTimer();
            StopIdleTimer();
            StopAdaptiveColorTimer();
            _taskbarCoverCts?.Cancel();
            _taskbarCoverCts?.Dispose();
            _taskbarCoverCts = null;
            _taskbarCoverMusic = null;
            ReplaceTaskbarCover(null);
            ViewModel.PersistBounds();
            _renderer?.Dispose();
            _renderer = null;
            _disposed = true;
        }
    }
}
