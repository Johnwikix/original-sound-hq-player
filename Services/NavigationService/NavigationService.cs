using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WinUIMusicPlayer.Services.NavigationService
{
    public class NavigationService : INavigationService
    {
        private readonly Dictionary<Type, Type> _registeredPages = new Dictionary<Type, Type>();
        private readonly IServiceProvider _serviceProvider;

        public Frame ContentFrame { get; set; }

        public bool CanGoBack => ContentFrame?.CanGoBack ?? false;

        public NavigationService(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        public void RegisterPage<T>() where T : Page
        {
            _registeredPages[typeof(T)] = typeof(T);
        }
        public void Navigate(Type pageType, object parameter = null)
        {
            if (_registeredPages.TryGetValue(pageType, out var resolvedType))
            {
                // 从服务容器中获取页面实例（单例）
                var pageInstance = _serviceProvider.GetRequiredService(resolvedType) as Page;

                // 直接设置 Content，绕过 Navigate 方法
                ContentFrame.Content = pageInstance;

                // 如果需要传递参数，可以通过页面的属性或方法
                //if (parameter != null && pageInstance is INavigationAware navigationAware)
                //{
                //    navigationAware.OnNavigatedTo(parameter);
                //}
            }
            else
            {
                // 如果未注册，使用默认方式导航
                ContentFrame?.Navigate(pageType, parameter);
            }
        }

        public void GoBack()
        {
            ContentFrame?.GoBack();
        }
    }
}
