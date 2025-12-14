using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Helper
{
    public class CustomAcrylicSystemBackdrop : SystemBackdrop
    {
        private DesktopAcrylicController _acrylicController;
        private SystemBackdropConfiguration _backdropConfiguration;
        private Microsoft.UI.Dispatching.DispatcherQueue _dispatcherQueue;

        // 保存 window 对象引用
        private ICompositionSupportsSystemBackdrop _currentTarget;
        private bool _isConnected = false;
        private Window _window;        

        // 透明度属性
        public double TintOpacity { get; set; } = 0.5;
        public double LuminosityOpacity { get; set; } = 0.8;
        public Color TintColor { get; set; } = Color.FromArgb(255, 32, 32, 32);
        public bool IsInputActive = false;

        public CustomAcrylicSystemBackdrop(Window window = null)
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

            _window?.Closed += Window_Closed;
            _window?.Activated += Window_Activated;

            // 创建并初始化亚克力控制器
            _acrylicController = new DesktopAcrylicController();

            // 设置透明度和颜色属性
            SetAcrylicProperties();

            // 激活亚克力效果
            if (_acrylicController is not null)
            {
                // 设置配置
                _acrylicController.SetSystemBackdropConfiguration(_backdropConfiguration);

                // 添加目标
                _acrylicController.AddSystemBackdropTarget(connectedTarget);
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

            if (_acrylicController is not null)
            {
                _acrylicController.RemoveSystemBackdropTarget(disconnectedTarget);
                _acrylicController.Dispose();
                _acrylicController = null;
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
            _acrylicController?.Dispose();
            _acrylicController = null;
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

        // 设置亚克力效果的属性
        private void SetAcrylicProperties()
        {
            if (_acrylicController is null || _dispatcherQueue is null || !_isConnected)
            {
                return;
            }

            _dispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    // 再次检查，因为在队列执行时可能已经变化
                    if (_acrylicController is not null && _isConnected)
                    {
                        // 设置亚克力效果的透明度和颜色
                        _acrylicController.TintColor = TintColor;
                        _acrylicController.TintOpacity = (float)TintOpacity;
                        _acrylicController.LuminosityOpacity = (float)LuminosityOpacity;
                    }
                }
                catch
                {
                }
            });
        }

        // 提供一个公共方法，用于动态更新亚克力效果的透明度
        public void UpdateProperties(double tintOpacity, double luminosityOpacity, Color? tintColor = null)
        {
            TintOpacity = tintOpacity;
            LuminosityOpacity = luminosityOpacity;
            if (tintColor is not null) {
                TintColor = tintColor.Value;
            }
            SetAcrylicProperties();
        }

        private void UpdateUiColor(ElementTheme elementTheme)
        {
            if (!IsDefaultColor(TintColor)) return;
            var isDarkTheme = elementTheme switch
            {
                ElementTheme.Dark => true,
                ElementTheme.Light => false,
                ElementTheme.Default => Application.Current.RequestedTheme == ApplicationTheme.Dark,
                _ => true
            };
            TintColor = isDarkTheme
                ? Color.FromArgb(255, 32, 32, 32)
                : Color.FromArgb(255, 255, 255, 255);
            SetAcrylicProperties();
        }

        private bool IsDefaultColor(Color color)
        {
            // 检查是否是默认的深色或浅色
            return (color.A == 255 && color.R == 32 && color.G == 32 && color.B == 32) ||
                   (color.A == 255 && color.R == 255 && color.G == 255 && color.B == 255);
        }
    }
}
