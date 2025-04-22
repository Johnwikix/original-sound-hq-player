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
using System.Threading.Tasks;
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
    public sealed partial class SongCollectionPage : Page
    {
        private ObservableCollection<Music> musicList;
        private MusicBrowsePage parentPage;
        public SongCollectionPage()
        {
            this.InitializeComponent();

        }

        // 然后在 SongListPage 的 OnNavigatedTo 中接收参数
        protected async override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (e.Parameter is MusicBrowsePage parentPage)
            {
                this.parentPage = parentPage;
                parentPage.DisableBackButton();
                parentPage.refreshSong += RefreshSong;
                RefreshPage();
            }
        }

        private async void RefreshPage()
        {
            if (parentPage != null)
            {
                if (parentPage.pageType == "album")
                {
                    var musicList = await MusicDatabaseService.GetAlbumMusicAsync(parentPage.currentAlbumName, AppData.searchText);
                    await LoadMusicAsync(musicList, parentPage.pageType);
                }
                if (parentPage.pageType == "artist")
                {
                    var musicList = await MusicDatabaseService.GetArtistMusicAsync(parentPage.currentArtistName, AppData.searchText);
                    await LoadMusicAsync(musicList, parentPage.pageType);
                }
                if (parentPage.pageType == "folder")
                {
                    var musicList = await MusicDatabaseService.GetFolderMusicAsync(parentPage.currentFolderName, AppData.searchText);
                    await LoadMusicAsync(musicList, parentPage.pageType);
                }
            }
        }

        private void RefreshSong(object? sender, EventArgs e)
        {
            RefreshPage();
        }

        public void SortMusicList(string sortOrder, string type)
        {
            var order = "DefaultOrder";
            if (!string.IsNullOrEmpty(sortOrder))
            {
                order = sortOrder;
            }
            if (musicList.Count > 0)
            {
                musicList = new ObservableCollection<Music>(ToolUtils.SortMusicList(type, order, musicList.ToList()));
            }
            MusicListView.ItemsSource = musicList;
        }

        public async Task LoadMusicAsync(List<Music> musics, string type = null)
        {
            try
            {
                musicList = new ObservableCollection<Music>(musics);
                if (type != null)
                {
                    SortMusicList("DefaultOrder", type);
                }
                else
                {
                    MusicListView.ItemsSource = musicList;
                }
                UpdateMusicListView();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"加载音乐列表失败: {ex.Message}");
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
                musicList.Remove(selectedMusic);
                await MusicDatabaseService.RemoveMusic(selectedMusic.Id);
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
                    parentPage.SelectBarArtist(artist);
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
                    parentPage.SelectBarAlbum(albumName);
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
            //musicList = new ObservableCollection<Music>(ToolUtils.UpdateMusicInList(musicList.ToList(), music));
            //MusicListView.ItemsSource = musicList;
            //UpdateMusicListView();
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
    }
}
