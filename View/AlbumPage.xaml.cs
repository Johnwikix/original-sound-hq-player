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
        public AlbumPage(AlbumViewModel viewModel)
        {
            ViewModel = viewModel;
            ViewModel.SetCurrentPage(this);
            this.InitializeComponent();
            DataContext = this;
        }

        public void ReceiveNavigationParameter(object parameter)
        {
            ViewModel.ReceiveNavigation();
        }
        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (e.Parameter is MusicBrowsePage parentPage)
            {
                ViewModel.ReceiveNavigation();
            }
        }

        public void SortMusicList(string sortOrder = "DefaultOrder")
        {
            ViewModel.SortMusicList(sortOrder);
        }


        private void Album_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            ViewModel.Album_RightTapped(sender, e);
        }

        public void OnAlbumDetailChanged(object sender, Music cover)
        {
            ViewModel.OnAlbumDetailChanged(sender, cover);
        }

        private void AlbumGridView_ItemClick(object sender, ItemClickEventArgs e)
        {
            ViewModel.AlbumGridView_ItemClick(sender, e);
        }
    }
}

