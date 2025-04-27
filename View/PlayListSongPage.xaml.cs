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
using Windows.Media.Playlists;
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
        private AudioConverterService converterService;
        private ProgressDialog progressDialog;
        public PlayListSongPage()
        {
            this.InitializeComponent();
            MusicListView.DragItemsCompleted += MusicListView_DragItemsCompleted;
            converterService = new AudioConverterService();
            progressDialog = new ProgressDialog("正在转换");
            musicList = new ObservableCollection<Music>();
            MusicListView.ItemsSource = musicList;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (e.Parameter is MusicBrowsePage parentPage)
            {
                this.parentPage = parentPage;
                parentPage.DisableBackButton();
                parentPage.refreshPage += RefreshPlayList;
                PlayListName.Text = parentPage.currentPlayList.Name;
                initizeData();
                //if (_lastSearchText != AppData.searchText || musicList == null || musicList.Count == 0 || PlayListName.Text!= parentPage.currentPlayList.Name)
                //{
                //    PlayListName.Text = parentPage.currentPlayList.Name;
                //    _lastSearchText = AppData.searchText;
                //    initizeData();
                //}
                //else
                //{
                //    UpdateMusicListView();
                //    Debug.WriteLine("搜索条件未变更，保留当前视图状态");
                //}                
            }
        }

        private void RefreshPlayList(object? sender, EventArgs e)
        {
            initizeData();
        }

        private async void initizeData()
        {
            if (parentPage != null)
            {
                var musicList = MusicDatabaseService.GetMusicByPlayListIdFromMem(parentPage.currentPlayListId, AppData.searchText);
                LoadMusicAsync(musicList);
            }
        }

        private async void MusicListView_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
        {
            if (parentPage != null)
            {
                for (int i = 0; i < musicList.Count; i++)
                {
                    musicList[i].PlayListOrder = musicList.Count - i;                   
                    await MusicDatabaseService.UpdatePlayListMusicOrder(parentPage.currentPlayList.Id, musicList[i]);
                    await MusicDatabaseService.GetPlayListMusic();
                }
            }
        }

        public void LoadMusicAsync(List<Music> musics)
        {
            try
            {

                musicList.Clear();
                foreach (var music in musics)
                {
                    musicList.Add(music);
                }
                //MusicListView.ItemsSource = musicList;
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
            List<Music> musics = new List<Music>();
            if (!string.IsNullOrEmpty(sortOrder))
            {
                order = sortOrder;
            }
            if (musicList.Count > 0)
            {
                musics = ToolUtils.SortMusicList("playList", order, musicList.ToList());
            }
            musicList.Clear();
            foreach (var music in musics) {
                musicList.Add(music);
            }
        }

        public void UpdateMusicListView()
        {
            try
            {
                if (musicList.Contains(parentPage.musicPlaybackService.currentPlayingMusic))
                {
                    Music selectedMusic = null;
                    foreach (var music in musicList)
                    {
                        if (music.Id == parentPage.musicPlaybackService.currentPlayingMusic.Id)
                        {
                            selectedMusic = music;
                        }
                    }
                    MusicListView.SelectedItem = selectedMusic;
                    MusicListView.ScrollIntoView(selectedMusic);
                }
                //if (parentPage != null)
                //{
                //    if (musicList.Contains(parentPage.musicPlaybackService.currentPlayingMusic))
                //    {
                //        MusicListView.SelectedItem = parentPage.musicPlaybackService.currentPlayingMusic;
                //        MusicListView.ScrollIntoView(parentPage.musicPlaybackService.currentPlayingMusic);
                //    }
                //}
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"滚动音乐失败: {ex.Message}");
            }
        }

        private List<Music> GetUniqueSelectedItems()
        {
            List<Music> uniqueItems = new List<Music>();
            var selectedItems = MusicListView.SelectedItems;
            foreach (var item in selectedItems)
            {
                if (item is Music music)
                {
                    uniqueItems.Add(music);
                }
            }
            return uniqueItems;
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
            List<Music> uniqueSelectedMusics = GetUniqueSelectedItems();
            if (uniqueSelectedMusics != null && uniqueSelectedMusics.Count > 1)
            {
                parentPage.musicPlaybackService.currentPlayingList = uniqueSelectedMusics;
                await parentPage.PlayMusic(uniqueSelectedMusics[0]);
            }
            else
            {
                if (MusicListView.SelectedItem is Music selectedMusic)
                {
                    parentPage.musicPlaybackService.currentPlayingList = musicList.ToList();
                    await parentPage.PlayMusic(selectedMusic);
                }
            }            
        }

        private async void DeleteMenuItem_Click(object sender, RoutedEventArgs e)
        {
            List<Music> uniqueSelectedMusics = GetUniqueSelectedItems();
            if (uniqueSelectedMusics != null && uniqueSelectedMusics.Count > 1)
            {
                foreach (Music item in uniqueSelectedMusics)
                {
                    await MusicDatabaseService.RemoveMusicFromPlayList(parentPage.currentPlayListId, item.Id);
                    musicList.Remove(item);
                }
            }
            else
            {
                if (MusicListView.SelectedItem is Music selectedMusic)
                {
                    await MusicDatabaseService.RemoveMusicFromPlayList(parentPage.currentPlayListId, selectedMusic.Id);
                    musicList.Remove(selectedMusic);
                }
            }           
        }

        private async void SetAsFavoriteMenuItem_Click(object sender, RoutedEventArgs e)
        {
            List<Music> uniqueSelectedMusics = GetUniqueSelectedItems();
            if (uniqueSelectedMusics != null && uniqueSelectedMusics.Count > 1)
            {
                foreach (Music item in uniqueSelectedMusics)
                {
                    await parentPage.AddToFavourite(item);
                }
            }
            else
            {
                if (MusicListView.SelectedItem is Music selectedMusic)
                {
                    await parentPage.AddToFavourite(selectedMusic);
                }
            }
            //if (MusicListView.SelectedItem is Music selectedMusic)
            //{
            //    await parentPage.AddToFavourite(selectedMusic);
            //}
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
            List<Music> uniqueSelectedMusics = GetUniqueSelectedItems();
            if (uniqueSelectedMusics != null && uniqueSelectedMusics.Count > 1)
            {
                var menuItem = sender as MenuFlyoutItem;
                if (menuItem != null && menuItem.Tag.ToString() != null)
                {
                    int progressBarValue = 0;
                    progressDialog.RequestedTheme = AppSettings.elementTheme;
                    await progressDialog.UpdateProgress(progressBarValue);
                    converterService.updateProgress += (sender, progress) =>
                    {
                        if (progressBarValue < (int)progress)
                        {
                            progressBarValue = (int)progress;
                        }
                        if (progressBarValue < 100)
                        {
                            _ = progressDialog.UpdateProgress(progressBarValue);
                        }
                    };
                    progressDialog.XamlRoot = this.XamlRoot;
                    _ = progressDialog.ShowAsync();

                    List<Task> conversionTasks = new List<Task>();
                    foreach (Music item in uniqueSelectedMusics)
                    {
                        Task conversionTask = converterService.ConvertAudio2Wav(item, menuItem.Tag.ToString());
                        conversionTasks.Add(conversionTask);
                    }
                    await Task.WhenAll(conversionTasks);
                    _ = progressDialog.UpdateProgress(100);
                }
            }
            else
            {
                var menuItem = sender as MenuFlyoutItem;
                if (menuItem != null && menuItem.Tag.ToString() != null)
                {
                    if (MusicListView.SelectedItem is Music selectedMusic)
                    {
                        int progressBarValue = 0;
                        progressDialog.RequestedTheme = AppSettings.elementTheme;
                        _ = progressDialog.UpdateProgress(progressBarValue);
                        _ = converterService.ConvertAudio2Wav(selectedMusic, menuItem.Tag.ToString());
                        converterService.updateProgress += (sender, progress) =>
                        {
                            progressBarValue = (int)progress;
                            _ = progressDialog.UpdateProgress(progressBarValue);
                        };
                        if (progressBarValue < 100)
                        {
                            progressDialog.XamlRoot = this.XamlRoot;
                            _ = progressDialog.ShowAsync();
                        }

                    }
                }
            }
        }

        private void MusicListView_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            var targetElement = e.OriginalSource as FrameworkElement;
            ListViewItem listViewItem = ToolUtils.FindParent<ListViewItem>(targetElement);
            if (listViewItem != null)
            {
                var musicItem = listViewItem.Content as Model.Music;
                // 检查当前指向的元素是否已在选中项列表中
                bool isCurrentItemSelected = false;
                foreach (var item in MusicListView.SelectedItems)
                {
                    if (item is Music selectedMusic && musicItem != null && selectedMusic.Id == musicItem.Id)
                    {
                        isCurrentItemSelected = true;
                        break;
                    }
                }
                // 如果当前项不在选中列表中，则清除现有选择并选中当前项
                if (!isCurrentItemSelected)
                {
                    MusicListView.SelectedItems.Clear();
                    listViewItem.IsSelected = true;
                    MusicListView.SelectedItem = musicItem;
                }
                List<Music> uniqueSelectedMusics = GetUniqueSelectedItems();
                // 设置右键菜单
                if (listViewItem.ContextFlyout is MenuFlyout flyout && musicItem != null)
                {
                    // 为菜单项设置DataContext
                    foreach (var menuItem in flyout.Items)
                    {
                        menuItem.DataContext = musicItem;
                    }                   
                }
            }
            //var targetElement = e.OriginalSource as FrameworkElement;
            //ListViewItem listViewItem = ToolUtils.FindParent<ListViewItem>(targetElement);
            //if (listViewItem != null)
            //{
            //    listViewItem.IsSelected = true;
            //    MusicListView.SelectedItem = listViewItem.Content;

            //    // 获取音乐对象
            //    var musicItem = listViewItem.Content as Model.Music;

            //    // 获取右键菜单
            //    if (listViewItem.ContextFlyout is MenuFlyout flyout && musicItem != null)
            //    {
            //        // 为菜单项设置DataContext
            //        foreach (var menuItem in flyout.Items)
            //        {
            //            menuItem.DataContext = musicItem;
            //        }
            //    }
            //}
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
                    music.Year = musicItem.Year;
                    music.TrackNumber = musicItem.TrackNumber;
                    music.Lyrics = musicItem.Lyrics;
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
