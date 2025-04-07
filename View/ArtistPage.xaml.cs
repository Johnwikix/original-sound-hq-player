using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System.Diagnostics;
using WinUIMusicPlayer.Model;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUIMusicPlayer.View
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class ArtistPage : Page
    {
        private MusicBrowsePage parentPage;
        private List<Music> musicList;        
        public ArtistPage()
        {
            this.InitializeComponent();            
        }
        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (e.Parameter is MusicBrowsePage parentPage)
            {
                this.parentPage = parentPage;
                InitializeData();
            }
        }

        private async void InitializeData()
        {
            try
            {
                if (parentPage != null)
                {
                    await parentPage.LoadMusic("DefaultOrder");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"初始化艺术家页面时出错: {ex.Message}");
            }
        }

        public void LoadArtists(List<Music> musics)
        {
            try
            {
                var groupArtists = musics.GroupBy(m => m.Author)
                                             .Select(g => g.First())
                                             .ToList();
                musicList = groupArtists;
                ArtistsItemsControl.ItemsSource = musicList;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"加载专辑数据失败: {ex.Message}");
            }
        }

        private void Artist_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is Music music)
            {
                Debug.WriteLine($"Clicked on artist: {music.Author}");
                if (parentPage != null)
                {
                    parentPage.LoadArtistMusic(music.Author);
                }
            }
        }

    }

}
