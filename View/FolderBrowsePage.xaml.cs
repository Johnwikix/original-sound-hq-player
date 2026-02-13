using Microsoft.UI.Xaml;
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
    public sealed partial class FolderBrowsePage : Page, INavigatable
    {
        public FolderViewModel ViewModel { get; }
        public FolderBrowsePage(FolderViewModel viewModel)
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

        private void FolderGridView_ItemClick(object sender, ItemClickEventArgs e)
        {
            ViewModel.FolderGridView_ItemClick(sender, e);
        }

        private void Folder_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            //ViewModel.Folder_RightTapped(sender, e);
            var frameworkElement = e.OriginalSource as FrameworkElement;
            if (frameworkElement?.DataContext is Music clickedItem)
            {
                ViewModel.SelectedItem = clickedItem;
            }
            e.Handled = true;
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
