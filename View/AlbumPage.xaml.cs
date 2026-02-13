using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using System.Diagnostics;
using WinUIMusicPlayer.Converters;
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
        public AlbumPage(AlbumViewModel viewModel)
        {
            ViewModel = viewModel;
            //ViewModel.SetCurrentPage(this);
            this.InitializeComponent();
            DataContext = this;
        }

        public void ReceiveNavigationParameter(object parameter)
        {
            ViewModel.ReceiveNavigation();
        }

        //public void SortMusicList(string sortOrder = "DefaultOrder")
        //{
        //    //ViewModel.SortMusicList(sortOrder);
        //}


        private void Album_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            var frameworkElement = e.OriginalSource as FrameworkElement;
            if (frameworkElement?.DataContext is Music clickedItem)
            {
                ViewModel.SelectedItem = clickedItem;
            }
            e.Handled = true;
        }

        //public void OnAlbumDetailChanged(object sender, Music cover)
        //{
        //    //ViewModel.OnAlbumDetailChanged(sender, cover);
        //}

        private void AlbumGridView_ItemClick(object sender, ItemClickEventArgs e)
        {
            ViewModel.AlbumGridView_ItemClick(sender, e);
        }

        private void AlbumGridView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.InRecycleQueue)
            {
                if (args.Item is Music music)
                {
                    AlbumArtConverter.OnMusicUnloaded(music.Id);
                }
            }
        }
    }
}

