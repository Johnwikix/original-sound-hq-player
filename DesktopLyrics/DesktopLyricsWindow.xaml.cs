using AnimatedWin2dControls.Messages;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl;
using Windows.Graphics;
using WinUIEx;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.DesktopLyrics
{
    /// <summary>
    /// 桌面歌词悬浮窗：透明、置顶、不进任务栏/Alt-Tab。
    /// 锁定 = WS_EX_LAYERED|WS_EX_TRANSPARENT 完全点击穿透（参考 BetterLyrics Desktop 模式），
    /// 解锁入口在托盘 NotifyIconControl；解锁后可任意拖动/调整大小。
    /// </summary>
    public sealed partial class DesktopLyricsWindow : Window, IDisposable
    {
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WM_NCLBUTTONDOWN = 0x00A1;
        private const int HTCAPTION = 0x0002;

        private const int DefaultWidth = 900;
        private const int DefaultHeight = 140;
        private const int BottomMargin = 60;

        private readonly IDesktopLyricsRenderer _renderer;
        private readonly IntPtr _hwnd;
        private bool _locked = true;
        private bool _disposed;

        public DesktopLyricsWindow()
        {
            InitializeComponent();
            _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

            // 渲染器接入点：未来接入逐字效果时，替换为包装 AdvanceLyricsCanvasControl 的 IDesktopLyricsRenderer 实现
            _renderer = new TextBlockLyricsRenderer();
            RendererHost.Content = _renderer.Content;

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
            int exStyle = (int)WindowHelper.GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
            if (locked)
                exStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT;
            else
                exStyle &= ~(WS_EX_LAYERED | WS_EX_TRANSPARENT);
            WindowHelper.SetWindowLongPtr(_hwnd, GWL_EXSTYLE, exStyle);

            if (AppWindow.Presenter is OverlappedPresenter presenter)
                presenter.IsResizable = !locked;
        }

        public void Dispose()
        {
            if (!_disposed) Close();
        }

        private void ConfigureWindow()
        {
            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                // 保留边框以支持调整大小，去掉标题栏
                presenter.SetBorderAndTitleBar(true, false);
                presenter.IsResizable = true;
                presenter.IsMinimizable = false;
                presenter.IsMaximizable = false;
                presenter.IsAlwaysOnTop = true;
            }
            AppWindow.IsShownInSwitchers = false;

            int width = DefaultWidth, height = DefaultHeight;
            if (AppSettings.DesktopLyricsX >= 0 && AppSettings.DesktopLyricsY >= 0 &&
                WindowSizeHelper.IsBoundsOnScreen(AppSettings.DesktopLyricsX, AppSettings.DesktopLyricsY, width, height))
            {
                AppWindow.MoveAndResize(new RectInt32(AppSettings.DesktopLyricsX, AppSettings.DesktopLyricsY, width, height));
            }
            else
            {
                var work = DisplayArea.Primary.WorkArea;
                int x = work.X + (work.Width - width) / 2;
                int y = work.Y + work.Height - height - BottomMargin;
                AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
                AppSettings.DesktopLyricsX = x;
                AppSettings.DesktopLyricsY = y;
            }
        }

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
        }

        private void OnWindowClosed(object sender, WindowEventArgs args)
        {
            UILyricsBus.Changed -= OnUILyricsChanged;
            TimeProgressBus.CurrentPlayingTimeChanged -= OnTimeProgressChanged;
            OffsetMsBus.Changed -= OnOffsetChanged;
            IsPlayingBus.Changed -= OnIsPlayingChanged;
            AppWindow.Changed -= OnAppWindowChanged;
            Closed -= OnWindowClosed;
            _renderer.Dispose();
            _disposed = true;
        }

        private void RootGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (_locked) return;
            var point = e.GetCurrentPoint(RootGrid);
            if (!point.Properties.IsLeftButtonPressed) return;
            ReleaseCapture();
            WindowHelper.SendMessage(_hwnd, WM_NCLBUTTONDOWN, HTCAPTION, System.IntPtr.Zero);
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ReleaseCapture();
    }
}
