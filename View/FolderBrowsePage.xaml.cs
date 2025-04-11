using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Utils;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUIMusicPlayer.View
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class FolderBrowsePage : Page
    {
        private MusicBrowsePage parentPage;
        private List<Music> musicList;
        public FolderBrowsePage()
        {
            this.InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (e.Parameter is MusicBrowsePage parentPage)
            {
                this.parentPage = parentPage;
                parentPage.currentFolderName = null;
                parentPage.DisableBackButton();
                InitializeData();
            }
        }
        public void SortMusicList(string sortOrder)
        {
            var order = "DefaultOrder";
            if (!string.IsNullOrEmpty(sortOrder))
            {
                order = sortOrder;
            }
            if (musicList.Count > 0)
            {
                musicList = ToolUtils.SortMusicList("folderCover", order, musicList.ToList());
            }
            FolderItemsControl.ItemsSource = musicList;
        }

        private async void InitializeData()
        {
            try
            {
                if (parentPage != null)
                {
                    await parentPage.LoadMusic();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"初始化文件夹时出错: {ex.Message}");
            }
        }

        public void LoadFolder(List<Music> musics)
        {
            try
            {
                var groupArtists = musics.GroupBy(m => m.LastLevelFolderPath)
                                             .Select(g => g.First())
                                             .ToList();
                musicList = groupArtists;
                SortMusicList("DefaultOrder");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"加载文件夹数据失败: {ex.Message}");
            }
        }

        private void Folder_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is Music music)
            {
                if (parentPage != null)
                {
                    parentPage.LoadFolderMusic(music.LastLevelFolderPath);
                }
            }
        }
    }
}
