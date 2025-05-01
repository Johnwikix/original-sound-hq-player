using H.NotifyIcon;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Diagnostics;

namespace WinUIMusicPlayer.Helper
{
    public class NotifyIconHelper
    {
        private Window _window;
        private AppWindow _appWindow;
        private TaskbarIcon _notifyIcon;

        public event EventHandler PlayLastSong;
        public event EventHandler PlayNextSong;
        public event EventHandler PlayStop;

        public NotifyIconHelper(Window window, AppWindow appWindow)
        {
            _window = window;
            _appWindow = appWindow;
        }

        public void Initialize()
        {
            try
            {
                _notifyIcon = new TaskbarIcon();
                _notifyIcon.IconSource = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri("ms-appx:///Assets/icon.ico"));
                _notifyIcon.ToolTipText = "原音HIFI";
                _notifyIcon.Visibility = Visibility.Visible;

                var contextMenu = new MenuFlyout();

                // 显示窗口菜单项
                var openCommand = new Microsoft.UI.Xaml.Input.XamlUICommand();
                openCommand.Label = "显示";
                openCommand.ExecuteRequested += (s, e) =>
                {
                    ShowWindow();
                };

                // 上一首菜单项
                var lastCommand = new Microsoft.UI.Xaml.Input.XamlUICommand();
                lastCommand.Label = "上一首";
                lastCommand.ExecuteRequested += (s, e) =>
                {
                    OnPlayLastSong();
                };

                // 播放/暂停菜单项
                var playCommand = new Microsoft.UI.Xaml.Input.XamlUICommand();
                playCommand.Label = "播放/暂停";
                playCommand.ExecuteRequested += (s, e) =>
                {
                    OnPlayStop();
                };

                // 下一首菜单项
                var nextCommand = new Microsoft.UI.Xaml.Input.XamlUICommand();
                nextCommand.Label = "下一首";
                nextCommand.ExecuteRequested += (s, e) =>
                {
                    OnPlayNextSong();
                };

                // 退出菜单项
                var exitCommand = new Microsoft.UI.Xaml.Input.XamlUICommand();
                exitCommand.Label = "退出";
                exitCommand.ExecuteRequested += (s, e) =>
                {
                    CloseApplication();
                };

                // 添加所有菜单项
                var openMenuItem = new MenuFlyoutItem { Text = "显示", Command = openCommand };
                contextMenu.Items.Add(openMenuItem);
                var lastMenuItem = new MenuFlyoutItem { Text = "上一首", Command = lastCommand };
                contextMenu.Items.Add(lastMenuItem);
                var playMenuItem = new MenuFlyoutItem { Text = "播放/暂停", Command = playCommand };
                contextMenu.Items.Add(playMenuItem);
                var nextMenuItem = new MenuFlyoutItem { Text = "下一首", Command = nextCommand };
                contextMenu.Items.Add(nextMenuItem);
                contextMenu.Items.Add(new MenuFlyoutSeparator());
                var exitMenuItem = new MenuFlyoutItem { Text = "退出", Command = exitCommand };
                contextMenu.Items.Add(exitMenuItem);

                _notifyIcon.ContextFlyout = contextMenu;
                _notifyIcon.DoubleClickCommand = new RelayCommand(() =>
                {
                    ShowWindow();
                });

                // 将托盘图标添加到资源
                if (_window.Content is FrameworkElement rootElement)
                {
                    rootElement.Resources.Add("NotifyIconResource", _notifyIcon);
                }

                _notifyIcon.ForceCreate();
                _notifyIcon.ShowNotification("原音HIFI", "托盘图标已初始化");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"初始化系统托盘图标时出错: {ex.Message}");
            }
        }

        private void OnPlayNextSong()
        {
            PlayNextSong?.Invoke(this, EventArgs.Empty);
        }

        private void OnPlayStop()
        {
            PlayStop?.Invoke(this, EventArgs.Empty);
        }

        private void OnPlayLastSong()
        {
            PlayLastSong?.Invoke(this, EventArgs.Empty);
        }

        public void ShowWindow()
        {
            Debug.WriteLine("准备显示窗口");
            _window.DispatcherQueue.TryEnqueue(() =>
            {
                if (_appWindow != null)
                {
                    // 先确保窗口显示
                    _appWindow.Show();

                    // 设置前台窗口激活
                    var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
                    WindowHelper.SetForegroundWindow(hwnd);

                    // 如果窗口被最小化，则恢复它
                    if (WindowHelper.IsIconic(hwnd))
                    {
                        WindowHelper.ShowWindow(hwnd, WindowHelper.SW_RESTORE);
                    }

                    Debug.WriteLine("窗口已显示并激活");
                }
                else
                {
                    Debug.WriteLine("m_AppWindow为空");
                }
            });
        }

        public void CloseApplication()
        {
            // 清理资源并关闭应用
            Dispose();
            _window.Close();
        }

        public void Dispose()
        {
            _notifyIcon?.Dispose();
            _notifyIcon = null;
        }

        // RelayCommand 类实现
        public class RelayCommand : System.Windows.Input.ICommand
        {
            private readonly Action _execute;
            private readonly Func<bool> _canExecute;

            public RelayCommand(Action execute, Func<bool> canExecute = null)
            {
                _execute = execute ?? throw new ArgumentNullException(nameof(execute));
                _canExecute = canExecute;
            }

            public event EventHandler CanExecuteChanged;

            public bool CanExecute(object parameter) => _canExecute?.Invoke() ?? true;

            public void Execute(object parameter) => _execute();

            public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
