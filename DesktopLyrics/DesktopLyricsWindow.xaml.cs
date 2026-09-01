using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl;
using AnimatedWin2dControls.Messages;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using Windows.Foundation;
using Windows.Graphics;
using WinUIEx;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.ViewModel;
using Windows.UI;

namespace WinUIMusicPlayer.DesktopLyrics
{
    /// <summary>
    /// 桌面歌词悬浮窗：透明、置顶、不进任务栏/Alt-Tab。
    /// 基类与 spectrum 一致使用 WinUIEx.WindowEx。
    /// 解锁态：标准窗口（标题栏 + 可调整大小），按住内容区任意位置拖动；
    /// 锁定态：GWL_STYLE 移除标题栏/边框位 + OR-in WS_POPUP（WinUIEx ToggleWindowStyle，
    /// 含 SWP_FRAMECHANGED）+ 整窗点击穿透常开；鼠标悬停窗口时仅"显示"右上角按钮组，
    /// 光标移到按钮上才临时取消穿透供点击（定时器轮询游标位置，BetterLyrics OverlayInputHelper 思路）。
    /// </summary>
    public sealed partial class DesktopLyricsWindow : WinUIEx.WindowEx, IDisposable
    {
        private const int DefaultWidth = 1800;
        private const int DefaultHeight = 280;
        private const int BottomMargin = 60;
        private const double HoverPollingIntervalMs = 50;
        private const double ControlPanelHoverMargin = 6.0;
        private const double AdaptiveSamplingIntervalMs = 1000;   // 环境取色轮询周期（BetterLyrics 同款 1s）

        private IDesktopLyricsRenderer? _renderer;
        private readonly IntPtr _hwnd;

        /// <summary>桌面歌词状态源（锁定图标绑定 / 按钮处理 / 边界与样式读写）。</summary>
        public DesktopLyricsViewModel ViewModel { get; } = App.Services.GetRequiredService<DesktopLyricsViewModel>();
        private bool _locked = true;
        private bool _clickThrough;              // 当前穿透样式状态（false = 尚未设置）
        private bool _cursorOverWindow;
        private bool _cursorOverPanel;
        private DispatcherQueueTimer? _hoverTimer;
        private WindowStyle? _originalWindowStyle;   // 首次锁定前缓存的解锁态样式
        private bool _disposed;

        private bool _isDragging;
        private WindowHelper.POINT _dragStartCursor;
        private PointInt32 _dragStartWindowPos;
        private RectInt32? _panelScreenRectCache;   // 按钮组屏幕矩形缓存（含悬停外扩）；窗口位置/尺寸变化时失效
        private DispatcherQueueTimer? _adaptiveColorTimer;
        private Color? _lastAdaptiveTextColor;      // 上次应用的取色结果（相同则跳过重绘）

        public DesktopLyricsWindow()
        {
            InitializeComponent();
            _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

            // 渲染器按"逐字效果"开关选择：CanvasLyricsRenderer（Win2D 逐字扫光）或
            // TextBlockLyricsRenderer（文本描边）。开关变化经 PropertyChanged 热切换（EnsureRenderer）。
            EnsureRenderer(ViewModel.IsKaraokeEnabled);

            // 复用 WinUIEx 自带的完全透明背景（与主程序"透明"样式同源）
            SystemBackdrop = new TransparentTintBackdrop();
            ConfigureWindow();
            UpdateAdaptiveColorMode();

            UILyricsBus.Changed += OnUILyricsChanged;
            TimeProgressBus.CurrentPlayingTimeChanged += OnTimeProgressChanged;
            OffsetMsBus.Changed += OnOffsetChanged;
            IsPlayingBus.Changed += OnIsPlayingChanged;
            AppWindow.Changed += OnAppWindowChanged;
            ViewModel.PropertyChanged += OnViewModelPropertyChanged;
            Closed += OnWindowClosed;

            // 拉取全量歌词/进度/样式状态（AppViewModel.SendFullLyricsSync）
            LyricsSyncRequestBus.Request();
        }

        /// <summary>窗口创建后由 Manager 以 VM 初值调用一次；后续锁定变化经 ViewModel.PropertyChanged 触发（幂等）。</summary>
        public void ApplyLock(bool locked)
        {
            _locked = locked;
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
        }

    /// <summary>
    /// 把有效样式推给渲染器：自定义颜色覆盖开启时用样式原色，
    /// 否则用环境取色结果（黑/白）覆盖样式颜色（悬浮窗默认跟随背景）。
    /// </summary>
    private void ApplyEffectiveStyle()
    {
        if (_renderer is null) return;
        DesktopLyricsStyle style = ViewModel.Style;
        if (!style.UseCustomColor && _lastAdaptiveTextColor is { } adaptive)
        {
            style = style with { Color = adaptive };
        }
        _renderer.SetStyle(style);
    }

