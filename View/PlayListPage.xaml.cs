using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.ViewModel;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUIMusicPlayer.View
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class PlayListPage : Page
    {
        //private ObservableCollection<PlayList> playLists;
        //private MusicBrowsePage parentPage;
        public PlayListViewModel ViewModel { get; }
        public PlayListPage()
        {
            this.InitializeComponent();
            ViewModel = App.Services.GetRequiredService<PlayListViewModel>();
            ViewModel.SetCurrentPage(this);
            DataContext = this;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (e.Parameter is MusicBrowsePage parentPage)
            {
                ViewModel.SetParentPage(parentPage);
                //this.parentPage = parentPage;
                //parentPage.currentPlayList = null;
                //parentPage.DisableBackButton();
                //parentPage.refreshPage += RefreshPlayList;
                //InitializingData();
            }
        }

        //private void RefreshPlayList(object? sender, EventArgs e)
        //{
        //    InitializingData();
        //}

        //private async void InitializingData()
        //{
        //    playLists = new ObservableCollection<PlayList>(await MusicDatabaseService.GetPlayListAsync());
        //    PlayListView.ItemsSource = playLists;
        //}

        //private void MainWindow_PlayListLoaded(object? sender, List<PlayList> _playLists)
        //{
        //    try
        //    {
        //        playLists = new ObservableCollection<PlayList>(_playLists);
        //        PlayListView.ItemsSource = playLists;
        //    }
        //    catch (Exception ex)
        //    {
        //        System.Diagnostics.Debug.WriteLine($"更新播放列表时出错: {ex.Message}");
        //    }
        //}

        //private async void OpenPlayListButton_Click(object sender, RoutedEventArgs e)
        //{
        //    if (sender is Button button && button.Tag is PlayList playList)
        //    {
        //        Debug.WriteLine($"Clicked on playlist: {playList.Name}");
        //        if (parentPage != null)
        //        {
        //            parentPage.LoadPlayListSong(playList);
        //        }
        //    }
        //}
        private async void RemovePlayListButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is PlayList playList)
            {
                ViewModel.RemovePlayList(playList);
            }
            //if (sender is Button button && button.Tag is PlayList playList)
            //{
            //    await MusicDatabaseService.RemovePlayList(playList);
            //    playLists.Remove(playList);
            //}
        }

        private async void EditPlayListNameButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is PlayList playList)
            {
                ViewModel.EditPlayListName(playList, async () =>
                {
                    ContentDialog contentDialog = new ContentDialog
                    {
                        Title = ToolUtils.GetString("ModifyPlaylist"),
                        Content = new TextBox { Text = $"{playList.Name}" },
                        PrimaryButtonText = ToolUtils.GetString("PrimaryButton"),
                        CloseButtonText = ToolUtils.GetString("CloseButton"),
                        XamlRoot = this.XamlRoot
                    };
                    contentDialog.RequestedTheme = AppSettings.elementTheme;
                    ContentDialogResult result = await contentDialog.ShowAsync();

                    if (result == ContentDialogResult.Primary)
                    {
                        TextBox textBox = (TextBox)contentDialog.Content;
                        return textBox.Text;
                    }

                    return string.Empty;
                });
            }
            //if (sender is Button button && button.Tag is PlayList playList)
            //{
            //    ContentDialog contentDialog = new ContentDialog
            //    {
            //        Title = ToolUtils.GetString("ModifyPlaylist"),
            //        Content = new Microsoft.UI.Xaml.Controls.TextBox { Text = $"{playList.Name}" },
            //        PrimaryButtonText = ToolUtils.GetString("PrimaryButton"),
            //        CloseButtonText = ToolUtils.GetString("CloseButton"),
            //        XamlRoot = this.XamlRoot
            //    };
            //    contentDialog.RequestedTheme = AppSettings.elementTheme;
            //    ContentDialogResult result = await contentDialog.ShowAsync();

            //    if (result == ContentDialogResult.Primary)
            //    {
            //        Microsoft.UI.Xaml.Controls.TextBox textBox = (Microsoft.UI.Xaml.Controls.TextBox)contentDialog.Content;
            //        string playlistName = textBox.Text;
            //        if (!string.IsNullOrEmpty(playlistName))
            //        {
            //            playList.Name = playlistName;
            //            await MusicDatabaseService.UpdatePlayList(playList);
            //            InitializingData();
            //        }
            //    }
            //}
        }
        private void PlayListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var playList = PlayListView.SelectedItem as PlayList;
            ViewModel.PlayListView_SelectionChanged(playList);
            //var playList = PlayListView.SelectedItem as PlayList;
            //if (playList != null && parentPage != null)
            //{
            //    parentPage.LoadPlayListSong(playList);
            //}
        }
    }
}
