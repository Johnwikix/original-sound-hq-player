using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.View;
using WinUIMusicPlayer.View.Controls;
using WinUIMusicPlayer.View.SubView;
using ZLinq;

namespace WinUIMusicPlayer.ViewModel.Controls
{
    public partial class PlaylistDetailViewModel : ObservableObject, IDisposable
    {
        public AppViewModel AppViewModel { get; }
        public MusicBrowseViewModel MusicBrowseViewModel { get; }
        private MusicDatabaseService _db;
        private readonly ILogger<PlaylistDetailViewModel> _logger;

        private PlaylistDetailControl? _view;

        private readonly HashSet<string> _seenAlbums = new(StringComparer.Ordinal);
        private readonly HashSet<string> _seenAuthors = new(StringComparer.Ordinal);

        public Music? CoverSource { get; set => SetProperty(ref field, value); }
        public string Title { get; set => SetProperty(ref field, value); } = string.Empty;
        public string SecondTitle { get; set => SetProperty(ref field, value); } = string.Empty;
        public string ThirdTitle { get; set => SetProperty(ref field, value); } = string.Empty;
        public string CoverGlyph { get; set => SetProperty(ref field, value); } = "\uE93C";
        public CornerRadius CoverCornerRadius { get; set => SetProperty(ref field, value); } = new CornerRadius(5);

        public BulkObservableCollection<PlayListMusicItem> Songs { get; set => SetProperty(ref field, value); } = [];
        public ObservableCollection<MenuModel> MenuOptions { get; set => SetProperty(ref field, value); } = [];
        public PlayListMusicItem SelectedMusic { get; set => SetProperty(ref field, value); }
        public List<PlayListMusicItem> SelectedMusics { get; } = [];

        public IRelayCommand PlayAllCommand { get; }
        public IRelayCommand ExportCommand { get; }
        public IRelayCommand EditNameCommand { get; }
        public IRelayCommand PlayCommand { get; }
        public IRelayCommand AddMusicToCurrentPlayListCommand { get; }
        public IRelayCommand UpdateFavouriteCommand { get; }
        public IRelayCommand AddToFavourCommand { get; }
        public IRelayCommand AddToPlayListCommand { get; }
        public IRelayCommand DeleteMenuItemCommand { get; }
        public IRelayCommand ConvertAudioCommand { get; }
        public IRelayCommand OpenInExplorerCommand { get; }
        public IRelayCommand MusicDetailCommand { get; }
        public IRelayCommand ReGetLyricsCommand { get; }
        public IRelayCommand TransmitFileToUsbCommand { get; }

        public PlaylistDetailViewModel(
            MusicBrowseViewModel musicBrowseViewModel,
            AppViewModel appViewModel,
            MusicDatabaseService db,
            ILogger<PlaylistDetailViewModel> logger)
        {
            MusicBrowseViewModel = musicBrowseViewModel;
            AppViewModel = appViewModel;
            _db = db;
            _logger = logger;

            AppViewModel.PropertyChanged += OnAppVmPropertyChanged;
            AppViewModel.PlayListSongs.CollectionChanged += OnSongsCollectionChanged;

            PlayAllCommand = new RelayCommand(async () => await OnPlayAllAsync());
            ExportCommand = new RelayCommand(OnExport);
            EditNameCommand = new RelayCommand(OnEditName);
            PlayCommand = new RelayCommand(async () => await OnPlayFromSelectionAsync());
            AddMusicToCurrentPlayListCommand = new RelayCommand(OnAddMusicToCurrentPlayListFromSelection);
            UpdateFavouriteCommand = new RelayCommand<PlayListMusicItem>(OnUpdateFavourite);
            AddToFavourCommand = new RelayCommand(OnAddToFavourFromSelection);
            AddToPlayListCommand = new RelayCommand<int>(OnAddToPlayList);
            DeleteMenuItemCommand = new RelayCommand(async () => await OnDeleteFromPlaylistAsync());
            ConvertAudioCommand = new RelayCommand<string>(async tag => await OnConvertAudioAsync(tag));
            OpenInExplorerCommand = new RelayCommand(OnOpenInExplorer);
            MusicDetailCommand = new RelayCommand(OnMusicDetail);
            ReGetLyricsCommand = new RelayCommand(async () => await OnReGetLyricsAsync());
            TransmitFileToUsbCommand = new RelayCommand<UsbStorageDevice>(async dev => await OnTransmitFileToUsbAsync(dev));

            InitalizeOption();
        }

