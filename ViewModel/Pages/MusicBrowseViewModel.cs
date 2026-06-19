using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.WinUI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Windowing;
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
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Reader;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.View;
using WinUIMusicPlayer.View.SubView;
using ZLinq;
using static WinUIMusicPlayer.Utils.ToolUtils;

namespace WinUIMusicPlayer.ViewModel
{
    public partial class MusicBrowseViewModel : ObservableObject
    {

        public SelectorBarItem SelectedPage
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    OnSelectionChanged();
                }
            }
        }
        private ProgressDialog ProgressDialog { get; set; }
        private int ProgressBarValue { get; set; } = 0;
        private bool IsMutiFile { get; set; } = false;
        private AudioConverterService ConverterService { get; set; }
        public int PreviousSelectedIndex { get; set; } = 0;
        public BassPlayerCommandService MusicPlaybackService { get; set; }
        private SystemMediaControlsService SystemMediaControlsService { get; set; }
        private MusicBrowsePage MusicBrowsePage { get; set; }
        private MainPage MainPage { get; set; }
        private DeviceWatcher DeviceWatcher { get; set; }
        private List<FileSystemWatcher> Watchers { get; set; } = [];
        private readonly SemaphoreSlim scanSemaphore = new(1, 1);
        private CancellationTokenSource? _musicUpdateCts;
        private CancellationTokenSource _scanCts;
        private readonly Lock _scanCtsLock = new();
        private ILogger<MusicBrowseViewModel> _logger;
        public AppViewModel AppViewModel { get; }
        private MusicDatabaseService _musicDatabaseService { get; }
        public MusicBrowseViewModel(BassPlayerCommandService bassPlayerCommand, SystemMediaControlsService systemMediaControlsService, AppViewModel appViewModel, MusicDatabaseService musicDatabaseService, AudioConverterService converterService, ILogger<MusicBrowseViewModel> logger)
        {
            this.AppViewModel = appViewModel;
            _musicDatabaseService = musicDatabaseService;
            ConverterService = converterService;
            MusicPlaybackService = bassPlayerCommand;
            _logger = logger;
            ProgressDialog = new ProgressDialog(ToolUtils.GetString("Converting"));
            ProgressDialog.Title = ToolUtils.GetString("Processing");
            ConverterService.updateProgress += OnConverterProgressUpdated;
            SystemMediaControlsService = systemMediaControlsService;
            InitializeSystemMediaControls();
            AppSettings.OutputSettingsChanged += AppSettings_OutputSettingsChanged;
            AppSettings.OutputSettingsUpdated += AppSettings_OutputSettingsUpdated;
            AppSettings.EqUpdated += AppSettings_OnEqUpdated;
            if (AppViewModel.IsFolderWatchEnabled)
            {
                StartWatchingFileFolder();
            }
            StartWatchingUsbStorageDevices();
        }

        private void OnConverterProgressUpdated(object sender, double progress)
        {
            if (ProgressDialog is not null)
            {
                if (ProgressBarValue < (int)progress)
                {
                    ProgressBarValue = (int)progress;
                }
                if (IsMutiFile)
                {
                    if (ProgressBarValue < 100)
                    {
                        _ = ProgressDialog.UpdateProgress(ProgressBarValue);
                    }
                }
                else
                {
                    _ = ProgressDialog.UpdateProgress(ProgressBarValue);
                }
            }
        }

        public async Task ConvertAudio_Click(IEnumerable<Music> uniqueSelectedMusics, string? tag)
        {
            if (uniqueSelectedMusics is null || tag is null)
                return;

            ProgressBarValue = 0;
            var musicList = uniqueSelectedMusics.AsValueEnumerable().ToList();
            IsMutiFile = musicList.Count > 1;
            if (IsMutiFile)
            {
                await ConvertMultipleFiles(musicList, tag);
            }
            else
            {
                await ConvertSingleFile(musicList.AsValueEnumerable().FirstOrDefault(), tag);
            }
        }

        private async Task ConvertMultipleFiles(List<Music> musics, string targetFormat)
        {
            await ProgressDialog.UpdateProgress(ProgressBarValue);
            _ = ProgressDialog.ShowThemedAsync(MusicBrowsePage.XamlRoot);

            foreach (Music music in musics)
            {
                await ConverterService.ConvertAudio2Wav(music, targetFormat);
            }
            _ = ProgressDialog.UpdateProgress(100);
        }

        private async Task ConvertSingleFile(Music? music, string targetFormat)
        {
            if (music is null)
                return;

            if (music.Extension.Equals(targetFormat, StringComparison.OrdinalIgnoreCase))
            {
                UpdateInfoBar(ToolUtils.GetString("InfoBarMessageConverter"));
                return;
            }

            _ = ProgressDialog.UpdateProgress(ProgressBarValue);
            _ = ConverterService.ConvertAudio2Wav(music, targetFormat);

            if (ProgressBarValue < 100)
            {
                _ = ProgressDialog.ShowThemedAsync(MusicBrowsePage.XamlRoot);
            }
        }

        private void AppSettings_OnEqUpdated(object? sender, EventArgs e)
        {
            MusicPlaybackService.EqUpdate();
        }

        private void AppSettings_OutputSettingsUpdated(object? sender, EventArgs e)
        {
            MusicPlaybackService.UpdateSettings();
        }



        private void AppSettings_OutputSettingsChanged(object? sender, EventArgs e)
        {
            MusicPlaybackService.ChangingSetting();
        }

        public void UpdateDisplayTexts()
        {
            foreach (var option in AppViewModel.SortOptions)
            {
                option.DisplayText = ToolUtils.GetString(option.UidKey);
            }
        }

        private void StartWatchingUsbStorageDevices()
        {
            try
            {
                // 定义设备选择器以筛选 USB 存储设备
                string deviceSelector = StorageDevice.GetDeviceSelector();
                // 创建设备监视器
                DeviceWatcher = DeviceInformation.CreateWatcher(deviceSelector);
                // 注册设备添加、移除和枚举完成事件
                DeviceWatcher.Added += DeviceWatcher_Added;
                DeviceWatcher.Removed += DeviceWatcher_Removed;
                DeviceWatcher.EnumerationCompleted += DeviceWatcher_EnumerationCompleted;
                // 启动设备监视器
                DeviceWatcher.Start();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"启动设备监视器失败:{ex.Message}");
            }
        }

        private async void DeviceWatcher_Added(DeviceWatcher sender, DeviceInformation args)
        {
            // 当 USB 存储设备插入时触发            
            Task.Delay(1500).Wait(); // 等待设备稳定
            await ReadUsbDevice();
        }

        private async void DeviceWatcher_Removed(DeviceWatcher sender, DeviceInformationUpdate args)
        {
            // 当 USB 存储设备移除时触发            
            await ReadUsbDevice();
        }

        private void DeviceWatcher_EnumerationCompleted(DeviceWatcher sender, object args)
        {
            // 设备枚举完成时触发
        }

        public void UpdateInfoBar(string message)
        {
            App.MainWindow.DispatcherQueue.TryEnqueue(() =>
            {
                AppViewModel.InfoBarIsOpen = true;
                AppViewModel.InfoBarTitle = ToolUtils.GetString("InfoBarTitleConverter");
                AppViewModel.InfoBarMessage = message;
            });
        }

        private async Task ReadUsbDevice()
        {
            try
            {
                AppData.UsbStorageDevices = new ObservableCollection<UsbStorageDevice>(await UsbStorageDeviceReader.GetUsbStorageDevicesAsync());
                AppViewModel.UpDateUsbDeviceMenuflyout();
                App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                {
                    if (AppData.UsbStorageDevices.Count > 0)
                    {
                        AppViewModel.UsbDeviceVisibility = Visibility.Visible;
                        AppViewModel.UsbStorageDevices = AppData.UsbStorageDevices;
                        AppViewModel.UsbSelectedIndex = 0;
                    }
                    else
                    {
                        AppViewModel.UsbSelectedIndex = -1;
                        AppViewModel.UsbDeviceVisibility = Visibility.Collapsed;
                        AppViewModel.UsbStorageDevices = null;
                        AppData.MusicOnUsbDevice.Clear();
                        ClearAllUsbStatus();
                    }
                });
            }
            catch (Exception ex)
            {
                App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                {
                    AppViewModel.UsbDeviceVisibility = Visibility.Collapsed;
                });
                AppData.MusicOnUsbDevice.Clear();
                ClearAllUsbStatus();
                _logger.LogError(ex, $"读取USB设备失败:{ex.Message}");
            }
        }

        public async void UsbDeviceComboxSelectionChanged(UsbStorageDevice usbStorageDevice)
        {
            AppData.UsbStorageDevice = usbStorageDevice;
            List<UsbDeviceMusic> usbDeviceMusics = await _musicDatabaseService.GetUsbDeviceMusics(usbStorageDevice.UniqueId);
            if (usbDeviceMusics is not null && usbDeviceMusics.Count > 0)
            {
                // 检查是否需要重新扫描
                DateTime startTime = DateTime.Now;
                UsbDeviceSubFolderRescan usbDeviceSubFolderRescan = new UsbDeviceSubFolderRescan();
                await usbDeviceSubFolderRescan.UsbDeviceSubFolderAutoScan(usbDeviceMusics, usbStorageDevice.Path, usbStorageDevice.UniqueId);
                AppData.MusicOnUsbDevice = await _musicDatabaseService.GetUsbDeviceMusics(usbStorageDevice.UniqueId);
            }
            else
            {
                // 读取USB设备中的音乐文件
                string folderPath = Path.Combine(usbStorageDevice.Path, "MUSIC");
                if (Directory.Exists(folderPath))
                {
                    AppData.MusicOnUsbDevice = await _musicDatabaseService.RescanUsbDeviceFolderByPath(usbDeviceMusics, usbStorageDevice.UniqueId, folderPath, false);
                }
                else
                {
                    App.Services.GetRequiredService<NotificationService>().SendNotification(ToolUtils.GetString("Error"), ToolUtils.GetString("NoMusicInUSBDevice"));
                }
            }
            ToolUtils.RefreshAllUsbStatus();
        }

        private async void StartWatchingFileFolder()
        {
            try
            {
                List<Folder> folders = await _musicDatabaseService.GetFolders();
                foreach (var folder in folders)
                {
                    if (!string.IsNullOrEmpty(folder.Path))
                    {
                        var watcher = new FileSystemWatcher(folder.Path);
                        watcher.IncludeSubdirectories = true;
                        watcher.NotifyFilter = NotifyFilters.FileName |
                            NotifyFilters.DirectoryName |
                            NotifyFilters.LastWrite;

                        // 订阅事件
                        watcher.Changed += OnFileChanged;
                        watcher.Deleted += OnFileChanged;

                        // 开始监听
                        watcher.EnableRaisingEvents = true;

                        Watchers.Add(watcher);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"启动文件夹监视失败:{ex.Message}");
            }
        }

        public async void OnFileChanged(object sender, FileSystemEventArgs e)
        {
            if (!AppViewModel.IsFolderWatchEnabled) return;

            // 取消上一次未执行的扫描，重新计时
            CancellationTokenSource cts;
            lock (_scanCtsLock)
            {
                _scanCts?.Cancel();
                _scanCts?.Dispose();
                _scanCts = new CancellationTokenSource();
                cts = _scanCts;
            }

            try
            {
                await Task.Delay(1000, cts.Token); // 防抖：等待 1000ms
            }
            catch (OperationCanceledException)
            {
                return; // 被新事件取消，退出
            }

            // 防抖通过，尝试获取信号量
            if (!await scanSemaphore.WaitAsync(0)) return;

            try
            {
                App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                    AppViewModel.ProcessRingVisibility = Visibility.Visible);

                await AutoRescanService.AutoScan(cts.Token);

                App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                    AppViewModel.ProcessRingVisibility = Visibility.Collapsed);
            }
            catch (OperationCanceledException)
            {
                App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                    AppViewModel.ProcessRingVisibility = Visibility.Collapsed);
            }
            finally
            {
                scanSemaphore.Release();
            }
        }

        private void InitializeSystemMediaControls()
        {

            // 订阅事件
            SystemMediaControlsService.PlayRequested += (s, e) =>
            {
                App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                {
                    PlayButton_Click();
                });
            };

            SystemMediaControlsService.PauseRequested += (s, e) =>
            {
                App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                {
                    PlayButton_Click();
                });
            };

            SystemMediaControlsService.NextTrackRequested += (s, e) =>
            {
                App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                {
                    NextMusicButton_Click();
                });
            };

            SystemMediaControlsService.PreviousTrackRequested += (s, e) =>
            {
                App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                {
                    LastMusicButton_Click();
                });
            };
        }

        //public void SetMusicService(BassPlayerCommandService musicPlaybackService)
        //{
        //    MusicPlaybackService = musicPlaybackService;
        //}

        public async Task LoadPlayStateToMusicBrowsePage()
        {
            if (AppViewModel.CurrentPlayingMusic is not null)
            {
                _ = UpdatePlayBar(AppViewModel.CurrentPlayingMusic);
                App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                {
                    AppViewModel.UILyrics = [];
                });
                AppViewModel.LoadLyricsToUI(AppViewModel.CurrentPlayingMusic);
            }
        }

        public async Task UpdatePlayBar(Music music, CancellationToken token = default)
        {
            try
            {
                byte[] picData = await Task.Run(async () =>
                {
                    token.ThrowIfCancellationRequested();
                    return await GetRawImage(music);
                }, token);
                if (token.IsCancellationRequested) return;

                AnimatedWin2dControls.Impressionist.PaletteResult? palette = null;
                if (!string.IsNullOrEmpty(music.ImageHash))
                {
                    string thumbPath = CoverLoadQueue.GetThumbCachePath(music.ImageHash, CoverLoadQueue.CoverSize);
                    palette = await AnimatedWin2dControls.Impressionist.PaletteExtractor
                        .ExtractFromBmpCacheAsync(thumbPath, ct: token);
                }
                if (palette is null && picData.Length > 0)
                {
                    palette = await Task.Run(() =>
                        AnimatedWin2dControls.Impressionist.PaletteExtractor
                            .ExtractFromImageBytesAsync(picData, ct: token), token);
                }

                if (token.IsCancellationRequested) return;

                App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                {
                    if (!token.IsCancellationRequested)
                    {
                        AppViewModel.LyricPageBackgroundHash = music.ImageHash ?? "";
                        AppViewModel.LyricPagePalette = palette;
                        AppViewModel.MusicInfo = $"{music.Extension} {music.SampleRate}Hz {music.BitDepth}bit {music.BitRate}kbps";
                    }
                });

                // --- 阶段 D: 更新系统媒体控制 (SMTC) ---
                // 同样在后台运行，避免 SMTC 的 COM 组件调用阻塞 UI
                App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                {
                    if (token.IsCancellationRequested) return;

                    SystemMediaControlsService.UpdateSystemMediaControlsState();
                    SystemMediaControlsService.UpdateTimelineProperties(TimeSpan.Zero, music.Duration);
                    _ = SystemMediaControlsService.UpdateMediaInfo(
                        music.Title,
                        music.Author,
                        music.Album,
                        picData);
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"更新播放栏失败: {ex.Message}");
            }
        }

        public async void ThemeChangedUpdateCover()
        {
            if (AppViewModel.CurrentPlayingMusic is null) return;
            AppViewModel.UpdateCover();
        }
        public void SetMusicBrowsePage(MusicBrowsePage musicBrowsePage)
        {
            MusicBrowsePage = musicBrowsePage;
        }

        public void SetMainPage(MainPage mainPage)
        {
            MainPage = mainPage;
        }

        [RelayCommand]
        public void OnPlayModeChanged()
        {
            switch (AppViewModel.CurrentPlayMode)
            {
                case PlayMode.SingleLoop:
                    AppViewModel.CurrentPlayMode = PlayMode.ListLoop;
                    AppViewModel.PlayModeFlyoutText = ToolUtils.GetString("IconListLoop");
                    break;
                case PlayMode.ListLoop:
                    AppViewModel.CurrentPlayMode = PlayMode.RandomLoop;
                    AppViewModel.PlayModeFlyoutText = ToolUtils.GetString("IconRandomLoop");
                    break;
                case PlayMode.RandomLoop:
                    AppViewModel.CurrentPlayMode = PlayMode.RepeatOff;
                    AppViewModel.PlayModeFlyoutText = ToolUtils.GetString("IconSinglePlayback");
                    break;
                case PlayMode.RepeatOff:
                    AppViewModel.CurrentPlayMode = PlayMode.SingleLoop;
                    AppViewModel.PlayModeFlyoutText = ToolUtils.GetString("IconSingleTuneCirculation");
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
            MusicPlaybackService.PlayButton();
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
            MusicPlaybackService.PlayNextTrack();
        }

        public void LastMusicButton_Click()
        {
            PlayLastTrack();
        }

        private void PlayLastTrack()
        {
            int index = AppViewModel.CurrentPlayingList.AsValueEnumerable()
                        .Select((music, i) => new { Music = music, Index = i })
                        .FirstOrDefault(x => x.Music.Id == AppViewModel.CurrentPlayingMusic.Id)
                        ?.Index ?? -1;
            if (index > 0)
            {
                PlayMusic(AppViewModel.CurrentPlayingList[index - 1]).Wait();
            }
            else if (index == 0 && AppViewModel.CurrentPlayingList.Count > 1)
            {
                PlayMusic(AppViewModel.CurrentPlayingList[AppViewModel.CurrentPlayingList.Count - 1]).Wait();
            }
        }


        [RelayCommand]
        private void OnStopButtonChanged()
        {
            MusicPlaybackService.MusicEnd();
            AppViewModel.ProgressSlider = 0;
        }
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
        [RelayCommand]
        private void OnFastForwardButton()
        {
            SeekRelative(30_000);
        }
        [RelayCommand]
        private void OnFastBackwardButton()
        {
            SeekRelative(-10_000);
        }
        private void SeekRelative(long deltaMs)
        {
            var (curMs, totalMs) = AppViewModel.GetTimeProgressCache();
            long newPosMs = Math.Clamp(curMs + deltaMs, 0, totalMs);
            AppViewModel.IsManualSelect = true;
            AppViewModel.SetTimeProgressCache(newPosMs, totalMs);
            AppViewModel.ProgressSlider = newPosMs / 1000.0;
            MusicPlaybackService.ChangeWaveChannelTime(newPosMs);
            AppViewModel.IsManualSelect = false;
        }
        [RelayCommand]
        private void OnVolumeSliderIconButtonChanged()
        {
            AppViewModel.IsMuted = !AppViewModel.IsMuted;
            AppViewModel.Volume = AppViewModel.IsMuted ? 0 : AppViewModel.TempVolume;
        }

        [RelayCommand]
        private void OnVolumeUpChanged()
        {
            AppViewModel.AdjustVolume(1);
        }
        [RelayCommand]
        private void OnVolumeDownChanged()
        {
            AppViewModel.AdjustVolume(-1);
        }


        [RelayCommand]
        private void OnFullScreenButtonChanged()
        {
            if (App.MainWindow.AppWindow is not null)
            {
                if (AppViewModel.IsFullScreen)
                {
                    App.MainWindow.AppWindow.SetPresenter(AppWindowPresenterKind.Default);
                }
                else
                {
                    App.MainWindow.AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
                }
                AppViewModel.IsFullScreen = !AppViewModel.IsFullScreen;
            }

        }

        [RelayCommand]
        private void PlayDetailButtonVisibleChanged()
        {
            AppViewModel.IsPlayDetailButtonVisible = !AppViewModel.IsPlayDetailButtonVisible;
        }

        private void OnSelectionChanged()
        {
            int currentSelectedIndex = GetSelectorBarItemIndex(SelectedPage);

            // 清空非本 tab 的 detail 状态(SelectBarAlbum/Artist 等跨链已把
            // 对应的 CurrentXxxObj 设好,这里不清本 tab,跨链才不会失效)
            switch (SelectedPage.Name)
            {
                case "Song":
                case "Favourite":
                    AppViewModel.CurrentAlbumObj = null;
                    AppViewModel.CurrentArtistObj = null;
                    AppViewModel.CurrentFolderObj = null;
                    break;
                case "Album":
                    AppViewModel.CurrentArtistObj = null;
                    AppViewModel.CurrentFolderObj = null;
                    break;
                case "Artist":
                    AppViewModel.CurrentAlbumObj = null;
                    AppViewModel.CurrentFolderObj = null;
                    break;
                case "Folder":
                    AppViewModel.CurrentAlbumObj = null;
                    AppViewModel.CurrentArtistObj = null;
                    break;
            }

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
            MusicBrowsePage.NavigatePage(AppData.CurrentPage, null, new SlideNavigationTransitionInfo() { Effect = slideNavigationTransitionEffect });
            PreviousSelectedIndex = currentSelectedIndex;
        }

        public async Task PlayMusic(Music music, TimeSpan currentPos = new TimeSpan(), bool isSettingChanged = false, bool IsChangeList = false)
        {
            try
            {
                // 1. 立即取消上一次正在进行的 UI 更新任务（图片读取、网络请求等）
                _musicUpdateCts?.Cancel();
                _musicUpdateCts?.Dispose();
                _musicUpdateCts = new CancellationTokenSource();
                var token = _musicUpdateCts.Token;
                MusicPlaybackService.PlayMusic(music);
                await App.MainWindow.DispatcherQueue.EnqueueAsync(() =>
                {
                    AppViewModel.CurrentPlayingMusic = music;
                    AppViewModel.UILyrics = [];
                    MusicBrowsePage.UpdateViewList();
                });
                _ = UpdatePlayBar(music, token);
                AppViewModel.LoadLyricsToUI(music);
                MainPage?.UpdateCurrentPlayList();
                AppViewModel.UpdateProgressTimerUI();
                _ = _musicDatabaseService.SavePlayState(AppViewModel.SequentialPlayingList,
                        AppViewModel.CurrentPlayMode,
                        AppViewModel.CurrentPlayingMusic?.Id,
                        (float)(AppViewModel.Volume),
                        AppViewModel.SelectedSortOption.Tag.ToString());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"播放音乐失败: {ex.Message}");
            }
        }

        public void SelectBarArtist(string artist)
        {
            MusicBrowsePage.SelectBarArtist(artist);
        }

        public void SelectBarAlbum(string Album)
        {
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

        public void HideTransmission()
        {
            AppViewModel.ProcessRingVisibility = Visibility.Collapsed;
        }

        public void ShowTransmission()
        {
            AppViewModel.ProcessRingVisibility = Visibility.Visible;
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
