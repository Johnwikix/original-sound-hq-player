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
    public sealed partial class SongListPage : Page
    {
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

        public void UpdateFavouriteMusic(Music music)
        {
            if (musicList != null && musicList.Count > 0)
            {
                var index = musicList.IndexOf(musicList.FirstOrDefault(m => m.Id == music.Id));
                if (index != -1)
                {
                    musicList[index].isFavorite = music.isFavorite;
                }
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
                await parentPage.RemoveMusic(selectedMusic.Id);
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

        private async void MusicListView_RightTapped(object sender, RightTappedRoutedEventArgs e)
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

                    // 找到“添加到播放列表”子菜单
                    var addToPlaylistSubItem = flyout.Items[2] as MenuFlyoutSubItem;

                    // 清空之前的菜单项
                    addToPlaylistSubItem.Items.Clear();

                    // 从数据库获取播放列表
                    var playlists = await MusicDatabaseService.GetPlayListAsync();

                    // 为每个播放列表添加菜单项
                    foreach (var playlist in playlists)
                    {
                        var menuItem = new MenuFlyoutItem
                        {
                            Text = playlist.Name
                        };
                        menuItem.Click += async (s, args) =>
                        {
                            if (musicItem != null)
                            {
                                await MusicDatabaseService.AddMusicToPlayList(playlist.Id, musicItem.Id);
                            }
                        };
                        addToPlaylistSubItem.Items.Add(menuItem);
                    }
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

        private void MusicDetail_Click(object sender, RoutedEventArgs e)
        {
            if (MusicListView.SelectedItem is Music selectedMusic)
            {
                var musicDetailsWindow = new MusicDetailsWindow(selectedMusic);
                musicDetailsWindow.MusicDetailChanged += MusicDetailsWindow_MusicDetailChanged;
                musicDetailsWindow.Activate();
            }
        }

        private async void MusicDetailsWindow_MusicDetailChanged(object? sender, Music music)
        {
            if (parentPage != null)
            {
                await parentPage.LoadMusic();
            }
        }
    }
}
