using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TagLib.Ape;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.View;
using WinUIMusicPlayer.View.SubView;
using ZLinq;

namespace WinUIMusicPlayer.ViewModel
{
    public partial class FavouritePlayListViewModel : ObservableObject
    {
        private MusicBrowsePage parentPage { get; }
        public AppViewModel AppViewModel { get; }
        private MusicDatabaseService _musicDatabaseService { get; }
        private FavouritePlayListPage currentPage { get; set; }
        public FavouritePlayListViewModel(MusicBrowsePage musicBrowsePage,AppViewModel appViewModel, MusicDatabaseService musicDatabaseService)
        {
            parentPage = musicBrowsePage;
            AppViewModel = appViewModel;
            _musicDatabaseService = musicDatabaseService;
        }

        public void SetCurrentPage(FavouritePlayListPage page)
        {
            currentPage = page;
        }

        public void ReceiveNavigation()
        {
            UpdateMusicListView();
            App.MainWindow.IsBackBtnEnable = false;
        }

        public void ShowTransmission()
        {
            parentPage?.ShowTransmission();
        }

        public void HideTransmission()
        {
            parentPage?.HideTransmission();
        }

        public void UpdateMusicListView()
        {
            try
            {
                if (AppViewModel.CurrentPlayingMusic is not null)
                {
                    var selectedMusic = AppViewModel.AllSongs.AsValueEnumerable().FirstOrDefault(music =>
                    music.Id == AppViewModel.CurrentPlayingMusic.Id);

                    if (selectedMusic is not null)
                    {
                        AppViewModel.FavoriteSongsSelectedMusic = selectedMusic;
                        currentPage?.OnScrollToMusic(selectedMusic);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"滚动音乐失败: {ex.Message}");
            }
        }

        public async Task DragItems()
        {
            if (AppViewModel.SelectedSortOption.Tag == "DefaultOrder")
            {
                for (int i = 0; i < AppViewModel.FavoriteSongs.Count; i++)
                {
                    AppViewModel.FavoriteSongs[i].Order = AppViewModel.FavoriteSongs.Count - i;
                }
                await _musicDatabaseService.UpdateAllAsync([.. AppViewModel.FavoriteSongs]);
            }
        }

        //public void ClearUsbDeviceMusicList()
        //{
        //    //App.MainWindow.DispatcherQueue.TryEnqueue(() =>
        //    //{
        //    //    foreach (var music in MusicList)
        //    //    {
        //    //        music.IsExistOnDevice = 0;
        //    //    }
        //    //});
        //}

        //public void RefreshUsbDeviceMusicList()
        //{
        //    ToolUtils.RefreshUsbDeviceMusicList(MusicList);
        //}

        public async Task<bool> IsDeleteFromDisk()
        {
            if (parentPage is null)
            {
                return false;
            }
            return await parentPage.AreUSureDeleteFromDisk();
        }

        public void MusicListView_DoubleTapped(Music selectedMusic)
        {
            if (selectedMusic is not null && parentPage is not null)
            {
                AppViewModel.SequentialPlayingList = new(AppViewModel.FavoriteSongs);
                parentPage.PlayMusic(music: selectedMusic, IsChangeList: true);
            }
        }

        public void MusicDetail_Click()
        {
            var musicDetailsWindow = new MusicDetailsWindow(AppViewModel.FavoriteSongsSelectedMusic);
            musicDetailsWindow.Activate();
        }

        public void PlayMenuItem_Click(IEnumerable<Music> uniqueSelectedMusics)
        {
            if (uniqueSelectedMusics is not null && uniqueSelectedMusics.AsValueEnumerable().Count() > 1)
            {
                AppViewModel.SequentialPlayingList = new(uniqueSelectedMusics);
                parentPage.PlayMusic(music: uniqueSelectedMusics.AsValueEnumerable().First(), IsChangeList: true);
            }
            else
            {
                AppViewModel.SequentialPlayingList = new(AppViewModel.FavoriteSongs);
                parentPage.PlayMusic(music: AppViewModel.FavoriteSongsSelectedMusic, IsChangeList: true);
            }
        }

        public async void ReGetLyrics_Click(IEnumerable<Music> uniqueSelectedMusics)
        {
            AppViewModel.ReGetLyrics(uniqueSelectedMusics, AppViewModel.FavoriteSongsSelectedMusic);           
        }

        public async void DeleteMenuItem_Click(IEnumerable<Music> uniqueSelectedMusics)
        {
            if (uniqueSelectedMusics is not null && uniqueSelectedMusics.AsValueEnumerable().Count() > 1)
            {
                for (int i = AppViewModel.AllSongs.Count - 1; i >= 0; i--)
                {
                    if (uniqueSelectedMusics.AsValueEnumerable().Contains(AppViewModel.AllSongs[i]))
                    {
                        if (ToolUtils.DeleteFileFromDisk((AppViewModel.AllSongs[i].Path)))
                        {
                            AppViewModel.AllSongs.FirstOrDefault(m => m.Id == AppViewModel.FavoriteSongsSelectedMusic.Id)?.Remove();
                        }
                    }
                }
            }
            else
            {
                if (ToolUtils.DeleteFileFromDisk(AppViewModel.FavoriteSongsSelectedMusic.Path))
                {
                    AppViewModel.AllSongs.FirstOrDefault(m => m.Id == AppViewModel.FavoriteSongsSelectedMusic.Id)?.Remove();
                }
            }
        }

        public async void SetAsFavoriteMenuItem_Click(List<Music> uniqueSelectedMusics)
        {
            if (uniqueSelectedMusics is not null && uniqueSelectedMusics.Count() > 1)
            {
                foreach (Music item in uniqueSelectedMusics)
                {
                    AppViewModel.AllSongs.FirstOrDefault(m => m.Id == item.Id)?.UpdateFavourite();
                }
            }
            else
            {
                if (AppViewModel.FavoriteSongsSelectedMusic is not null)
                {
                    AppViewModel.AllSongs.FirstOrDefault(m => m.Id == AppViewModel.FavoriteSongsSelectedMusic.Id)?.UpdateFavourite();
                }
            }
        }

        public void OpenInExplorer_Click()
        {
            var filePath = AppViewModel.FavoriteSongsSelectedMusic.Path;
            if (System.IO.File.Exists(filePath))
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

        public async Task ConvertAudio_Click(IEnumerable<Music> uniqueSelectedMusics, MenuFlyoutItem? menuItem)
        {
            parentPage?.ViewModel?.ConvertAudio_Click(uniqueSelectedMusics, menuItem);
        }

        public void AlbumTextBlock_Tapped(TextBlock textBlock)
        {
            string albumName = textBlock.Text;
            if (parentPage is not null)
            {
                parentPage.SelectBarAlbum(albumName);
            }
        }

        public void AuthorTextBlock_Tapped(TextBlock textBlock)
        {
            string artist = textBlock.Text;
            if (parentPage is not null)
            {
                parentPage.SelectBarArtist(artist);
            }
        }
    }
}