        public void SetView(PlaylistDetailControl? view) => _view = view;

        private void OnAppVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is "CurrentPlayList" or "CurrentPlayListId")
            {
                RefreshFromAppState();
            }
        }

        private void OnSongsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            RefreshTitlesOnly();
        }

        public void RefreshFromAppState()
        {
            if (AppViewModel.CurrentPlayList is { } pl)
            {
                var musics = _db.GetMusicByPlayListIdFromMem(pl.Id, AppViewModel.SearchText);
                _ = AppViewModel.PlayListSongs.ReplaceAllAsync(musics);
                CoverSource = AppViewModel.PlayListSongs.Count > 0 ? AppViewModel.PlayListSongs[0].Music : null;
                Title = pl.Name ?? string.Empty;
                ThirdTitle = ToolUtils.GetString("Playlist");
                RefreshTitlesOnly();
            }
            else
            {
                CoverSource = null;
                Title = string.Empty;
                SecondTitle = string.Empty;
                ThirdTitle = string.Empty;
            }

            if (!ReferenceEquals(Songs, AppViewModel.PlayListSongs))
            {
                Songs = AppViewModel.PlayListSongs;
            }
            UpdateMusicListView();
        }

        private void RefreshTitlesOnly()
        {
            if (AppViewModel.CurrentPlayList is null) return;
            var plm = AppViewModel.PlayListSongs;
            int count = plm.Count;
            int albums = 0;
            int authors = 0;
            _seenAlbums.Clear();
            _seenAuthors.Clear();
            for (int i = 0; i < plm.Count; i++)
            {
                var m = plm[i].Music;
                if (!string.IsNullOrEmpty(m.Album) && _seenAlbums.Add(m.Album)) albums++;
                if (!string.IsNullOrEmpty(m.Author) && _seenAuthors.Add(m.Author)) authors++;
            }
            SecondTitle = $"{count} {ToolUtils.GetString("NumberOfSongs")} · {albums} {ToolUtils.GetString("NumberOfAlbums")} · {authors} {ToolUtils.GetString("NumberOfArtists")}";
            if (AppViewModel.PlayListSongs.Count > 0)
            {
                CoverSource = AppViewModel.PlayListSongs[0].Music;
            }
        }

        public void UpdateMusicListView()
        {
            try
            {
                if (AppViewModel.CurrentPlayingMusic is not null &&
                    AppViewModel.TryFindById(AppViewModel.CurrentPlayingMusic.Id, out var m) && m is not null)
                {
                    var plm = AppViewModel.PlayListSongs;
                    for (int i = 0; i < plm.Count; i++)
                    {
                        if (plm[i].Music.Id == m.Id)
                        {
                            SelectedMusic = plm[i];
                            _view?.OnScrollToMusic(plm[i]);
                            return;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "UpdateMusicListView 滚动音乐失败: {Message}", ex.Message);
            }
        }

        public void AuthorTextBlock_Tapped(string artist)
        {
            MusicBrowseViewModel?.SelectBarArtist(artist);
        }

        public void AlbumTextBlock_Tapped(string albumName)
        {
            MusicBrowseViewModel?.SelectBarAlbum(albumName);
        }

        public void MusicListView_DoubleTapped() => _ = MusicListView_DoubleTappedAsync();

        public async Task MusicListView_DoubleTappedAsync()
        {
            if (SelectedMusic is not null && MusicBrowseViewModel is not null)
            {
                AppViewModel.SequentialPlayingList = ToMusicCollection(AppViewModel.PlayListSongs);
                await MusicBrowseViewModel.PlayMusic(music: SelectedMusic.Music, IsChangeList: true);
            }
        }

        public async void MusicListView_DragItemsCompleted()
        {
            if (AppViewModel.SelectedSortOption.Tag.ToString() == "DefaultOrder")
            {
                for (int i = 0; i < AppViewModel.PlayListSongs.Count; i++)
                {
                    AppViewModel.PlayListSongs[i].PlayListOrder = AppViewModel.PlayListSongs.Count - i;
                }
                if (AppViewModel.CurrentPlayList is not null)
                {
                    await _db.UpdatePlayListMusicOrderBatch(AppViewModel.CurrentPlayList.Id, AppViewModel.PlayListSongs);
                    await _db.GetPlayListMusic();
                }
            }
        }

        private async Task OnPlayAllAsync()
        {
            if (MusicBrowseViewModel is null) return;
            var plm = AppViewModel.PlayListSongs;
            if (plm.Count == 0) return;
            var seqList = ToMusicCollection(plm);
            AppViewModel.SequentialPlayingList = seqList;
            await MusicBrowseViewModel.PlayMusic(music: seqList[0], IsChangeList: true);
        }

        private void OnExport()
        {
            if (AppViewModel.CurrentPlayList is null) return;
            ToolUtils.ExportPlayList(AppViewModel.CurrentPlayList);
        }

        private static BulkObservableCollection<Music> ToMusicCollection(BulkObservableCollection<PlayListMusicItem> src)
        {
            var result = new BulkObservableCollection<Music>();
            var span = src.AsSpan();
            if (span.Length == 0) return result;
            var arr = new Music[span.Length];
            for (int i = 0; i < span.Length; i++)
            {
                arr[i] = span[i].Music;
            }
            result.AddRange(arr);
            return result;
        }

        private static BulkObservableCollection<Music> ToMusicCollection(List<PlayListMusicItem> src)
        {
            var result = new BulkObservableCollection<Music>();
            if (src.Count == 0) return result;
            var arr = new Music[src.Count];
            for (int i = 0; i < src.Count; i++)
            {
                arr[i] = src[i].Music;
            }
            result.AddRange(arr);
            return result;
        }

        private void OnEditName()
        {
            // 实际编辑行为在 PlaylistDetailControl code-behind 中通过 ContentDialog 完成
            // 这里保留命令以备绑定,但真正的 ContentDialog 弹窗由控件触发
        }

        private async Task OnPlayFromSelectionAsync()
        {
            if (MusicBrowseViewModel is null) return;
            if (SelectedMusics.Count == 1)
            {
                var seqList = ToMusicCollection(AppViewModel.PlayListSongs);
                AppViewModel.SequentialPlayingList = seqList;
                await MusicBrowseViewModel.PlayMusic(music: SelectedMusics[0].Music, IsChangeList: true);
            }
            else if (SelectedMusics.Count > 1)
            {
                var seqList = ToMusicCollection(SelectedMusics);
                AppViewModel.SequentialPlayingList = seqList;
                await MusicBrowseViewModel.PlayMusic(music: seqList[0], IsChangeList: true);
            }
            else if (SelectedMusic is not null)
            {
                var seqList = ToMusicCollection(AppViewModel.PlayListSongs);
                AppViewModel.SequentialPlayingList = seqList;
                await MusicBrowseViewModel.PlayMusic(music: SelectedMusic.Music, IsChangeList: true);
            }
        }

        private void OnAddMusicToCurrentPlayListFromSelection()
        {
            if (SelectedMusics.Count > 0)
            {
                foreach (var item in SelectedMusics)
                {
                    AppViewModel.AddMusicToCurrentPlayList(item.Music);
                }
            }
            else if (SelectedMusic is not null)
            {
                AppViewModel.AddMusicToCurrentPlayList(SelectedMusic.Music);
            }
        }

        private void OnUpdateFavourite(PlayListMusicItem? item)
        {
            if (item is null) return;
            item.Music.UpdateFavourite();
        }

        private void OnAddToFavourFromSelection()
        {
            if (SelectedMusics.Count == 0 && SelectedMusic is null) return;
            if (SelectedMusics.Count == 0)
            {
                SelectedMusic!.Music.UpdateFavourite();
                return;
            }
            foreach (var m in SelectedMusics)
            {
                if (!m.Music.IsFavorite) m.Music.UpdateFavourite();
            }
        }

        private void OnAddToPlayList(int playListId)
        {
            IEnumerable<Music> targets;
            if (SelectedMusics.Count > 0)
                targets = SelectedMusics.Select(x => x.Music);
            else if (SelectedMusic is not null)
                targets = [SelectedMusic.Music];
            else
                return;
            _ = _db.AddMusicListToPlayList(targets, playListId);
        }

        private async Task OnDeleteFromPlaylistAsync()
        {
            if (AppViewModel.CurrentPlayList is null) return;
            int playListId = AppViewModel.CurrentPlayList.Id;
            if (SelectedMusics.Count > 1)
            {
                var ids = new List<int>(SelectedMusics.Count);
                for (int i = 0; i < SelectedMusics.Count; i++)
                    ids.Add(SelectedMusics[i].Music.Id);
                await _db.DeleteAllMusicFromPlayList(playListId, ids);
                AppViewModel.PlayListSongs.RemoveRange(SelectedMusics);
            }
            else if (SelectedMusic is not null)
            {
                await _db.RemoveMusicFromPlayList(playListId, SelectedMusic.Music.Id);
                AppViewModel.PlayListSongs.Remove(SelectedMusic);
            }
            await _db.GetPlayListMusic();
        }

        private async Task OnConvertAudioAsync(string tag)
        {
            if (MusicBrowseViewModel is null) return;
            IEnumerable<Music> targets;
            if (SelectedMusics.Count > 0)
                targets = SelectedMusics.Select(x => x.Music);
            else if (SelectedMusic is not null)
                targets = [SelectedMusic.Music];
            else
                return;
            _ = MusicBrowseViewModel.ConvertAudio_Click(targets, tag);
        }

        private void OnOpenInExplorer()
        {
            Music? music = SelectedMusic?.Music ?? (SelectedMusics.Count > 0 ? SelectedMusics[0].Music : null);
            if (music is null) return;
            string filePath = music.Path;
            if (System.IO.File.Exists(filePath))
            {
                try
                {
                    Process.Start("explorer.exe", $"/select,\"{filePath}\"");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "OpenInExplorer 打开资源管理器时出错: {Message}", ex.Message);
                }
            }
        }

        private void OnMusicDetail()
        {
            Music? music = SelectedMusics.Count > 0 ? SelectedMusics[0].Music : SelectedMusic?.Music;
            if (music is null) return;
            var window = new MusicDetailsWindow(music);
            window.Activate();
        }

        private async Task OnReGetLyricsAsync()
        {
            IEnumerable<Music> uniqueSelectedMusics;
            if (SelectedMusics.Count > 0)
                uniqueSelectedMusics = SelectedMusics.Select(x => x.Music);
            else if (SelectedMusic is not null)
                uniqueSelectedMusics = [SelectedMusic.Music];
            else
                return;
            await AppViewModel.ReGetLyrics(uniqueSelectedMusics, SelectedMusic?.Music);
        }

        private async Task OnTransmitFileToUsbAsync(UsbStorageDevice? dev)
        {
            if (dev is null) return;
            IEnumerable<Music> targets;
            if (SelectedMusics.Count > 0)
                targets = SelectedMusics.Select(x => x.Music);
            else if (SelectedMusic is not null)
                targets = [SelectedMusic.Music];
            else
                return;
            await AppViewModel.TransmitFileToUsb(targets, dev);
        }

        private void InitalizeOption()
        {
            MenuOptions.Add(new() { Title = ToolUtils.GetString("FlyoutPlayItem"), Tag = "Play", Command = PlayCommand });
            MenuOptions.Add(new() { Title = ToolUtils.GetString("FlyoutFavoriteItem"), Tag = "AddToFavour", Command = AddToFavourCommand });
            MenuOptions.Add(new() { Title = ToolUtils.GetString("FlyoutAddToPlaylistItem"), Tag = "AddToPlayList", Children = [] });
            MenuOptions.Add(new()
            {
                Title = ToolUtils.GetString("FlyoutConvertItem"),
                Tag = "ConvertAudio",
                Children = [
                    new(){ Title="Wav",Tag="wav",Command=ConvertAudioCommand},
                    new(){ Title="Mp3",Tag="mp3",Command=ConvertAudioCommand},
                    new(){ Title="Flac",Tag="flac",Command=ConvertAudioCommand},
                    new(){ Title="Ogg",Tag="ogg",Command=ConvertAudioCommand},
                    new(){ Title="Opus",Tag="opus",Command=ConvertAudioCommand},
                ]
            });
            MenuOptions.Add(new() { Title = ToolUtils.GetString("FlyoutAddToCurrentPlayList"), Tag = "AddMusicToCurrentPlayList", Command = AddMusicToCurrentPlayListCommand });
            MenuOptions.Add(new() { Title = ToolUtils.GetString("ReGetLyrics"), Tag = "ReGetLyrics", Command = ReGetLyricsCommand });
            MenuOptions.Add(new() { Title = ToolUtils.GetString("FlyoutOpenLocationItem"), Tag = "OpenInExplorer", Command = OpenInExplorerCommand });
            MenuOptions.Add(new() { Title = ToolUtils.GetString("FlyoutPropertiesItem"), Tag = "MusicDetail", Command = MusicDetailCommand });
            MenuOptions.Add(new() { Title = ToolUtils.GetString("FlyoutRemoveFromPlaylistItem"), Tag = "DeleteMenuItem", Command = DeleteMenuItemCommand });
        }

        public void UpDateUsbDeviceMenuflyout()
        {
            var usbFlyout = MenuOptions.AsValueEnumerable().FirstOrDefault(m => (string)m.Tag == "SendToUsbDevice");
            if (AppData.UsbStorageDevices.Count == 0)
            {
                if (usbFlyout is not null) MenuOptions.Remove(usbFlyout);
                return;
            }
            if (usbFlyout is null)
            {
                usbFlyout = new MenuModel { Title = ToolUtils.GetString("SendToUsbDevice"), Tag = "SendToUsbDevice", Children = [] };
                MenuOptions.Add(usbFlyout);
            }
            usbFlyout.Children.Clear();
            var pathLabel = ToolUtils.GetString("Path");
            var freeSpaceLabel = ToolUtils.GetString("FreeSpace");
            foreach (var usb in AppData.UsbStorageDevices)
            {
                var title = $"{usb.Name} , {pathLabel}：{usb.Path} , {freeSpaceLabel}：{usb.FreeSpaceInGB}GB";
                usbFlyout.Children.Add(new() { Title = title, Tag = usb, Command = TransmitFileToUsbCommand });
            }
        }

        public void UpdateAlbumMenuOptionsPlayList()
        {
            var option = MenuOptions.AsValueEnumerable().FirstOrDefault(a => (string)a.Tag == "AddToPlayList");
            option?.Children.Clear();
            foreach (var item in AppViewModel.AllPlayList)
            {
                option?.Children.Add(new() { Title = item.Name, Tag = item.Id, Command = AddToPlayListCommand });
            }
        }

        public void Dispose()
        {
            AppViewModel.PropertyChanged -= OnAppVmPropertyChanged;
            AppViewModel.PlayListSongs.CollectionChanged -= OnSongsCollectionChanged;
        }
    }
}
