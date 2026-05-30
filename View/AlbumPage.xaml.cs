using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services.NavigationService;
using WinUIMusicPlayer.ViewModel;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUIMusicPlayer.View
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class AlbumPage : Page, INavigatable
    {
        public AlbumViewModel ViewModel { get; }
        public AlbumPage()
        {
            ViewModel = App.Services.GetRequiredService<AlbumViewModel>();
            this.InitializeComponent();
            AlbumGridView.ContainerContentChanging += AlbumGridView_ContainerContentChanging;
            DataContext = this;
            this.NavigationCacheMode = NavigationCacheMode.Enabled;
        }

        private void AlbumGridView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.InRecycleQueue)
                WinUIMusicPlayer.Behaviors.AlbumCoverBehavior.ClearImagesInContainer(args.ItemContainer);
        }

        public void ReceiveNavigationParameter(object parameter)
        {
            ViewModel.ReceiveNavigation();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            ViewModel.ReceiveNavigation();
        }

        private void Album_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            var frameworkElement = e.OriginalSource as FrameworkElement;
            if (frameworkElement?.DataContext is Music clickedItem)
            {
                ViewModel.SelectedItem = clickedItem;
            }
            e.Handled = true;
        }

        private void AlbumGridView_ItemClick(object sender, ItemClickEventArgs e)
        {
            ViewModel.AlbumGridView_ItemClick(sender, e);
        }
    }
}

