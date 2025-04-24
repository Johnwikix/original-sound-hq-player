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
    public sealed partial class FavouritePlayListPage : Page
    {
        private ObservableCollection<Music> musicList;
        private MusicBrowsePage parentPage;
        private string _lastSearchText = "";
        private AudioConverterService converterService;
        private ProgressDialog progressDialog;
        public FavouritePlayListPage()
        {
            this.InitializeComponent();
            MusicListView.DragItemsCompleted += MusicListView_DragItemsCompleted;
            converterService = new AudioConverterService();
            progressDialog = new ProgressDialog("正在转换");
            musicList = new ObservableCollection<Music>();
            MusicListView.ItemsSource = musicList;
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
                await MusicDatabaseService.UpdateMuisc(musicList[i]);
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
                parentPage.refreshPage += RefreshMusicList;
                InitializeData();
                //if (_lastSearchText != AppData.searchText || musicList == null || musicList.Count == 0)
                //{
                //    _lastSearchText = AppData.searchText;
                //    InitializeData();
                //}
                //else
                //{
                //    UpdateMusicListView();
                //    Debug.WriteLine("搜索条件未变更，保留当前视图状态");
                //}
            }
        }

        private void RefreshMusicList(object? sender, EventArgs e)
        {
            InitializeData();
        }

        public void SortMusicList(string sortOrder)
        {
            var order = "DefaultOrder";
            List<Music> musics = new List<Music>();            
            if (!string.IsNullOrEmpty(sortOrder))
            {
                order = sortOrder;
            }
            if (musicList.Count > 0)
            {
                musics = ToolUtils.SortMusicList("favour", order, musicList.ToList());
            }
            musicList.Clear();
            foreach (Music music in musics) {
                musicList.Add(music);
            }
        }

        private async void InitializeData()
        {
            if (parentPage != null)
            {
                var musicList = await MusicDatabaseService.GetFavoriteMusicAsync(AppData.searchText);
                LoadMusicAsync(musicList);
            }
        }

        public void LoadMusicAsync(List<Music> musics)
        {
            try
            {
                musicList.Clear();
                foreach (Music music in musics) {
                    musicList.Add(music);
                }
                //musicList = new ObservableCollection<Music>(musics);
                //MusicListView.ItemsSource = musicList;
                UpdateMusicListView();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"加载音乐列表失败: {ex.Message}");
            }
        }

        public void UpdateFavouriteMusic(Music music)
        {
            if (music.isFavorite)
            {
                AddMusicToTop(music);
            }
            else
            {
                RemoveMusic(music);
            }
        }

        private void AddMusicToTop(Music newMusic)
        {
            int maxOrder = musicList.Any() ? musicList.Max(m => m.Order) : 0;
            newMusic.Order = maxOrder + 1;
            musicList.Insert(0, newMusic);
            //MusicListView.ItemsSource = musicList;
        }

        private void RemoveMusic(Music musicToRemove)
        {
            var music = musicList.FirstOrDefault(m => m.Id == musicToRemove.Id);
            if (music != null)
            {
                musicList.Remove(music);
                //MusicListView.ItemsSource = musicList;
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
                await MusicDatabaseService.RemoveMusic(selectedMusic.Id);
                musicList.Remove(selectedMusic);
            }
        }

        private async void SetAsFavoriteMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (MusicListView.SelectedItem is Music selectedMusic)
            {
                if (selectedMusic.isFavorite)
                {
                    musicList.Remove(selectedMusic);
                }
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

        private async void ConvertAudio_Click(object sender, RoutedEventArgs e)
        {
            var menuItem = sender as MenuFlyoutItem;
            if (menuItem != null && menuItem.Tag.ToString() != null)
            {
                if (MusicListView.SelectedItem is Music selectedMusic)
                {
                    int progressBarValue = 0;
                    progressDialog.RequestedTheme = AppSettings.elementTheme;
                    progressDialog.UpdateProgress(0);
                    converterService.ConvertAudio2Wav(selectedMusic, menuItem.Tag.ToString());
                    converterService.updateProgress += (sender, progress) =>
                    {
                        progressBarValue = (int)progress;
                        progressDialog.UpdateProgress(progressBarValue);
                    };
                    if (progressBarValue < 100)
                    {
                        progressDialog.XamlRoot = this.XamlRoot;
                        progressDialog.ShowAsync();
                    }

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
            //musicList = new ObservableCollection<Music>(ToolUtils.UpdateMusicInList(musicList.ToList(), music));
            //MusicListView.ItemsSource = musicList;
            //UpdateMusicListView();
        }

        private async void IsFavouriteIconButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button != null && button.Tag is Music music)
            {
                if (music != null)
                {
                    ((FontIcon)button.Content).Glyph = !music.isFavorite ? "\ueb52" : "\ueb51";
                    if (music.isFavorite)
                    {
                        musicList.Remove(music);
                    }
                    await parentPage.AddToFavourite(music);
                }
            }
        }
    }
}
