using Microsoft.UI.Xaml.Controls;

namespace WinUIMusicPlayer.Services.NavigationService
{
    public interface INavigationServiceFactory
    {
        INavigationService CreateNavigationService(Frame frame);
    }
}
