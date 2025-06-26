using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WinUIMusicPlayer.Services.NavigationService
{
    public interface INavigatable
    {
        void ReceiveNavigationParameter(object parameter);
    }
}
