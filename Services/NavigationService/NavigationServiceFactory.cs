using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WinUIMusicPlayer.Services.NavigationService
{
    public class NavigationServiceFactory : INavigationServiceFactory
    {
        private readonly IServiceProvider _serviceProvider;

        public NavigationServiceFactory(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        public INavigationService CreateNavigationService(Frame frame)
        {
            // 通过IServiceProvider创建导航服务实例
            var navigationService = _serviceProvider.GetRequiredService<INavigationService>();

            // 设置Frame
            navigationService.Initialize(frame);

            return navigationService;
        }
    }

}
