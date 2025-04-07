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
using SQLite;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using System.Diagnostics;
using WinUIMusicPlayer.Utils;
using System.Collections.ObjectModel;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUIMusicPlayer.View
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class SongListPage : Page
    {
        private SQLiteAsyncConnection dbConnection;
        private ObservableCollection<Music> musicList;
        private MusicBrowsePage parentPage;

        public SongListPage()
        {
            InitializeComponent();            
        }

        // 然后在 SongListPage 的 OnNavigatedTo 中接收参数
        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (e.Parameter is MusicBrowsePage parentPage)
            {
                this.parentPage = parentPage;
                InitializeDatabase();
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
                musicList = new ObservableCollection<Music>(ToolUtils.SortMusicList("song", order, musicList.ToList()));
            }
            MusicListView.ItemsSource = musicList;
        }

        private async void InitializeDatabase()
        {
            var dbPath = Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, "MusicDatabase.db");
            dbConnection = new SQLiteAsyncConnection(dbPath);
            await dbConnection.CreateTableAsync<Music>();
            if (parentPage != null)
            {
                await parentPage.LoadMusic();               
            }
        }

        public void LoadMusicAsync(List<Music> musics)
        {
            try
            {
                musicList = new ObservableCollection<Music>(musics);
                MusicListView.ItemsSource = musicList;
                UpdateMusicListView();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"加载音乐列表失败: {ex.Message}");
            }
        }

        public void UpdateMusicListView()
        {
            try
            {
                if (parentPage != null) {
                    if (musicList.Contains(parentPage.currentPlayingMusic)) {
                        MusicListView.SelectedItem = parentPage.currentPlayingMusic;
                        MusicListView.ScrollIntoView(parentPage.currentPlayingMusic);
                    }                    
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"滚动音乐失败: {ex.Message}");
            }
        }

        private async void MusicListView_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
        {
            var selectedMusic = MusicListView.SelectedItem as Music;
            if (selectedMusic != null && parentPage != null)
            {
                await parentPage.PlayMusic(selectedMusic);
            }
        }

        private async void RemoveMusicButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button != null && button.Tag is int musicId)
            {
                await parentPage.RemoveMusic(musicId);
            }
        }

        private async void PlayMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (MusicListView.SelectedItem is Music selectedMusic)
            {
                await parentPage.PlayMusic(selectedMusic);
            }
        }

        private async void DeleteMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (MusicListView.SelectedItem is Music selectedMusic)
            {
                await parentPage.RemoveMusic(selectedMusic.Id);
            }
        }

        private void AddToPlaylistMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (MusicListView.SelectedItem is Music selectedMusic)
            {
                // 处理添加到播放列表的逻辑
            }
        }

        private async void SetAsFavoriteMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (MusicListView.SelectedItem is Music selectedMusic)
            {
                await parentPage.AddToFavourite(selectedMusic);
            }
        }

        private void OpenInExplorer_Click(object sender, RoutedEventArgs e)
        {
            if (MusicListView.SelectedItem is Music selectedMusic)
            {
                var filePath = selectedMusic.Path;
                if (File.Exists(filePath))
                {
                    try
                    {
                        Process.Start("explorer.exe", $"/select,\"{filePath}\"");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"打开资源管理器时出错: {ex.Message}");
                    }
                }
                else
                {
                    Debug.WriteLine($"文件不存在: {filePath}");
                }
            }
        }

        private void MusicListView_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            var targetElement = e.OriginalSource as FrameworkElement;
            ListViewItem listViewItem = ToolUtils.FindParent<ListViewItem>(targetElement);
            if (listViewItem != null)
            {
                listViewItem.IsSelected = true;
                MusicListView.SelectedItem = listViewItem.Content;
            }
        }

        private async void IsFavouriteIconButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button != null && button.Tag is Music music)
            {                
                if (music != null)
                {
                    ((FontIcon)button.Content).Glyph = !music.isFavorite ? "\ueb52" : "\ueb51";
                    await parentPage.AddToFavourite(music);                    
                }
            }
        }

        private void AuthorTextBlock_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            if (sender is TextBlock textBlock)
            {
                string artist = textBlock.Text;
                if (parentPage != null)
                {
                    parentPage.LoadArtistMusic(artist);
                }
            }
        }

        private void AlbumTextBlock_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            if (sender is TextBlock textBlock)
            {
                string albumName = textBlock.Text;
                if (parentPage != null)
                {
                    parentPage.LoadAlbumMusic(albumName);
                }
            }
        }
    }
}
