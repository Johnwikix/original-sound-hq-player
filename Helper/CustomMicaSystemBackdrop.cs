using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace WinUIMusicPlayer.Helper
{
    public class CustomMicaSystemBackdrop : SystemBackdrop
    {
        private MicaController _micaController;
        private SystemBackdropConfiguration _backdropConfiguration;
        private Microsoft.UI.Dispatching.DispatcherQueue _dispatcherQueue;

        // 保存 window 对象引用
        private ICompositionSupportsSystemBackdrop _currentTarget;
        private bool _isConnected = false;
        private Window _window;
        // Mica效果属性
        public MicaKind MicaKind { get; set; } = MicaKind.Base;
        public Color TintColor { get; set; } = Color.FromArgb(255, 32, 32, 32);
        public float TintOpacity { get; set; } = 1.0f;
        public bool IsInputActive = false;

        public CustomMicaSystemBackdrop(Window window = null)
        {
            _window = window;
        }

        protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
        {
            _currentTarget = connectedTarget;
            _isConnected = true;

            // 获取当前线程的 DispatcherQueue
            _dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            _backdropConfiguration = new SystemBackdropConfiguration();

            // 根据应用当前主题设置背景配置
            Microsoft.UI.Xaml.FrameworkElement rootElement = xamlRoot.Content as Microsoft.UI.Xaml.FrameworkElement;
            SetConfigurationSourceTheme(rootElement);

            // 监听主题变更事件
            rootElement.ActualThemeChanged += RootElement_ActualThemeChanged;

            // 监听窗口状态变更
            _window?.Closed += Window_Closed;
            _window?.Activated += Window_Activated;

            // 创建并初始化云母控制器
            _micaController = new MicaController();

            // 设置云母效果属性
            SetMicaProperties();

            // 激活云母效果
            if (_micaController is not null)
            {
                // 设置配置
                _micaController.SetSystemBackdropConfiguration(_backdropConfiguration);

                // 添加目标
                _micaController.AddSystemBackdropTarget(connectedTarget);
            }
        }

        private void RootElement_ActualThemeChanged(FrameworkElement sender, object args)
        {
            if (_isConnected)
            {
                SetConfigurationSourceTheme(sender);
            }
        }

        protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
        {
            // 清理资源和事件监听
            _isConnected = false;
            _currentTarget = null;


            if (disconnectedTarget is FrameworkElement element)
            {
                element.ActualThemeChanged -= RootElement_ActualThemeChanged;

                if (_currentTarget is Window window)
                {
                    window.Closed += Window_Closed;
                }
            }

            if (_micaController is not null)
            {
                _micaController.RemoveSystemBackdropTarget(disconnectedTarget);
                _micaController.Dispose();
                _micaController = null;
            }

            _backdropConfiguration = null;
        }

        private void Window_Activated(object sender, WindowActivatedEventArgs args)
        {
            if (IsInputActive)
            {
                _backdropConfiguration?.IsInputActive = args.WindowActivationState != WindowActivationState.Deactivated;
            }
            else
            {
                _backdropConfiguration?.IsInputActive = true;
            }
        }

        private void Window_Closed(object sender, WindowEventArgs args)
        {
            _micaController?.Dispose();
            _micaController = null;
            _backdropConfiguration = null;
            _window.Closed -= Window_Closed;
            _window.Activated -= Window_Activated;
        }

        private void SetConfigurationSourceTheme(FrameworkElement element)
        {
            if (_backdropConfiguration is null) return;

            _backdropConfiguration.Theme = element.ActualTheme switch
            {
                ElementTheme.Dark => SystemBackdropTheme.Dark,
                ElementTheme.Light => SystemBackdropTheme.Light,
                ElementTheme.Default => SystemBackdropTheme.Default,
                _ => SystemBackdropTheme.Default
            };
            UpdateUiColor(element.ActualTheme);
        }

        // 设置云母效果的属性
        private void SetMicaProperties()
        {
            if (_micaController is null || _dispatcherQueue is null || !_isConnected)
            {
                // 记录日志，帮助诊断问题
                //System.Diagnostics.Debug.WriteLine("SetMicaProperties被调用，但控制器或调度队列无效");
                return;
            }

            _dispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    // 再次检查，因为在队列执行时可能已经变化
                    if (_micaController is not null && _isConnected)
                    {
                        // 设置云母效果的类型和颜色
                        _micaController.Kind = MicaKind;
                        _micaController.TintColor = TintColor;
                        _micaController.TintOpacity = TintOpacity;
                    }
                }
                catch
                {
                }
            });
        }

        // 提供一个公共方法，用于动态更新云母效果的属性
        public void UpdateProperties(MicaKind micaKind, float tintOpacity, Color tintColor)
        {
            MicaKind = micaKind;
            TintOpacity = tintOpacity;
            TintColor = tintColor;

            SetMicaProperties();
        }

        // 检查系统是否支持Mica效果
        public static bool IsSupported()
        {
            return MicaController.IsSupported();
        }

        private void UpdateUiColor(ElementTheme elementTheme)
        {
            var isDarkTheme = elementTheme switch
            {
                ElementTheme.Dark => true,
                ElementTheme.Light => false,
                ElementTheme.Default => Application.Current.RequestedTheme == ApplicationTheme.Dark,
                _ => true
            };
            TintColor = isDarkTheme
                ? Color.FromArgb(255, 32, 32, 32)
                : Color.FromArgb(220, 255, 255, 255);
            SetMicaProperties();
        }
    }
}
