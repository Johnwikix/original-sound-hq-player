using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.View.SubView;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUIMusicPlayer.View
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class PlayListSongPage : Page
    {
        private ObservableCollection<Music> musicList;
        private MusicBrowsePage parentPage;
        private string _lastSearchText = "";
        public PlayListSongPage()
        {
            this.InitializeComponent();
            MusicListView.DragItemsCompleted += MusicListView_DragItemsCompleted;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (e.Parameter is MusicBrowsePage parentPage)
            {
                this.parentPage = parentPage;
                parentPage.DisableBackButton();
                if (_lastSearchText != AppData.searchText || musicList == null || musicList.Count == 0 || PlayListName.Text!= parentPage.currentPlayList.Name)
                {
                    PlayListName.Text = parentPage.currentPlayList.Name;
                    _lastSearchText = AppData.searchText;
                    initizeData();
                }
                else
                {
                    Debug.WriteLine("搜索条件未变更，保留当前视图状态");
                }
            }
        }
        private async void initizeData() {
            if (parentPage != null)
            {
                var musicList = await MusicDatabaseService.GetMusicByPlayListId(parentPage.currentPlayListId, AppData.searchText);
                LoadMusicAsync(musicList);
            }
        }

        private async void MusicListView_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
        {
            if (parentPage != null)
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
                    musicList[i].PlayListOrder = musicList.Count - i;
                    await MusicDatabaseService.UpdatePlayListMusicOrder(parentPage.currentPlayList.Id, musicList[i]);
                }
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

        public void SortMusicList(string sortOrder)
        {
            var order = "DefaultOrder";
            if (!string.IsNullOrEmpty(sortOrder))
            {
                order = sortOrder;
            }
            if (musicList.Count > 0)
            {
                musicList = new ObservableCollection<Music>(ToolUtils.SortMusicList("playList", order, musicList.ToList()));
            }
            MusicListView.ItemsSource = musicList;
        }

        public void UpdateMusicListView()
        {
            try
            {
                if (parentPage != null)
                {
                    if (musicList.Contains(parentPage.musicPlaybackService.currentPlayingMusic))
                    {
                        MusicListView.SelectedItem = parentPage.musicPlaybackService.currentPlayingMusic;
                        MusicListView.ScrollIntoView(parentPage.musicPlaybackService.currentPlayingMusic);
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
                parentPage.musicPlaybackService.currentPlayingList = musicList.ToList();
                await parentPage.PlayMusic(selectedMusic);
            }
        }


        private async void PlayMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (MusicListView.SelectedItem is Music selectedMusic)
            {
                parentPage.musicPlaybackService.currentPlayingList = musicList.ToList();
                await parentPage.PlayMusic(selectedMusic);
            }
        }

        private async void DeleteMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (MusicListView.SelectedItem is Music selectedMusic)
            {
                await MusicDatabaseService.RemoveMusicFromPlayList(parentPage.currentPlayListId, selectedMusic.Id);
                musicList.Remove(selectedMusic);
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

                // 获取音乐对象
                var musicItem = listViewItem.Content as Model.Music;

                // 获取右键菜单
                if (listViewItem.ContextFlyout is MenuFlyout flyout && musicItem != null)
                {
                    // 为菜单项设置DataContext
                    foreach (var menuItem in flyout.Items)
                    {
                        menuItem.DataContext = musicItem;
                    }
                }
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
                    parentPage.SelectBarAlbum(albumName);
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
                    parentPage.SelectBarArtist(artist);
                }
            }
        }
        private void MusicDetail_Click(object sender, RoutedEventArgs e)
        {
            if (MusicListView.SelectedItem is Music selectedMusic)
            {
                var musicDetailsWindow = new MusicDetailsWindow(selectedMusic);
                musicDetailsWindow.MusicDetailChanged += MusicDetailsWindow_MusicDetailChanged;
                musicDetailsWindow.Activate();
            }
        }

        private async void MusicDetailsWindow_MusicDetailChanged(object? sender, Music musicItem)
        {
            foreach (var music in musicList)
            {
                if (music.Path == musicItem.Path)
                {
                    music.Title = musicItem.Title;
                    music.Author = musicItem.Author;
                    music.Album = musicItem.Album;
                    break;
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
                    await parentPage.AddToFavourite(music);
                }
            }
        }
    }
}
