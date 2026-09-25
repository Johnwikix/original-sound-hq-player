using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.WinUI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Devices.Portable;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Reader;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.View;
using WinUIMusicPlayer.View.SubView;
using ZLinq;
using static WinUIMusicPlayer.Utils.ToolUtils;

namespace WinUIMusicPlayer.ViewModel
{
    public partial class MusicBrowseViewModel : ObservableObject, IDisposable
    {
        public WebDavSourcesViewModel RemoteSources => App.Services.GetRequiredService<WebDavSourcesViewModel>();

        public SelectorBarItem SelectedPage
        {
            get => field;
            set
            {
                if (value is null) return;
                if (SetProperty(ref field, value))
                {
                    OnSelectionChanged();
                }
            }
        }
        public int PreviousSelectedIndex { get; set; } = 0;
        public BassPlayerCommandService MusicPlaybackService { get; set; }
        private SystemMediaControlsService SystemMediaControlsService { get; set; }
        private MusicBrowsePage MusicBrowsePage { get; set; }
        private MainPage MainPage { get; set; }
        private readonly PlaybackCoordinator _coordinator;
        private readonly CoverPresentationService _covers;
        private bool _disposed;
        private ILogger<MusicBrowseViewModel> _logger;
        private const long MemoryTrimThreshold = 400L * 1024 * 1024;

        private static readonly DispatcherQueueHandler _clearUILyrics = static () =>
            App.Services.GetRequiredService<MusicBrowseViewModel>().AppViewModel.UILyrics = [];
        public PlaybackCommands Playback { get; }
        public AppViewModel AppViewModel { get; }
        public WinUIMusicPlayer.State.AppState State => AppViewModel.State;
        private MusicDatabaseService _musicDatabaseService { get; }
        public MusicBrowseViewModel(BassPlayerCommandService bassPlayerCommand, SystemMediaControlsService systemMediaControlsService, AppViewModel appViewModel, MusicDatabaseService musicDatabaseService, UsbDeviceService usbDeviceService, ILogger<MusicBrowseViewModel> logger, PlaybackCommands playback, PlaybackCoordinator coordinator, CoverPresentationService covers)
        {
            _covers = covers;
            Playback = playback;
            _coordinator = coordinator;
            coordinator.TrackStarted += OnTrackStarted;
            coordinator.SelectionChanged += OnPlaybackSelectionChanged;
            this.AppViewModel = appViewModel;
            _musicDatabaseService = musicDatabaseService;
            UsbDeviceService = usbDeviceService;
            MusicPlaybackService = bassPlayerCommand;
            _logger = logger;
            SystemMediaControlsService = systemMediaControlsService;
            WireAddFolderScanGuard();
        }

        public Task ConvertAudio_Click(IEnumerable<Music> music, string? tag) =>
            App.Services.GetRequiredService<AudioConversionViewModel>().ConvertAsync(music, tag);

        public void UpdateDisplayTexts()
        {
            foreach (var option in AppViewModel.SortOptions)
            {
                option.DisplayText = ToolUtils.GetString(option.UidKey);
            }
        }

        /// <summary>USB 设备生命周期由 UsbDeviceService 收敛管理（含插入后自动选中第一项）。</summary>
        public UsbDeviceService UsbDeviceService { get; }

        public async Task LoadPlayStateToMusicBrowsePage()
        {
            if (AppViewModel.CurrentPlayingMusic is not null)
            {
                _ = UpdatePlayBar(AppViewModel.CurrentPlayingMusic);
                App.MainWindow.DispatcherQueue.TryEnqueue(_clearUILyrics);
                AppViewModel.LoadLyricsToUI(AppViewModel.CurrentPlayingMusic);
            }
        }

        public Task UpdatePlayBar(Music music, CancellationToken token = default) => _covers.UpdatePlayBar(music, token);
        public void ThemeChangedUpdateCover() => _covers.ThemeChangedUpdateCover();

        public void SetMusicBrowsePage(MusicBrowsePage musicBrowsePage)
        {
            MusicBrowsePage = musicBrowsePage;
        }

        public void SetMainPage(MainPage mainPage)
        {
            MainPage = mainPage;
        }

        // 空音乐库占位交互：复用 AddFolderViewModel 的渐进上架流程——文件夹行先出现、
        // 歌曲按批次出现在当前页（首批到达时占位自动让位），不再借用顶部 ProgressRing；
        // 该环只归 USB 传输与文件监视自动重扫使用。扫描进行中禁用入口。
        [RelayCommand(CanExecute = nameof(CanStartFolderScan))]
        private async Task EmptyAddFolderAsync()
        {
            await App.Services.GetRequiredService<AddFolderViewModel>().AddFolderWithLoadingAsync();
        }

        [RelayCommand(CanExecute = nameof(CanStartFolderScan))]
        private async Task DropFoldersFromEmptyAsync(IReadOnlyList<Windows.Storage.IStorageItem> folders)
        {
            if (folders is null || folders.Count == 0) return;
            await App.Services.GetRequiredService<AddFolderViewModel>().DropFoldersAsync(folders);
        }

