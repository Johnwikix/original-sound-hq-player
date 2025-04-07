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
using System.Collections.ObjectModel;
using System.Data.Common;
using System.Diagnostics;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Utils;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUIMusicPlayer.View
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class FavouritePlayListPage : Page
    {
        private SQLiteAsyncConnection dbConnection;
        private ObservableCollection<Music> musicList;
        private MusicBrowsePage parentPage;
        public FavouritePlayListPage()
        {
            this.InitializeComponent();
            MusicListView.DragItemsCompleted += MusicListView_DragItemsCompleted;
        }

        private async void MusicListView_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
        {
            var draggedItem = args.Items[0] as Music;
            // 获取拖拽后的新索引
            var newIndex = sender.Items.IndexOf(draggedItem);
            // 从数据源中移除该项
            musicList.Remove(draggedItem);
            // 将该项插入到新的位置
            musicList.Insert(newIndex, draggedItem);
            for (int i = 0; i < musicList.Count; i++)
            {
                musicList[i].Order = musicList.Count - i;
                await dbConnection.UpdateAsync(musicList[i]);
            }
            if (parentPage != null)
            {
                parentPage.UpdateFavourtPlaylist(musicList.ToList());
            }
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (e.Parameter is MusicBrowsePage parentPage)
            {
                this.parentPage = parentPage;
                InitializeDatabase();
            }
        }

        private async void InitializeDatabase()
        {
            var dbPath = Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, "MusicDatabase.db");
            dbConnection = new SQLiteAsyncConnection(dbPath);
            await dbConnection.CreateTableAsync<Music>();
            if (parentPage != null)
            {
                await parentPage.LoadFavouriteMusic("DefaultOrder");
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
                if (parentPage != null)
                {
                    if (musicList.Contains(parentPage.currentPlayingMusic))
                    {
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

        private void AlbumTextBlock_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            if (sender is TextBlock textBlock)
            {
                string albumName = textBlock.Text;
                // 假设 AlbumDetailsPage 是目标页面，将专辑名作为参数传递
                if (parentPage != null)
                {
                    parentPage.LoadAlbumMusic(albumName);
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

        private async void IsFavouriteIconButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button != null && button.Tag is Music music)
            {
                if (music != null)
                {
                    ((FontIcon)button.Content).Glyph = !music.isFavorite ? "\ueb52" : "\ueb51";
                    if (music.isFavorite) {
                        musicList.Remove(music);
                    }
                    await parentPage.AddToFavourite(music);
                }
            }
        }
    }
}
