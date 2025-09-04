using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System;
using Windows.UI;

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

        // 透明度属性
        public double TintOpacity { get; set; } = 0.8;
        public double LuminosityOpacity { get; set; } = 0.7;
        public Color TintColor { get; set; } = Color.FromArgb(255, 135, 206, 235); // SkyBlue

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
            if (connectedTarget is Window window)
            {
                window.Activated += Window_Activated;
                window.Closed += Window_Closed;
            }

            // 创建并初始化亚克力控制器
            _acrylicController = new DesktopAcrylicController();

            // 设置透明度和颜色属性
            SetAcrylicProperties();

            // 激活亚克力效果
            if (_acrylicController != null)
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
                    window.Activated += Window_Activated;
                    window.Closed += Window_Closed;
                }
            }

            if (_acrylicController != null)
            {
                _acrylicController.RemoveSystemBackdropTarget(disconnectedTarget);
                _acrylicController.Dispose();
                _acrylicController = null;
            }

            _backdropConfiguration = null;
        }

        private void Window_Activated(object sender, WindowActivatedEventArgs args)
        {
            if (_backdropConfiguration != null)
            {
                _backdropConfiguration.IsInputActive = args.WindowActivationState != WindowActivationState.Deactivated;
            }
        }

        private void Window_Closed(object sender, WindowEventArgs args)
        {
            // 确保释放资源
            if (_acrylicController != null)
            {
                _acrylicController.Dispose();
                _acrylicController = null;
            }

            _backdropConfiguration = null;
        }

        private void SetConfigurationSourceTheme(FrameworkElement element)
        {
            if (_backdropConfiguration != null)
            {
                switch (element.ActualTheme)
                {
                    case ElementTheme.Dark:
                        _backdropConfiguration.Theme = SystemBackdropTheme.Dark;
                        break;
                    case ElementTheme.Light:
                        _backdropConfiguration.Theme = SystemBackdropTheme.Light;
                        break;
                    case ElementTheme.Default:
                        _backdropConfiguration.Theme = SystemBackdropTheme.Default;
                        break;
                }
            }
        }

        // 设置亚克力效果的属性
        private void SetAcrylicProperties()
        {
            if (_acrylicController == null || _dispatcherQueue == null || !_isConnected)
            {
                // 记录日志，帮助诊断问题
                //System.Diagnostics.Debug.WriteLine("SetAcrylicProperties被调用，但控制器或调度队列无效");
                return;
            }

            _dispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    // 再次检查，因为在队列执行时可能已经变化
                    if (_acrylicController != null && _isConnected)
                    {
                        // 设置亚克力效果的透明度和颜色
                        _acrylicController.TintColor = TintColor;
                        _acrylicController.TintOpacity = (float)TintOpacity;
                        _acrylicController.LuminosityOpacity = (float)LuminosityOpacity;
                        //System.Diagnostics.Debug.WriteLine($"成功更新亚克力效果 - 颜色: {TintColor}, 不透明度: {TintOpacity}");
                    }
                    else
                    {
                        //System.Diagnostics.Debug.WriteLine("队列执行时控制器已无效");
                    }
                }
                catch (Exception ex)
                {
                    //System.Diagnostics.Debug.WriteLine($"SetAcrylicProperties error: {ex.Message}");
                }
            });
        }

        // 提供一个公共方法，用于动态更新亚克力效果的透明度
        public void UpdateProperties(double tintOpacity, double luminosityOpacity, Color tintColor)
        {
            TintOpacity = tintOpacity;
            LuminosityOpacity = luminosityOpacity;
            TintColor = tintColor;

            SetAcrylicProperties();
        }
    }
}
