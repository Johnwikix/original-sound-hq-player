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
using System.Threading;
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
    public partial class MusicGroupDetailViewModel : ObservableObject, IDisposable
    {
        public enum GroupKind { None, Album, Artist, Folder }

        private static readonly Lock _instancesLock = new();
        private static readonly List<MusicGroupDetailViewModel> _instances = [];

        public AppViewModel AppViewModel { get; }
        public MusicBrowseViewModel MusicBrowseViewModel { get; }
        private MusicDatabaseService _db;
        private readonly ILogger<MusicGroupDetailViewModel> _logger;

        private GroupKind _kind = GroupKind.None;
        private MusicGroupDetailControl? _view;
        private bool _songsBound;

        public GroupKind PageKind { get; private set; } = GroupKind.None;

        private readonly HashSet<string> _seenAlbums = new(StringComparer.Ordinal);
        private readonly HashSet<string> _seenAuthors = new(StringComparer.Ordinal);
        private readonly List<string> _authorsList = new(8);

        public Music? CoverSource { get; set => SetProperty(ref field, value); }
        public string Title { get; set => SetProperty(ref field, value); } = string.Empty;
        public string SecondTitle { get; set => SetProperty(ref field, value); } = string.Empty;
        public string ThirdTitle { get; set => SetProperty(ref field, value); } = string.Empty;
        public string CoverGlyph { get; set => SetProperty(ref field, value); } = "\uE93C";
        public CornerRadius CoverCornerRadius { get; set => SetProperty(ref field, value); } = new CornerRadius(5);
        public bool IsAlbumDetail { get; set => SetProperty(ref field, value); }
        public bool IsClosingForTransition { get; set; }

        public BulkObservableCollection<Music> Songs { get; set => SetProperty(ref field, value); } = [];
        public ObservableCollection<MenuModel> MenuOptions { get; set => SetProperty(ref field, value); } = [];
        public Music SelectedMusic { get; set => SetProperty(ref field, value); }
        public List<Music> SelectedMusics { get; } = [];

        public IRelayCommand PlayAllCommand { get; }
        public IRelayCommand AlbumInfoChangedCommand { get; }
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

        public MusicGroupDetailViewModel(
            MusicBrowseViewModel musicBrowseViewModel,
            AppViewModel appViewModel,
            MusicDatabaseService db,
            ILogger<MusicGroupDetailViewModel> logger)
        {
            MusicBrowseViewModel = musicBrowseViewModel;
            AppViewModel = appViewModel;
            _db = db;
            _logger = logger;

            AppViewModel.PropertyChanged += OnAppVmPropertyChanged;
            AppViewModel.AlbumSongs.CollectionChanged += OnSongsCollectionChanged;
            AppViewModel.ArtistSongs.CollectionChanged += OnSongsCollectionChanged;
            AppViewModel.FolderSongs.CollectionChanged += OnSongsCollectionChanged;

            PlayAllCommand = new RelayCommand(async () => await OnPlayAllAsync());
            AlbumInfoChangedCommand = new RelayCommand(OnAlbumInfoChanged);
            PlayCommand = new RelayCommand(async () => await OnPlayFromSelectionAsync());
            AddMusicToCurrentPlayListCommand = new RelayCommand(OnAddMusicToCurrentPlayListFromSelection);
            UpdateFavouriteCommand = new RelayCommand<Music>(OnUpdateFavourite);
            AddToFavourCommand = new RelayCommand(OnAddToFavourFromSelection);
            AddToPlayListCommand = new RelayCommand<int>(OnAddToPlayList);
            DeleteMenuItemCommand = new RelayCommand(async () => await OnDeleteAsync());
            ConvertAudioCommand = new RelayCommand<string>(async tag => await OnConvertAudioAsync(tag));
            OpenInExplorerCommand = new RelayCommand(OnOpenInExplorer);
            MusicDetailCommand = new RelayCommand(OnMusicDetail);
            ReGetLyricsCommand = new RelayCommand(async () => await OnReGetLyricsAsync());
            TransmitFileToUsbCommand = new RelayCommand<UsbSendTarget>(async target => await OnTransmitFileToUsbAsync(target));

            InitalizeOption();

            lock (_instancesLock)
            {
                _instances.Add(this);
            }
        }

        public static void UpdateAll(Action<MusicGroupDetailViewModel> action)
        {
            lock (_instancesLock)
            {
                foreach (var vm in _instances)
                {
                    action(vm);
                }
            }
        }

        public void SetPageKind(GroupKind kind)
        {
            if (PageKind == kind) return;
            PageKind = kind;
            RefreshFromAppState();
        }

        public void SetView(MusicGroupDetailControl? view) => _view = view;

        public GroupKind CurrentKind => _kind;

        private void OnAppVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (IsClosingForTransition) return;
            if (e.PropertyName == "CurrentAlbumObj" && PageKind == GroupKind.Album) RefreshFromAppState();
            else if (e.PropertyName == "CurrentArtistObj" && PageKind == GroupKind.Artist) RefreshFromAppState();
            else if (e.PropertyName == "CurrentFolderObj" && PageKind == GroupKind.Folder) RefreshFromAppState();
        }

        private void OnSongsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (_kind == GroupKind.None) return;
            if (!_songsBound) return;
            RefreshTitlesOnly();
        }

        public void RefreshFromAppState()
        {
            switch (PageKind)
            {
                case GroupKind.Album:
                    if (AppViewModel.CurrentAlbumObj is { } album && !string.IsNullOrEmpty(album.Album))
                    {
                        _kind = GroupKind.Album;
                        _songsBound = true;
                        CoverSource = album;
                        Title = album.Album;
                        CoverGlyph = "\uE93C";
                        CoverCornerRadius = new CornerRadius(5);
                        IsAlbumDetail = true;
                        BindSongs(AppViewModel.AlbumSongs);
                        RefreshAlbumTitles(album);
                        return;
                    }
                    break;
                case GroupKind.Artist:
                    if (AppViewModel.CurrentArtistObj is { } artist && !string.IsNullOrEmpty(artist.Author))
                    {
                        _kind = GroupKind.Artist;
                        _songsBound = true;
                        CoverSource = artist;
                        Title = artist.Author;
                        CoverGlyph = "\uE77B";
                        CoverCornerRadius = new CornerRadius(75);
                        IsAlbumDetail = false;
                        BindSongs(AppViewModel.ArtistSongs);
                        RefreshArtistTitles(artist);
                        return;
                    }
                    break;
                case GroupKind.Folder:
                    if (AppViewModel.CurrentFolderObj is { } folder && !string.IsNullOrEmpty(folder.LastLevelFolderPath))
                    {
                        _kind = GroupKind.Folder;
                        _songsBound = true;
                        CoverSource = folder;
                        Title = folder.LastLevelFolderPath;
                        CoverGlyph = "\uE8B7";
                        CoverCornerRadius = new CornerRadius(10);
                        IsAlbumDetail = false;
                        BindSongs(AppViewModel.FolderSongs);
                        RefreshFolderTitles(folder);
                        return;
                    }
                    break;
            }

            _kind = GroupKind.None;
            _songsBound = false;
            Songs = [];
            CoverSource = null;
            Title = string.Empty;
            SecondTitle = string.Empty;
            ThirdTitle = string.Empty;
        }

        private void BindSongs(BulkObservableCollection<Music> source)
        {
            if (!ReferenceEquals(Songs, source))
            {
                Songs = source;
            }
            UpdateMusicListView();
        }

        private void RefreshTitlesOnly()
        {
            if (AppViewModel.CurrentAlbumObj is { } album && _kind == GroupKind.Album)
                RefreshAlbumTitles(album);
            else if (AppViewModel.CurrentArtistObj is { } artist && _kind == GroupKind.Artist)
                RefreshArtistTitles(artist);
            else if (AppViewModel.CurrentFolderObj is { } folder && _kind == GroupKind.Folder)
                RefreshFolderTitles(folder);
        }

        private static readonly string NumberOfSongsText = ToolUtils.GetString("NumberOfSongs");
        private static readonly string NumberOfAlbumsText = ToolUtils.GetString("NumberOfAlbums");
        private static readonly string NumberOfArtistsText = ToolUtils.GetString("NumberOfArtists");
        private static readonly string ArtistText = ToolUtils.GetString("Artist");
        private static readonly string FolderText = ToolUtils.GetString("Folder");

        private void RefreshAlbumTitles(Music album)
        {
            int count = 0;
            _seenAuthors.Clear();
            _authorsList.Clear();
            var srcSpan = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(AppViewModel.SongsSource);
            for (int i = 0; i < srcSpan.Length; i++)
            {
                var music = srcSpan[i];
                if (music.Album != album.Album) continue;
                count++;
                if (!string.IsNullOrEmpty(music.Author) && _seenAuthors.Add(music.Author))
                {
                    _authorsList.Add(music.Author);
                }
            }
            SecondTitle = string.Join(" · ", _authorsList);
            ThirdTitle = album.Year != 0
                ? $"{album.Year} · {count} {NumberOfSongsText}"
                : $"{count} {NumberOfSongsText}";
        }

        private void RefreshArtistTitles(Music artist)
        {
            int count = 0;
            int albums = 0;
            _seenAlbums.Clear();
            var srcSpan = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(AppViewModel.SongsSource);
            for (int i = 0; i < srcSpan.Length; i++)
            {
                var music = srcSpan[i];
                if (!ArtistHelper.IsMusicByArtist(music, artist.Author)) continue;
                count++;
                if (!string.IsNullOrEmpty(music.Album) && _seenAlbums.Add(music.Album))
                {
                    albums++;
                }
            }
            SecondTitle = $"{count} {NumberOfSongsText} · {albums} {NumberOfAlbumsText}";
            ThirdTitle = ArtistText;
        }

        private void RefreshFolderTitles(Music folder)
        {
            int count = 0;
            int albums = 0;
            int authors = 0;
            _seenAlbums.Clear();
            _seenAuthors.Clear();
            var currentFolder = folder.LastLevelFolderPath;
            var srcSpan = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(AppViewModel.SongsSource);
            for (int i = 0; i < srcSpan.Length; i++)
            {
                var music = srcSpan[i];
                if (music.LastLevelFolderPath != currentFolder) continue;
                count++;
                if (!string.IsNullOrEmpty(music.Album) && _seenAlbums.Add(music.Album)) albums++;
                if (!string.IsNullOrEmpty(music.Author) && _seenAuthors.Add(music.Author)) authors++;
            }
            SecondTitle = $"{count} {NumberOfSongsText} · {albums} {NumberOfAlbumsText} · {authors} {NumberOfArtistsText}";
            ThirdTitle = FolderText;
        }

        public void UpdateMusicListView()
        {
            try
            {
                if (AppViewModel.CurrentPlayingList is not null && AppViewModel.CurrentPlayingMusic is not null &&
                    AppViewModel.TryFindById(AppViewModel.CurrentPlayingMusic.Id, out var m) && m is not null)
                {
                    SelectedMusic = m;
                    _view?.OnScrollToMusic(m);
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
                var list = _kind switch
                {
                    GroupKind.Album => AppViewModel.AlbumSongs,
                    GroupKind.Artist => AppViewModel.ArtistSongs,
                    GroupKind.Folder => AppViewModel.FolderSongs,
                    _ => null
                };
                if (list is null) return;
                AppViewModel.SequentialPlayingList = new BulkObservableCollection<Music>(list);
                await MusicBrowseViewModel.PlayMusic(music: SelectedMusic, IsChangeList: true);
            }
        }

        private async Task OnPlayAllAsync()
        {
            if (MusicBrowseViewModel is null) return;
            var list = _kind switch
            {
                GroupKind.Album => AppViewModel.AlbumSongs,
                GroupKind.Artist => AppViewModel.ArtistSongs,
                GroupKind.Folder => AppViewModel.FolderSongs,
                _ => null
            };
            if (list is null || list.Count == 0) return;
            AppViewModel.SequentialPlayingList = new BulkObservableCollection<Music>(list);
            await MusicBrowseViewModel.PlayMusic(music: list[0], IsChangeList: true);
        }

        private void OnAlbumInfoChanged()
        {
            if (AppViewModel.CurrentAlbumObj is not null)
            {
                var window = new AlbumDetailWindow(AppViewModel.CurrentAlbumObj);
                window.Activate();
            }
        }

        private async Task OnPlayFromSelectionAsync()
        {
            if (MusicBrowseViewModel is null) return;
            if (SelectedMusics.Count == 1)
            {
                var groupList = _kind switch
                {
                    GroupKind.Album => AppViewModel.AlbumSongs,
                    GroupKind.Artist => AppViewModel.ArtistSongs,
                    GroupKind.Folder => AppViewModel.FolderSongs,
                    _ => (System.Collections.Generic.IEnumerable<Music>)(SelectedMusic is null ? [] : [SelectedMusic])
                };
                AppViewModel.SequentialPlayingList = new BulkObservableCollection<Music>(groupList);
                await MusicBrowseViewModel.PlayMusic(music: SelectedMusics[0], IsChangeList: true);
            }
            else if (SelectedMusics.Count > 1)
            {
                AppViewModel.SequentialPlayingList = new BulkObservableCollection<Music>(SelectedMusics);
                await MusicBrowseViewModel.PlayMusic(music: SelectedMusics[0], IsChangeList: true);
            }
            else if (SelectedMusic is not null)
            {
                var groupList = _kind switch
                {
                    GroupKind.Album => AppViewModel.AlbumSongs,
                    GroupKind.Artist => AppViewModel.ArtistSongs,
                    GroupKind.Folder => AppViewModel.FolderSongs,
                    _ => (System.Collections.Generic.IEnumerable<Music>)([SelectedMusic])
                };
                AppViewModel.SequentialPlayingList = new BulkObservableCollection<Music>(groupList);
                await MusicBrowseViewModel.PlayMusic(music: SelectedMusic, IsChangeList: true);
            }
        }

        private void OnAddMusicToCurrentPlayListFromSelection()
        {
            if (SelectedMusics.Count > 0)
            {
                AppViewModel.AddMusicRangeToCurrentPlayList(SelectedMusics);
            }
            else if (SelectedMusic is not null)
            {
                MusicCommands.AddToPlayListCommand.Execute(SelectedMusic);
            }
        }

        private void OnUpdateFavourite(Music? m)
        {
            if (m is null) return;
            MusicCommands.UpdateFavouriteCommand.Execute(m);
        }

        private void OnAddToFavourFromSelection()
        {
            var targets = SelectedMusics.Count > 0 ? SelectedMusics : (SelectedMusic is null ? [] : [SelectedMusic]);
            if (targets.Count == 0) return;
            if (targets.Count == 1)
            {
                MusicCommands.AddToFavouriteCommand.Execute(targets[0]);
                return;
            }
            foreach (var m in targets)
            {
                if (!m.IsFavorite) MusicCommands.AddToFavouriteCommand.Execute(m);
            }
        }

        private void OnAddToPlayList(int playListId)
        {
            var targets = SelectedMusics.Count > 1 ? SelectedMusics : (SelectedMusic is null ? [] : [SelectedMusic]);
            if (targets.Count == 0) return;
            _ = _db.AddMusicListToPlayList(targets, playListId);
        }

        private async Task OnDeleteAsync()
        {
            if (!await IsDeleteFromDisk()) return;
            if (SelectedMusics.Count > 1)
            {
                foreach (var item in SelectedMusics)
                {
                    if (ToolUtils.DeleteFileFromDisk(item.Path))
                    {
                        AppViewModel.RemoveFromSongsSource(item);
                    }
                }
            }
            else if (SelectedMusic is not null)
            {
                if (ToolUtils.DeleteFileFromDisk(SelectedMusic.Path))
                {
                    AppViewModel.RemoveFromSongsSource(SelectedMusic);
                }
            }
        }

        private async Task OnConvertAudioAsync(string tag)
        {
            if (MusicBrowseViewModel is null) return;
            _ = MusicBrowseViewModel.ConvertAudio_Click(SelectedMusics, tag);
        }

        private void OnOpenInExplorer()
        {
            if (SelectedMusic is null) return;
            var filePath = SelectedMusic.Path;
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
            if (SelectedMusics.Count > 0)
            {
                var window = new MusicDetailsWindow(SelectedMusics[0]);
                window.Activate();
            }
        }

        private async Task OnReGetLyricsAsync()
        {
            await AppViewModel.ReGetLyrics(SelectedMusics, SelectedMusic);
        }

        private async Task OnTransmitFileToUsbAsync(UsbSendTarget? target)
        {
            if (target?.Device is null) return;
            await AppViewModel.TransmitFileToUsb(SelectedMusics, target.Device, target.Format, target.BitrateKbps);
        }

        private async Task<bool> IsDeleteFromDisk()
        {
            if (MusicBrowseViewModel is null) return false;
            return await MusicBrowseViewModel.AreUSureDeleteFromDisk();
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
                Children = ToolUtils.BuildConvertMenuChildren(ConvertAudioCommand)
            });
            MenuOptions.Add(new() { Title = ToolUtils.GetString("FlyoutAddToCurrentPlayList"), Tag = "AddMusicToCurrentPlayList", Command = AddMusicToCurrentPlayListCommand });
            MenuOptions.Add(new() { Title = ToolUtils.GetString("ReGetLyrics"), Tag = "ReGetLyrics", Command = ReGetLyricsCommand });
            MenuOptions.Add(new() { Title = ToolUtils.GetString("FlyoutOpenLocationItem"), Tag = "OpenInExplorer", Command = OpenInExplorerCommand });
            MenuOptions.Add(new() { Title = ToolUtils.GetString("FlyoutPropertiesItem"), Tag = "MusicDetail", Command = MusicDetailCommand });
            MenuOptions.Add(new() { Title = ToolUtils.GetString("FlyoutDeleteItem"), Tag = "DeleteMenuItem", Command = DeleteMenuItemCommand });
        }

        public void UpDateUsbDeviceMenuflyout()
            => ToolUtils.UpdateUsbSendMenu(MenuOptions, TransmitFileToUsbCommand);

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
            lock (_instancesLock)
            {
                _instances.Remove(this);
            }
            AppViewModel.PropertyChanged -= OnAppVmPropertyChanged;
            AppViewModel.AlbumSongs.CollectionChanged -= OnSongsCollectionChanged;
            AppViewModel.ArtistSongs.CollectionChanged -= OnSongsCollectionChanged;
            AppViewModel.FolderSongs.CollectionChanged -= OnSongsCollectionChanged;
        }
    }
}
