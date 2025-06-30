using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
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
        void Navigate(Type pageType, object parameter = null, NavigationTransitionInfo transitionInfo = null,int animeTime = 300);
        void Initialize(Frame frame);
        void GoBack();
        bool CanGoBack { get; }
    }
}
