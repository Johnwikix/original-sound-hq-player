using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using System;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.ViewModel;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUIMusicPlayer.View
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class AlbumPage : Page
    {
        public AlbumViewModel ViewModel { get; }
        public AlbumPage()
        {
            ViewModel = App.Services.GetRequiredService<AlbumViewModel>();
            ViewModel.SetCurrentPage(this);
            this.InitializeComponent();            
            DataContext = this;
        }
        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (e.Parameter is MusicBrowsePage parentPage)
            {
                ViewModel.SetParentPage(parentPage);                
            }            
        }

        public async void SortMusicList(string sortOrder = "DefaultOrder")
        {
            await ViewModel.SortMusicList(sortOrder);           
        }
        

        private async void Album_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            ViewModel.Album_RightTapped(sender, e);
            //var originalSource = e.OriginalSource as FrameworkElement;

            //// 向上遍历查找 GridViewItem
            //GridViewItem clickedItem = ToolUtils.FindParent<GridViewItem>(originalSource);

            //if (clickedItem != null)
            //{
            //    // 从 GridViewItem 获取数据项
            //    var album = clickedItem.Content as Music;

            //    if (album != null)
            //    {
            //        ContextMenuService.Instance.SetAlbumPage(this);
            //        // 显示专辑右键菜单
            //        await ContextMenuService.Instance.ShowAlbumContextMenu(
            //            album,
            //            originalSource,
            //            e.GetPosition(originalSource),
            //            "album"
            //        );
            //        ContextMenuService.playingAlbumMusic += PlayingAlbum;
            //        ContextMenuService.showTransmission += (s, e) =>
            //        {
            //            if (parentPage != null)
            //            {
            //                parentPage.ShowTransmission();
            //            }
            //        };
            //        ContextMenuService.hideTransmission += (s, e) =>
            //        {
            //            if (parentPage != null)
            //            {
            //                parentPage.HideTransmission();
            //            }
            //        };
            //    }
            //}
            //e.Handled = true;
        }

        public async void OnAlbumDetailChanged(object sender, Music cover)
        {
            ViewModel.OnAlbumDetailChanged(sender, cover);
        }

        private void AlbumGridView_ItemClick(object sender, ItemClickEventArgs e)
        {
            ViewModel.AlbumGridView_ItemClick(sender, e);
        }
    }
}

