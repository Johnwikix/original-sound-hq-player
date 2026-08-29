using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl;
using AnimatedWin2dControls.Messages;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Windows.Foundation;
using Windows.Graphics;
using WinUIEx;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;

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
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_TRANSPARENT = 0x00000020;

        private const int DefaultWidth = 1800;
        private const int DefaultHeight = 280;
        private const int BottomMargin = 60;
        private const double HoverPollingIntervalMs = 50;
        private const double ControlPanelHoverMargin = 6.0;

        private readonly IDesktopLyricsRenderer _renderer;
        private readonly IntPtr _hwnd;
        private bool _locked = true;
        private bool _clickThrough;              // 当前穿透样式状态（false = 尚未设置）
        private bool _cursorOverWindow;
        private bool _cursorOverPanel;
        private DispatcherQueueTimer? _hoverTimer;
        private WindowStyle? _originalWindowStyle;   // 首次锁定前缓存的解锁态样式
        private bool _disposed;

        private bool _isDragging;
        private POINT _dragStartCursor;
        private PointInt32 _dragStartWindowPos;

        public DesktopLyricsWindow()
        {
            InitializeComponent();
            _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

            // 渲染器接入点：未来接入逐字效果时，替换为包装 AdvanceLyricsCanvasControl 的 IDesktopLyricsRenderer 实现
            _renderer = new TextBlockLyricsRenderer();
            RendererHost.Content = _renderer.Content;
            _renderer.SetStyle(DesktopLyricsManager.GetCurrentStyle());

            // 复用 WinUIEx 自带的完全透明背景（与主程序"透明"样式同源）
            SystemBackdrop = new TransparentTintBackdrop();
            ConfigureWindow();

            UILyricsBus.Changed += OnUILyricsChanged;
            TimeProgressBus.CurrentPlayingTimeChanged += OnTimeProgressChanged;
            OffsetMsBus.Changed += OnOffsetChanged;
            IsPlayingBus.Changed += OnIsPlayingChanged;
            AppWindow.Changed += OnAppWindowChanged;
            Closed += OnWindowClosed;

            // 拉取全量歌词/进度/样式状态（AppViewModel.SendFullLyricsSync）
            LyricsSyncRequestBus.Request();
        }

        /// <summary>由 DesktopLyricsManager 在创建后与锁定状态变化时调用（幂等）。</summary>
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
            UpdateControlPanelVisual();
        }

        /// <summary>应用桌面歌词独立样式（设置页变更 / 初始化时由 Manager 推送）。</summary>
        public void ApplyStyle(DesktopLyricsStyle style) => _renderer.SetStyle(style);

        /// <summary>恢复默认尺寸并置于主屏工作区底部居中（重置按钮调用）。</summary>
        public void ApplyDefaultBounds()
        {
            var bounds = DesktopLyricsManager.BoundsState;
            var work = DisplayArea.Primary.WorkArea;
            int x = work.X + (work.Width - DefaultWidth) / 2;
            int y = work.Y + work.Height - DefaultHeight - BottomMargin;
            AppWindow.MoveAndResize(new RectInt32(x, y, DefaultWidth, DefaultHeight));
            bounds.HasBounds = true;
            bounds.X = x;
            bounds.Y = y;
            bounds.Width = DefaultWidth;
            bounds.Height = DefaultHeight;
            DesktopLyricsManager.PersistBounds();
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

            var bounds = DesktopLyricsManager.BoundsState;
            int width = bounds.Width > 0 ? bounds.Width : DefaultWidth;
            int height = bounds.Height > 0 ? bounds.Height : DefaultHeight;
            if (bounds.HasBounds &&
                WindowSizeHelper.IsBoundsOnScreen(bounds.X, bounds.Y, width, height))
            {
                // 多显示器：IsBoundsOnScreen 遍历所有显示器 WorkArea 校验可见性
                AppWindow.MoveAndResize(new RectInt32(bounds.X, bounds.Y, width, height));
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
            int exStyle = (int)WindowHelper.GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
            if (enable)
                exStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT;
            else
                exStyle &= ~(WS_EX_LAYERED | WS_EX_TRANSPARENT);
            WindowHelper.SetWindowLongPtr(_hwnd, GWL_EXSTYLE, exStyle);
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
            if (!GetCursorPos(out POINT cursor)) return;

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
        private bool IsCursorOverWindow(POINT cursor)
        {
            PointInt32 pos = AppWindow.Position;
            SizeInt32 size = AppWindow.Size;
            return cursor.X >= pos.X && cursor.X < pos.X + size.Width
                && cursor.Y >= pos.Y && cursor.Y < pos.Y + size.Height;
        }

        /// <summary>游标是否悬停在右上角按钮组上（XAML 坐标 × RasterizationScale + ClientToScreen 换算屏幕矩形）。</summary>
        private bool IsCursorOverControlPanel(POINT cursor)
        {
            if (ControlPanel.ActualWidth <= 0 || RootGrid.XamlRoot is null) return false;
            double scale = RootGrid.XamlRoot.RasterizationScale;
            Rect bounds = ControlPanel.TransformToVisual(null)
                .TransformBounds(new Rect(0, 0, ControlPanel.ActualWidth, ControlPanel.ActualHeight));
            var origin = new POINT();
            if (!ClientToScreen(_hwnd, ref origin)) return false;
            int left = origin.X + (int)((bounds.X - ControlPanelHoverMargin) * scale);
            int top = origin.Y + (int)((bounds.Y - ControlPanelHoverMargin) * scale);
            int right = origin.X + (int)((bounds.X + bounds.Width + ControlPanelHoverMargin) * scale);
            int bottom = origin.Y + (int)((bounds.Y + bounds.Height + ControlPanelHoverMargin) * scale);
            return cursor.X >= left && cursor.X <= right && cursor.Y >= top && cursor.Y <= bottom;
        }

        private void LockButton_Click(object sender, RoutedEventArgs e)
        {
            DesktopLyricsManager.SetLocked(!DesktopLyricsManager.IsLocked);
        }

        private void ResetButton_Click(object sender, RoutedEventArgs e)
        {
            DesktopLyricsManager.ResetWindowBounds();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            DesktopLyricsManager.SetEnabled(false);
        }

        // ==== 手动拖动：按住任意位置拖动（GetCursorPos 与 AppWindow.Position 均为物理像素） ====

        private void RootGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (_locked) return;
            if (!e.GetCurrentPoint(RootGrid).Properties.IsLeftButtonPressed) return;
            if (!GetCursorPos(out _dragStartCursor)) return;
            _dragStartWindowPos = AppWindow.Position;
            _isDragging = true;
            RootGrid.CapturePointer(e.Pointer);
        }

        private void RootGrid_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_isDragging || !GetCursorPos(out POINT cursor)) return;
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

        private void OnUILyricsChanged(IList<LyricLine>? value) => _renderer.SetLyrics(value);

        private void OnTimeProgressChanged(long totalMs) => _renderer.SetPlaybackTime(totalMs);

        private void OnOffsetChanged(double value) => _renderer.SetOffset(value);

        private void OnIsPlayingChanged(bool value) => _renderer.SetIsPlaying(value);

        private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
        {
            if (!args.DidPositionChange && !args.DidSizeChange) return;
            var bounds = DesktopLyricsManager.BoundsState;
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
            Closed -= OnWindowClosed;
            StopHoverTimer();
            DesktopLyricsManager.PersistBounds();
            _renderer.Dispose();
            _disposed = true;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);
    }
}
