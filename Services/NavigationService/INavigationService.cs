using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WinUIMusicPlayer.Services.NavigationService
{
    public interface INavigationService
    {
        Frame ContentFrame { get; set; }
        void RegisterPage<T>() where T : Page;
        void Navigate(Type pageType, object parameter = null);
        void GoBack();
        bool CanGoBack { get; }
    }
}