    /// <summary>按样式快照的自定义颜色覆盖开关启停环境取色轮询；开关/样式变化时立即采样（或还原）一次。</summary>
    private void UpdateAdaptiveColorMode()
    {
        if (!ViewModel.Style.UseCustomColor)
        {
            StartAdaptiveColorTimer();
            RefreshAdaptiveColor();
        }
        else
        {
            StopAdaptiveColorTimer();
            _lastAdaptiveTextColor = null;
            ApplyEffectiveStyle();
        }
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

    /// <summary>采样窗口周围环境色推导黑/白文字色；与上次结果相同则跳过重绘。</summary>
    private void RefreshAdaptiveColor()
    {
        if (ViewModel.Style.UseCustomColor) return;
        if (!DesktopLyricsAdaptiveColor.TryGetAdaptiveTextColor(_hwnd, out Color textColor)) return;
        if (_lastAdaptiveTextColor == textColor) return;
        _lastAdaptiveTextColor = textColor;
        ApplyEffectiveStyle();
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
        RendererHost.Content = _renderer.Content;
        ApplyEffectiveStyle();
    }

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(DesktopLyricsViewModel.IsLocked):
                    ApplyLock(ViewModel.IsLocked);
                    break;
                case nameof(DesktopLyricsViewModel.IsKaraokeEnabled):
                    EnsureRenderer(ViewModel.IsKaraokeEnabled);
                    LyricsSyncRequestBus.Request();   // 新渲染器重拉歌词/进度全量快照
                    break;
                case nameof(DesktopLyricsViewModel.Style):
                    UpdateAdaptiveColorMode();
                    break;
            }
        }

        /// <summary>恢复默认尺寸并置于主屏工作区底部居中（重置按钮调用）。</summary>
        public void ApplyDefaultBounds()
        {
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
                presenter.IsAlwaysOnTop = true;
            }
            AppWindow.IsShownInSwitchers = false;

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
            if (_locked)
            {
                _cursorOverWindow = false;
                _cursorOverPanel = false;
                ControlPanel.Opacity = 0;
                StartHoverTimer();
            }
            else
            {
                StopHoverTimer();
                ControlPanel.Opacity = 1;
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

        private void OnHoverTimerTick(DispatcherQueueTimer sender, object args)
        {
            if (!_locked)
            {
                sender.Stop();
                return;
            }
            if (!WindowHelper.GetCursorPos(out WindowHelper.POINT cursor)) return;

            bool overWindow = IsCursorOverWindow(cursor);
            bool overPanel = overWindow && IsCursorOverControlPanel(cursor);

            // 悬停窗口 = 仅显示按钮组（穿透保持，绝不因进入窗口而取消）
            if (overWindow != _cursorOverWindow)
            {
                _cursorOverWindow = overWindow;
                ControlPanel.Opacity = overWindow ? 1.0 : 0.0;
            }
            // 光标移到按钮上 = 临时取消穿透供点击；离开按钮立即恢复穿透
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
            if (_locked) return;
            if (!e.GetCurrentPoint(RootGrid).Properties.IsLeftButtonPressed) return;
            if (!WindowHelper.GetCursorPos(out _dragStartCursor)) return;
            _dragStartWindowPos = AppWindow.Position;
            _isDragging = true;
            RootGrid.CapturePointer(e.Pointer);
        }

        private void RootGrid_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_isDragging || !WindowHelper.GetCursorPos(out WindowHelper.POINT cursor)) return;
            AppWindow.Move(new PointInt32(
                _dragStartWindowPos.X + cursor.X - _dragStartCursor.X,
                _dragStartWindowPos.Y + cursor.Y - _dragStartCursor.Y));
        }

        private void RootGrid_PointerReleased(object sender, PointerRoutedEventArgs e) => EndDrag(e);

        private void RootGrid_PointerCanceled(object sender, PointerRoutedEventArgs e) => EndDrag(e);

        private void RootGrid_PointerCaptureLost(object sender, PointerRoutedEventArgs e) => _isDragging = false;

        private void EndDrag(PointerRoutedEventArgs e)
        {
            if (!_isDragging) return;
            _isDragging = false;
            RootGrid.ReleasePointerCapture(e.Pointer);
        }

        // ==== 数据总线转发 ====

        private void OnUILyricsChanged(IList<LyricLine>? value) => _renderer?.SetLyrics(value);

        private void OnTimeProgressChanged(long totalMs) => _renderer?.SetPlaybackTime(totalMs);

        private void OnOffsetChanged(double value) => _renderer?.SetOffset(value);

        private void OnIsPlayingChanged(bool value) => _renderer?.SetIsPlaying(value);

        private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
        {
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

        private void OnWindowClosed(object sender, WindowEventArgs args)
        {
            UILyricsBus.Changed -= OnUILyricsChanged;
            TimeProgressBus.CurrentPlayingTimeChanged -= OnTimeProgressChanged;
            OffsetMsBus.Changed -= OnOffsetChanged;
            IsPlayingBus.Changed -= OnIsPlayingChanged;
            AppWindow.Changed -= OnAppWindowChanged;
            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            Closed -= OnWindowClosed;
            StopHoverTimer();
            StopAdaptiveColorTimer();
            ViewModel.PersistBounds();
            _renderer?.Dispose();
            _renderer = null;
            _disposed = true;
        }
    }
}
