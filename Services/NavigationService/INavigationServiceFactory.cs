using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WinUIMusicPlayer.Services.NavigationService
{
    public interface INavigationServiceFactory
    {
        INavigationService CreateNavigationService(Frame frame);
    }
}