        private bool CanStartFolderScan => !App.Services.GetRequiredService<AddFolderViewModel>().IsScanning;

        // 与 AddFolderPage 添加按钮同步：IsScanning 翻转时刷新占位按钮可用态
        private AddFolderViewModel? _addFolderVm;
        private void WireAddFolderScanGuard()
        {
            _addFolderVm = App.Services.GetRequiredService<AddFolderViewModel>();
            _addFolderVm.PropertyChanged += FolderScanChanged;
        }
        private void FolderScanChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(AddFolderViewModel.IsScanning)) return;
            EmptyAddFolderCommand.NotifyCanExecuteChanged();
            DropFoldersFromEmptyCommand.NotifyCanExecuteChanged();
        }
        public void Dispose()
        {
            _disposed = true;
            if (_addFolderVm is not null) _addFolderVm.PropertyChanged -= FolderScanChanged;
            _coordinator.TrackStarted -= OnTrackStarted;
            _coordinator.SelectionChanged -= OnPlaybackSelectionChanged;
        }

        [RelayCommand]
        public void OnPlayModeChanged()
        {
            switch (AppViewModel.CurrentPlayMode)
            {
                case PlayMode.SingleLoop:
                    AppViewModel.CurrentPlayMode = PlayMode.ListLoop;
                    break;
                case PlayMode.ListLoop:
                    AppViewModel.CurrentPlayMode = PlayMode.RandomLoop;
                    break;
                case PlayMode.RandomLoop:
                    AppViewModel.CurrentPlayMode = PlayMode.RepeatOff;
                    break;
                case PlayMode.RepeatOff:
                    AppViewModel.CurrentPlayMode = PlayMode.SingleLoop;
                    break;
            }
            MusicPlaybackService.UpdateSettings();
        }
        [RelayCommand]
        public void OnPlayButtonChanged()
        {
            PlayButton_Click();
        }

        public void PlayButton_Click()
        {
            Playback.ToggleCommand.Execute(null);
        }

        [RelayCommand]
        public void OnNextMusicButtonChanged()
        {
            NextMusicButton_Click();
        }

        [RelayCommand]
        public void OnLastMusicButtonChanged()
        {
            LastMusicButton_Click();
        }

        public void NextMusicButton_Click()
        {
            Playback.NextCommand.Execute(null);
        }

        public void LastMusicButton_Click()
        {
            PlayLastTrack();
        }

        private void PlayLastTrack() => Playback.PreviousCommand.Execute(null);

        [RelayCommand]
        private void OnAlbumCoverImage()
        {
            (MainPage ?? App.Services.GetRequiredService<MainPage>()).NavigateToPlayingDetailPage();
        }
        [RelayCommand]
        private void OnEqualizerButton()
        {
            var mainPage = MainPage ?? App.Services.GetRequiredService<MainPage>();
            _ = mainPage.EqualizerDialog.ShowThemedAsync(mainPage.XamlRoot);
        }        
        

        private void OnSelectionChanged()
        {
            int currentSelectedIndex = GetSelectorBarItemIndex(SelectedPage);

            // 各 tab 的 detail 状态(CurrentXxxObj)在切换时保留,切回时由
            // ReceiveNavigation/RefreshFromAppState 按 PageType 恢复;退出详情
            // 由各页返回按钮(CollapseDetail)显式清空。

            AppData.CurrentPage = typeof(SongListPage);
            switch (SelectedPage.Name)
            {
                case "Song":
                    AppViewModel.PageType = "song";
                    AppData.CurrentPage = typeof(SongListPage);
                    break;
                case "Album":
                    AppData.CurrentPage = typeof(AlbumPage);
                    AppViewModel.PageType = AppViewModel.CurrentAlbumObj is { } a && !string.IsNullOrEmpty(a.Album)
                        ? "album" : "albumBrowse";
                    break;
                case "Artist":
                    AppData.CurrentPage = typeof(ArtistPage);
                    AppViewModel.PageType = AppViewModel.CurrentArtistObj is { } ar && !string.IsNullOrEmpty(ar.Author)
                        ? "artist" : "artistBrowse";
                    break;
                case "Folder":
                    AppData.CurrentPage = typeof(FolderBrowsePage);
                    AppViewModel.PageType = AppViewModel.CurrentFolderObj is { } f && !string.IsNullOrEmpty(f.LastLevelFolderPath)
                        ? "folder" : "folderBrowse";
                    break;
                case "Favourite":
                    AppViewModel.PageType = "favourite";
                    AppData.CurrentPage = typeof(FavouritePlayListPage);
                    break;
            }
            AppViewModel.RefreshDataSource();
            var slideNavigationTransitionEffect = currentSelectedIndex - PreviousSelectedIndex > 0 ? SlideNavigationTransitionEffect.FromRight : SlideNavigationTransitionEffect.FromLeft;
            MusicBrowsePage?.NavigatePage(AppData.CurrentPage, null, new SlideNavigationTransitionInfo() { Effect = slideNavigationTransitionEffect });
            PreviousSelectedIndex = currentSelectedIndex;
        }

        public Task PlayMusic(Music music, TimeSpan currentPos = new TimeSpan(), bool isSettingChanged = false, bool IsChangeList = false)
            => _coordinator.PlayAsync(music);

        private void OnPlaybackSelectionChanged()
        {
            if (_disposed) return;
            MusicBrowsePage?.UpdateViewList(refreshDetails: false);
            MainPage?.UpdateCurrentPlayList();
        }

        private void OnTrackStarted(Music music, CancellationToken token)
        {
            if (_disposed) return;
            MusicBrowsePage?.UpdateViewList();
            _ = UpdatePlayBar(music, token);
            MainPage?.UpdateCurrentPlayList();
            TrimMemory();
        }

        /// <summary>
        /// 外部文件匹配库内条目后的播放入口：播放队列替换为同文件夹曲目（文件夹语义与
        /// FolderViewModel.Play 一致，按文件夹名聚合、沿用库内顺序），从匹配曲目开始；
        /// 匹配条目尚未同步进 SongsSource（如同文件夹刚扫描入库）时不替换队列，仅替换当前曲。
        /// </summary>
        public void PlayMusicWithFolderQueue(Music music)
        {
            if (!AppViewModel.CanStartPlayback) return;
            if (string.IsNullOrEmpty(music.LastLevelFolderPath))
            {
                _ = PlayMusic(music, IsChangeList: true);
                return;
            }

            var source = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(AppViewModel.SongsSource);
            List<Music> folderSongs = [];
            for (int i = 0; i < source.Length; i++)
            {
                var m = source[i];
                if (m.LastLevelFolderPath is not null
                    && m.LastLevelFolderPath.Equals(music.LastLevelFolderPath, StringComparison.OrdinalIgnoreCase))
                {
                    folderSongs.Add(m);
                }
            }
            if (folderSongs.Count == 0)
            {
                _ = PlayMusic(music, IsChangeList: true);
                return;
            }

            // SequentialPlayingList 是唯一状态源：赋值后 CurrentPlayingList 自动跟随（随机模式自动洗牌）。
            AppViewModel.SequentialPlayingList = new BulkObservableCollection<Music>(folderSongs);
            _ = PlayMusic(music, IsChangeList: true);
        }

        private void TrimMemory() {
            try
            {
                if (!AppViewModel.IsTrimAfterPlaybackEnabled) return;
                if (WorkingSetCompressor.GetPrivateWorkingSet() > MemoryTrimThreshold)
                {
                    _ = WorkingSetCompressor.TrimSelfAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"内存清理失败: {ex.Message}");
            }
        }


        [RelayCommand]
        private void OnArtistButton(string? artist)
        {
            if (string.IsNullOrWhiteSpace(artist)) return;
            SelectBarArtist(artist);
        }
        [RelayCommand]
        private void OnAlbumButton(string? album)
        {
            if (string.IsNullOrWhiteSpace(album)) return;
            SelectBarAlbum(album);
        }

        public void SelectBarArtist(string artist) => _ = SelectBarArtistCore(artist);

        private async Task SelectBarArtistCore(string artist)
        {
            if (string.IsNullOrWhiteSpace(artist)) return;
            var names = ArtistHelper.GetArtistNames(artist);
            if (names.Length > 1)
            {
                var mainPage = MainPage ?? App.Services.GetRequiredService<MainPage>();
                var xamlRoot = mainPage.XamlRoot ?? MusicBrowsePage?.XamlRoot;
                if (xamlRoot is null) return;
                artist = await DialogHelper.ShowArtistPickerAsync(xamlRoot, names) ?? string.Empty;
                if (artist.Length == 0) return;
            }
            MainPage?.NavigateToMusicBrowsePage();
            MusicBrowsePage.SelectBarArtist(artist);
        }

        public void SelectBarAlbum(string Album)
        {
            MainPage?.NavigateToMusicBrowsePage();
            MusicBrowsePage.SelectBarAlbum(Album);
        }

        public async Task<bool> AreUSureDeleteFromDisk()
        {
            return await MusicBrowsePage.AreUSureDeleteFromDisk();
        }

        public void NavigatePage(Type pageType, object? parameter = null, NavigationTransitionInfo? navigationTransitionInfo = null)
        {
            MusicBrowsePage.NavigatePage(pageType, parameter, navigationTransitionInfo);
        }

        public void BackButton()
        {
            MusicBrowsePage?.BackButton();
        }

        public void UpdateViewList()
        {
            MusicBrowsePage?.UpdateViewList();
        }

        private int GetSelectorBarItemIndex(SelectorBarItem item)
        {
            if (item is null) return -1;
            return item.Name switch
            {
                "Song" => 0,
                "Album" => 1,
                "Artist" => 2,
                "Folder" => 3,
                "Favourite" => 4,
                _ => -1
            };
        }
    }
}
