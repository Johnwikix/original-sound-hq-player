using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
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
    public sealed partial class FolderBrowsePage : Page
    {
        public FolderViewModel ViewModel { get; }
        public FolderBrowsePage()
        {
            NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
            ViewModel = App.Services.GetRequiredService<FolderViewModel>();
            ViewModel.SetCurrentPage(this);
            this.InitializeComponent();
            DataContext = this;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            ViewModel.ReceiveNavigation();
        }

        private void FolderGridView_ItemClick(object sender, ItemClickEventArgs e)
        {
            ViewModel.FolderGridView_ItemClick(sender, e);
        }

        private void Folder_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            ViewModel.Folder_RightTapped(sender, e);
        }

        private void FolderGridView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
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
