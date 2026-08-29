using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl;
using AnimatedWin2dControls.Messages;
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
    /// 解锁态：保留边框可调整大小，按住任意位置拖动；
    /// 锁定态：GWL_STYLE 移除标题栏/边框（WinUIEx ToggleWindowStyle，含 SWP_FRAMECHANGED）
    /// + 整窗点击穿透，仅鼠标悬停右上角按钮组时临时恢复交互
    /// （BetterLyrics OverlayInputHelper 方案：定时器轮询游标位置切换穿透状态）。
    /// </summary>
    public sealed partial class DesktopLyricsWindow : Window, IDisposable
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
        private bool _cursorOverControlPanel;
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
                // spectrum 同款：GWL_STYLE 移除标题栏与可调边框（WinUIEx 内部含 SWP_FRAMECHANGED 立即重算）
                _originalWindowStyle ??= this.GetWindowStyle();
                this.ToggleWindowStyle(false, WindowStyle.Caption | WindowStyle.ThickFrame);
            }
            else if (_originalWindowStyle is { } style)
            {
                this.SetWindowStyle(style);
            }
            UpdateControlPanelVisual();
        }

        /// <summary>应用桌面歌词独立样式（设置页变更 / 初始化时由 Manager 推送）。</summary>
        public void ApplyStyle(DesktopLyricsStyle style) => _renderer.SetStyle(style);

        /// <summary>恢复默认尺寸并置于主屏工作区底部居中（重置按钮调用）。</summary>
        public void ApplyDefaultBounds()
        {
            var work = DisplayArea.Primary.WorkArea;
            int x = work.X + (work.Width - DefaultWidth) / 2;
            int y = work.Y + work.Height - DefaultHeight - BottomMargin;
            AppWindow.MoveAndResize(new RectInt32(x, y, DefaultWidth, DefaultHeight));
            AppSettings.DesktopLyricsX = x;
            AppSettings.DesktopLyricsY = y;
            AppSettings.DesktopLyricsWidth = DefaultWidth;
            AppSettings.DesktopLyricsHeight = DefaultHeight;
        }

        public void Dispose()
        {
            if (!_disposed) Close();
        }

        private void ConfigureWindow()
        {
            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.SetBorderAndTitleBar(true, false);
                presenter.IsResizable = true;
                presenter.IsMinimizable = false;
                presenter.IsMaximizable = false;
                presenter.IsAlwaysOnTop = true;
            }
            AppWindow.IsShownInSwitchers = false;

            int width = AppSettings.DesktopLyricsWidth > 0 ? AppSettings.DesktopLyricsWidth : DefaultWidth;
            int height = AppSettings.DesktopLyricsHeight > 0 ? AppSettings.DesktopLyricsHeight : DefaultHeight;
            if (AppSettings.DesktopLyricsX >= 0 && AppSettings.DesktopLyricsY >= 0 &&
                WindowSizeHelper.IsBoundsOnScreen(AppSettings.DesktopLyricsX, AppSettings.DesktopLyricsY, width, height))
            {
                // 多显示器：IsBoundsOnScreen 遍历所有显示器 WorkArea 校验可见性
                AppWindow.MoveAndResize(new RectInt32(AppSettings.DesktopLyricsX, AppSettings.DesktopLyricsY, width, height));
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
                _cursorOverControlPanel = false;
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

            bool overPanel = IsCursorOverControlPanel(cursor);
            if (overPanel == _cursorOverControlPanel) return;
            _cursorOverControlPanel = overPanel;
            ControlPanel.Opacity = overPanel ? 1.0 : 0.0;
            ApplyClickThrough(!overPanel);
        }

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
            if (args.DidPositionChange)
            {
                PointInt32 pos = sender.Position;
                AppSettings.DesktopLyricsX = pos.X;
                AppSettings.DesktopLyricsY = pos.Y;
            }
            if (args.DidSizeChange)
            {
                SizeInt32 size = sender.Size;
                AppSettings.DesktopLyricsWidth = size.Width;
                AppSettings.DesktopLyricsHeight = size.Height;
            }
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
