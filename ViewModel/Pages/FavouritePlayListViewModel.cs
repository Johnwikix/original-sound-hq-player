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
        public Music SelectedMusic { get; set => SetProperty(ref field, value); }
        public List<Music> SelectedMusics { get; set; } = [];
        public ObservableCollection<MenuModel> FavouriteSongsMenuOptions { get; set => SetProperty(ref field, value); } = [];
        private MusicBrowsePage parentPage { get; }
        public AppViewModel AppViewModel { get; }
        private MusicDatabaseService _musicDatabaseService { get; }
        private FavouritePlayListPage currentPage { get; set; }
        public FavouritePlayListViewModel(MusicBrowsePage musicBrowsePage,AppViewModel appViewModel, MusicDatabaseService musicDatabaseService)
        {
            parentPage = musicBrowsePage;
            AppViewModel = appViewModel;
            _musicDatabaseService = musicDatabaseService;
            InitalizeOption();
        }

        private void InitalizeOption()
        {
            FavouriteSongsMenuOptions.Add(new() { Title = "播放", Tag = "Play", Command = PlayCommand });
            FavouriteSongsMenuOptions.Add(new() { Title = "取消最爱", Tag = "AddToFavour", Command = AddToFavourCommand });
            FavouriteSongsMenuOptions.Add(new() { Title = "添加到播放列表", Tag = "AddToPlayList", Children = [] });
            FavouriteSongsMenuOptions.Add(new()
            {
                Title = "转换为",
                Tag = "ConvertAudio",
                Children = [
                new(){ Title="WAV",Tag="wav",Command=ConvertAudioCommand},
                new(){ Title="MP3",Tag="mp3",Command=ConvertAudioCommand},
                new(){ Title="FLAC",Tag="flac",Command=ConvertAudioCommand},
                new(){ Title="Ogg",Tag="ogg",Command=ConvertAudioCommand},
                new(){ Title="Opus",Tag="opus",Command=ConvertAudioCommand},
                ]
            });
            FavouriteSongsMenuOptions.Add(new() { Title = "添加到当前播放列表", Tag = "AddMusicToCurrentPlayList", Command = AddMusicToCurrentPlayListCommand });
            FavouriteSongsMenuOptions.Add(new() { Title = "重新获取歌词", Tag = "ReGetLyrics", Command = ReGetLyricsCommand });
            FavouriteSongsMenuOptions.Add(new() { Title = "打开文件位置", Tag = "OpenInExplorer", Command = OpenInExplorerCommand });
            FavouriteSongsMenuOptions.Add(new() { Title = "属性", Tag = "MusicDetail", Command = MusicDetailCommand });
            FavouriteSongsMenuOptions.Add(new() { Title = "从磁盘中删除", Tag = "DeleteMenuItem", Command = DeleteMenuItemCommand });
        }

        public void UpdateAlbumMenuOptionsPlayList()
        {
            var option = FavouriteSongsMenuOptions.AsValueEnumerable().FirstOrDefault(a => (string)a.Tag == "AddToPlayList");
            option?.Children.Clear();
            foreach (var item in AppViewModel.AllPlayList)
            {
                option?.Children.Add(new() { Title = item.Name, Tag = item.Id, Command = AddToPlayListCommand });
            }
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
                        SelectedMusic = selectedMusic;
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
                parentPage.PlayMusic(music: SelectedMusic, IsChangeList: true);
            }
        }       

        [RelayCommand]
        public async Task ConvertAudio(string tag)
        {
            parentPage?.ViewModel?.ConvertAudio_Click(SelectedMusics, tag);
        }

        [RelayCommand]
        public async Task DeleteMenuItem()
        {
            if (SelectedMusics is not null && SelectedMusics.AsValueEnumerable().Count() > 1)
            {
                foreach (var item in SelectedMusics)
                {
                    if (ToolUtils.DeleteFileFromDisk(item.Path))
                    {
                        AppViewModel.AllSongs.FirstOrDefault(m => m.Id == SelectedMusic.Id)?.Remove();
                    }
                }
            }
            else
            {
                if (ToolUtils.DeleteFileFromDisk(SelectedMusic.Path))
                {
                    AppViewModel.AllSongs.FirstOrDefault(m => m.Id == SelectedMusic.Id)?.Remove();
                }
            }
        }

        [RelayCommand]
        public void OpenInExplorer()
        {
            if (SelectedMusic is not null)
            {
                var filePath = SelectedMusic.Path;
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
        }

        public void OpenInExplorer_Click()
        {
            var filePath = SelectedMusic.Path;
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

        [RelayCommand]
        public void MusicDetail()
        {
            if (SelectedMusics.Count > 0)
            {
                var musicDetailsWindow = new MusicDetailsWindow(SelectedMusics[0]);
                musicDetailsWindow.Activate();
            }
        }
        [RelayCommand]
        public void ReGetLyrics()
        {
            _ = AppViewModel.ReGetLyrics(SelectedMusics, SelectedMusic);
        }

        [RelayCommand]
        private void Play()
        {
            if (SelectedMusics.Count == 1)
            {
                if (parentPage is not null)
                {
                    AppViewModel.SequentialPlayingList = new([.. AppViewModel.AllSongsView.Cast<Music>()]);
                    parentPage.PlayMusic(music: SelectedMusics[0], IsChangeList: true);
                }
            }
            if (SelectedMusics.Count > 1)
            {
                if (parentPage is not null)
                {
                    AppViewModel.SequentialPlayingList = new(SelectedMusics);
                    parentPage.PlayMusic(music: SelectedMusics[0], IsChangeList: true);
                }
            }
        }
        [RelayCommand]
        private void AddToFavour()
        {
            if (SelectedMusics.Count > 0)
            {
                foreach (var music in SelectedMusics)
                {
                    music.UpdateFavourite();
                }
            }
        }

        [RelayCommand]
        private void AddToPlayList(int playListId)
        {
            if (SelectedMusics.Count > 0)
            {
                _ = _musicDatabaseService.AddMusicListToPlayList(SelectedMusics, playListId);
            }
        }

        [RelayCommand]
        public void AddMusicToCurrentPlayList()
        {
            if (SelectedMusics.Count > 0)
            {
                foreach (var item in SelectedMusics)
                {
                    AppViewModel.AddMusicToCurrentPlayList(item);
                }
            }
        }

        public void AlbumTextBlock_Tapped(TextBlock textBlock)
        {
            string albumName = textBlock.Text;
            parentPage?.SelectBarAlbum(albumName);
        }

        public void AuthorTextBlock_Tapped(TextBlock textBlock)
        {
            string artist = textBlock.Text;
            parentPage?.SelectBarArtist(artist);
        }
    }
}
