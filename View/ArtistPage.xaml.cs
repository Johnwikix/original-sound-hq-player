using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Services.NavigationService;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.ViewModel;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUIMusicPlayer.View
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class ArtistPage : Page, INavigatable
    {
        public ArtistViewModel ViewModel { get; }
        public ArtistPage(ArtistViewModel viewModel)
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

        //public void SortMusicList(string sortOrder)
        //{
        //    ViewModel.SortMusicList(sortOrder);
        //}      

        private void ArtistGridView_ItemClick(object sender, ItemClickEventArgs e)
        {
            ViewModel.ArtistGridView_ItemClick(sender, e);          
        }

        private void Artist_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            ViewModel.Artist_RightTapped(sender, e);            
        }
    }

}
