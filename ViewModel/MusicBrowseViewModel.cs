using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using System.Xml.Linq;
using Windows.Devices.Enumeration;
using Windows.Devices.Portable;
using Windows.Storage.Streams;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Reader;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.View;
using ZLinq;
using static WinUIMusicPlayer.Utils.ToolUtils;

namespace WinUIMusicPlayer.ViewModel
{
    public partial class MusicBrowseViewModel : ObservableObject
    {   
        private bool _isUserDraggingProgressSlider = false;
        public bool IsUserDraggingProgressSlider
        {
            get => _isUserDraggingProgressSlider;
            set
            {
                if (SetProperty(ref _isUserDraggingProgressSlider, value))
                {
                }
            }
        }

        private double _progressSlider = 0;
        public double ProgressSlider
        {
            get => _progressSlider;
            set
            {
                if (SetProperty(ref _progressSlider, value))
                {
                    HandleProgressSliderChange(value);

                }
            }
        }

        private async void HandleProgressSliderChange(double value)
        {
            if (IsMouseOverProgressBar)
            {
                if (!IsUserDraggingProgressSlider)
                {
                    double currentPlayPosition = await _musicPlaybackService.GetCurrentPosition();
                    if (Math.Abs(value - currentPlayPosition) > 2.0)
                    {
                        _ = Task.Run(() =>
                        {
                            isManualSelect = true;
                            _musicPlaybackService.ChangeWaveChannelTime(TimeSpan.FromSeconds(value));
                            isManualSelect = false;
                        });
                    }
                }
            }
        }        

        private SelectorBarItem _selectedPage;

        public SelectorBarItem SelectedPage
        {
            get => _selectedPage;
            set
            {
                if (SetProperty(ref _selectedPage, value))
                {
                    OnSelectionChanged();
                }
            }
        }

        private SortOption _selectedSortOption;
        public SortOption SelectedSortOption
        {
            get => _selectedSortOption;
            set
            {
                if (SetProperty(ref _selectedSortOption, value))
                {
                    OnSelectSortChanged();
                }
            }
        }

        private bool _isPlaying = false;
        public bool IsPlaying
        {
            get => _isPlaying;
            set
            {
                if (SetProperty(ref _isPlaying, value))
                {
                    _musicBrowsePage.BeginOrPauseLyricImgAnimation(value);
                }
            }
        }
        private bool _isMouseOverProgressBar = false;
        public bool IsMouseOverProgressBar
        {
            get => _isMouseOverProgressBar;
            set => SetProperty(ref _isMouseOverProgressBar, value);
        }
        
        private IEnumerable<SortOption> _allSortOptions = [
            new SortOption( "DefaultOrder", "SortOrderDefault"),
            new SortOption("A-Z", "SortOrderA_Z"),
            new SortOption("Artist", "SortOrderArtist"),
            new SortOption("Album", "SortOrderAlbum"),
            new SortOption("CreateTimeASC", "SortOrderCreateTimeASC"),
            new SortOption("CreateTimeDESC", "SortOrderCreateTimeDESC"),
            new SortOption("UpdateTimeASC", "SortOrderUpdateTimeASC"),
            new SortOption("UpdateTimeDESC", "SortOrderUpdateTimeDESC")
        ];
        private IEnumerable<SortOption> _albumSortOptions = [
            new SortOption( "DefaultOrder", "SortOrderDefault"),
            new SortOption("Artist", "SortOrderArtist")
        ];

        private ObservableCollection<SortOption> _sortOptions;
        public ObservableCollection<SortOption> SortOptions
        {
            get => _sortOptions;
            set => SetProperty(ref _sortOptions, value);
        }


        public System.Type currentPage = typeof(SongListPage);
        public Music CurrentAlbum;
        public Music CurrentArtist;
        public Music CurrentFolder;
        public PlayList currentPlayList;
        public string paramName = "defualt";
        public int previousSelectedIndex = 0;
        public int currentPlayListId;
        public BassPlayerCommandService _musicPlaybackService;
        private SystemMediaControlsService _systemMediaControlsService;
        private MusicBrowsePage _musicBrowsePage;
        private DeviceWatcher deviceWatcher;
        private List<FileSystemWatcher> watchers = [];
        private readonly SemaphoreSlim scanSemaphore = new(1, 1);
        private System.Timers.Timer progressTimer;
        private TimeSpan _totalTime;
        private TimeSpan _currentTime;
        public bool isManualSelect = false;
        private readonly StringBuilder _timeStringBuilder = new StringBuilder(16);
        public LyricsRefreshService LyricsRefreshService { get; set; }
        public TimeSpan LyricsDurationTime = TimeSpan.Zero;
        public AppObservableObj AppObservableObj { get;}
        public MusicBrowseViewModel(SystemMediaControlsService systemMediaControlsService,AppObservableObj  appObservableObj)
        {
            AppObservableObj = appObservableObj;
            //CurrentPlayMode = AppData.PlayMode;
            //PlayModeFlyoutText = ToolUtils.GetPlayModeText(AppData.PlayMode);
            //IsPlayDetailButtonVisible = AppSettings.IsPlayDetailBtnVisible;
            //LyricsMargin = new Thickness(AppSettings.LyricsMargin, 0, AppSettings.LyricsMargin, 0);
            //UILyrics = new ObservableCollection<LyricLine>();
            _systemMediaControlsService = systemMediaControlsService;
            InitializeSystemMediaControls();
            //Volume = (double)(AppData.Volume * 100);
            //_tempVolume = (double)(AppData.Volume * 100);
            AppSettings.OutputSettingsChanged += AppSettings_OutputSettingsChanged;
            AppSettings.OutputSettingsUpdated += AppSettings_OutputSettingsUpdated;
            AppSettings.EqUpdated += AppSettings_OnEqUpdated;
            if (AppSettings.IsFolderWatchEnabled)
            {
                StartWatchingFileFolder();
            }
            SortOptions = new ObservableCollection<SortOption>(_allSortOptions);
            StartWatchingUsbStorageDevices();
            progressTimer = new System.Timers.Timer(200);
            progressTimer.Elapsed += ProgressTimer_Elapsed;
        }

        private void AppSettings_OnEqUpdated(object? sender, EventArgs e)
        {
            _musicPlaybackService.EqUpdate();
        }

        private void AppSettings_OutputSettingsUpdated(object? sender, EventArgs e)
        {
            _musicPlaybackService.UpdateSettings();
        }

        public void StartProgressTimer()
        {
            progressTimer?.Start();
        }
        public void StopProgressTimer()
        {
            progressTimer?.Stop();
        }

        private void ProgressTimer_Elapsed(object? sender, ElapsedEventArgs e)
        {
            try
            {
                if (!IsUserDraggingProgressSlider)
                {
                    UpdateProgressTimerUI();
                }
            }
            catch (Exception)
            {
            }
        }

        public async void UpdateProgressTimerUI()
        {
            try
            {
                _totalTime = TimeSpan.FromSeconds(await _musicPlaybackService.GetTotalPosition());
                _currentTime = TimeSpan.FromSeconds(await _musicPlaybackService.GetCurrentPosition());
                _timeStringBuilder.Clear();
                App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                {
                    if (!isManualSelect)
                    {
                        try
                        {
                            ProgressSlider = _currentTime.TotalSeconds;
                            AppObservableObj.ProgressSliderMax = _totalTime.TotalSeconds;
                            if (_totalTime.TotalHours >= 1)
                            {
                                AppObservableObj.PlayTimeText = _timeStringBuilder
                                    .AppendFormat("{0:hh\\:mm\\:ss}/{1:hh\\:mm\\:ss}", _currentTime, _totalTime)
                                    .ToString();
                            }
                            else
                            {
                                AppObservableObj.PlayTimeText = _timeStringBuilder
                                    .AppendFormat("{0:mm\\:ss}/{1:mm\\:ss}", _currentTime, _totalTime)
                                    .ToString();
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine(ex.Message);
                        }
                    }
                });
                LyricsRefreshService.UpdateLyrics(_currentTime);
                _systemMediaControlsService.UpdateTimelineProperties(_currentTime, _totalTime);
            }
            catch {
            }            
        }
        //public void UpdateLyricsMargin()
        //{
        //    if (AppObservableObj.LyricsMargin.Left != AppSettings.LyricsMargin)
        //    {
        //        AppObservableObj.LyricsMargin = new Thickness(AppSettings.LyricsMargin, 0, AppSettings.LyricsMargin, 0);
        //    }
        //}

        public void AllSortOptions()
        {
            SortOptions.Clear();
            foreach (var options in _allSortOptions)
            {
                SortOptions.Add(options);
            }
            UpdateDisplayTexts();
            InitializeSortComboBox();
        }
        public void AlbumSortOptions()
        {
            SortOptions.Clear();
            foreach (var options in _albumSortOptions)
            {
                SortOptions.Add(options);
            }
            UpdateDisplayTexts();
            InitializeSortComboBox();
        }

        private void InitializeSortComboBox()
        {
            var matchingItem = SortOptions.AsValueEnumerable().FirstOrDefault(item => item.Tag == AppData.sortOrder);
            SelectedSortOption = matchingItem ?? SortOptions.AsValueEnumerable().FirstOrDefault();
            AppData.sortOrder = SelectedSortOption.Tag;
        }

        private void AppSettings_OutputSettingsChanged(object? sender, EventArgs e)
        {
            _musicPlaybackService.ChangingSetting();
        }

        public void UpdateDisplayTexts()
        {
            foreach (var option in SortOptions)
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
                deviceWatcher = DeviceInformation.CreateWatcher(deviceSelector);
                // 注册设备添加、移除和枚举完成事件
                deviceWatcher.Added += DeviceWatcher_Added;
                deviceWatcher.Removed += DeviceWatcher_Removed;
                deviceWatcher.EnumerationCompleted += DeviceWatcher_EnumerationCompleted;
                // 启动设备监视器
                deviceWatcher.Start();
            }
            catch {
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
            System.Diagnostics.Debug.WriteLine("设备枚举已完成");
        }

        public void UpdateInfoBar(string message)
        {
            App.MainWindow.DispatcherQueue.TryEnqueue(() =>
            {
                AppObservableObj.InfoBarIsOpen = true;
                AppObservableObj.InfoBarTitle = ToolUtils.GetString("InfoBarTitleConverter");
                AppObservableObj.InfoBarMessage = message;
            });
        }

        private async Task ReadUsbDevice()
        {
            try
            {
                AppData.usbStorageDevices = new ObservableCollection<UsbStorageDevice>(await UsbStorageDeviceReader.GetUsbStorageDevicesAsync());
                App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                {
                    if (AppData.usbStorageDevices.Count > 0)
                    {
                        AppObservableObj.UsbDeviceVisibility = Visibility.Visible;
                        AppObservableObj.UsbStorageDevices = AppData.usbStorageDevices;
                        AppObservableObj.UsbSelectedIndex = 0;
                    }
                    else
                    {
                        AppObservableObj.UsbSelectedIndex = -1;
                        AppObservableObj.UsbDeviceVisibility = Visibility.Collapsed;
                        AppObservableObj.UsbStorageDevices = null;
                        AppData.musicOnUsbDevice.Clear();
                        ClearAllUsbStatus();
                    }
                });
            }
            catch (Exception ex)
            {
                App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                {
                    AppObservableObj.UsbDeviceVisibility = Visibility.Collapsed;
                });
                AppData.musicOnUsbDevice.Clear();
                ClearAllUsbStatus();
                System.Diagnostics.Debug.WriteLine($"读取USB设备失败: {ex.Message}");
            }
        }

        public async void UsbDeviceComboxSelectionChanged(UsbStorageDevice usbStorageDevice)
        {
            Debug.WriteLine($"USB设备已选择: {usbStorageDevice.UniqueId}");
            AppData.usbStorageDevice = usbStorageDevice;
            List<UsbDeviceMusic> usbDeviceMusics = await MusicDatabaseService.GetUsbDeviceMusics(usbStorageDevice.UniqueId);
            if (usbDeviceMusics is not null && usbDeviceMusics.Count > 0)
            {
                // 检查是否需要重新扫描
                DateTime startTime = DateTime.Now;
                UsbDeviceSubFolderRescan usbDeviceSubFolderRescan = new UsbDeviceSubFolderRescan();
                await usbDeviceSubFolderRescan.UsbDeviceSubFolderAutoScan(usbDeviceMusics, usbStorageDevice.Path, usbStorageDevice.UniqueId);
                Debug.WriteLine($"UsbDeviceSubFolderAutoScan完成,耗时:{(DateTime.Now - startTime).TotalSeconds}秒");
                AppData.musicOnUsbDevice = await MusicDatabaseService.GetUsbDeviceMusics(usbStorageDevice.UniqueId);
                Debug.WriteLine($"USB设备扫描完成,耗时:{(DateTime.Now - startTime).TotalSeconds}秒");
            }
            else
            {
                // 读取USB设备中的音乐文件
                string folderPath = Path.Combine(usbStorageDevice.Path, "MUSIC");
                if (Directory.Exists(folderPath))
                {
                    AppData.musicOnUsbDevice = await MusicDatabaseService.RescanUsbDeviceFolderByPath(usbDeviceMusics, usbStorageDevice.UniqueId, folderPath, false);
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
                List<Folder> folders = await MusicDatabaseService.GetFolders();
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

                        watchers.Add(watcher);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
            }
        }

        public async void OnFileChanged(object sender, FileSystemEventArgs e)
        {
            if (!await scanSemaphore.WaitAsync(0) || !AppSettings.IsFolderWatchEnabled)
            {
                return;
            }
            try
            {
                App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                {
                    AppObservableObj.ProcessRingVisibility = Visibility.Visible;
                });
                await AutoRescanService.AutoScan();
                App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                {
                    AppObservableObj.ProcessRingVisibility = Visibility.Collapsed;
                });
            }
            finally
            {
                scanSemaphore.Release();
            }
        }

        private void InitializeSystemMediaControls()
        {

            // 订阅事件
            _systemMediaControlsService.PlayRequested += (s, e) =>
            {
                App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                {
                    PlayButton_Click();
                });
            };

            _systemMediaControlsService.PauseRequested += (s, e) =>
            {
                App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                {
                    PlayButton_Click();
                });
            };

            _systemMediaControlsService.NextTrackRequested += (s, e) =>
            {
                App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                {
                    NextMusicButton_Click();
                });
            };

            _systemMediaControlsService.PreviousTrackRequested += (s, e) =>
            {
                App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                {
                    LastMusicButton_Click();
                });
            };
        }

        public void SetMusicService(BassPlayerCommandService musicPlaybackService)
        {
            _musicPlaybackService = musicPlaybackService;
            InitializeDatabase();
        }

        public void SetLyricsService(LyricsRefreshService lyricsRefreshService)
        {
            LyricsRefreshService = lyricsRefreshService;
        }

        private async void InitializeDatabase()
        {
            try
            {
                _musicPlaybackService.isInitializing = true;
                await LoadPlayState();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"错误: {ex.Message}");
            }
        }

        private async Task LoadPlayState()
        {
            _musicPlaybackService.lastPlayedMusicId = AppData.LastPlayedMusicId;
            AppObservableObj.CurrentPlayingMusic = await MusicDatabaseService.LoadCurrentPlayingMusic(AppData.LastPlayedMusicId);
            if (AppObservableObj.CurrentPlayingMusic is not null)
            {
                UpdatePlayBar(AppObservableObj.CurrentPlayingMusic);
                LoadLyricsToUI();
                _musicPlaybackService.InitializeMusicUrl(AppObservableObj.CurrentPlayingMusic.Path);
            }
            _musicPlaybackService.isInitializing = false;
        }

        public async void LoadLyricsToUI()
        {
            AppObservableObj.LastLyricIndex = -1;
            AppObservableObj.UILyrics.Clear();
            // 设置播放服务中的歌词
            await LyricsRefreshService.SetLyrics();
            // 解析歌词并添加到UI集合
            List<LyricLine> parsedLyrics = LyricsRefreshService.Lyrics;
            foreach (var lyric in parsedLyrics)
            {
                AppObservableObj.UILyrics.Add(lyric);
            }
        }

        public void UpdateLyricsToUI(int index)
        {
            if (AppObservableObj.LastLyricIndex == index)
                return;
            TimeSpan duration = TimeSpan.Zero;
            if (index >= 0 && index < AppObservableObj.UILyrics.Count)
            {
                int nextIndex = index + 1;
                if (nextIndex < AppObservableObj.UILyrics.Count)
                {
                    TimeSpan currentTime = AppObservableObj.UILyrics[index].Time;
                    TimeSpan nextTime = AppObservableObj.UILyrics[nextIndex].Time;
                    LyricsDurationTime = nextTime.Subtract(currentTime);
                }
            }
            App.MainWindow.DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    for (int i = 0; i < AppObservableObj.UILyrics.Count; i++)
                    {
                        AppObservableObj.UILyrics[i].IsCurrent = (i == index);
                    }
                    AppObservableObj.LastLyricIndex = index;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"更新歌词失败: {ex.Message}");
                }
            });
        }

        public async void UpdatePlayBar(Music music)
        {
            var albumCoverData = await ToolUtils.GetRawImage(music);
            BitmapImage DetailCover = await ToolUtils.ConvertByteArrayToBitmapImage(albumCoverData);
            await UpdateCover(albumCoverData);
            App.MainWindow.DispatcherQueue.TryEnqueue(() =>
            {
                AppObservableObj.MusicInfo = $"{music.Extension} {music.SampleRate}Hz {music.BitDepth}bit {music.BitRate}kbps";
                AppObservableObj.MusicDetailCover = DetailCover;
                _musicBrowsePage.ChangeAcrylicBrushBackgroundOpacity();
            });
            _systemMediaControlsService.UpdateSystemMediaControlsState();
            _systemMediaControlsService.UpdateTimelineProperties(TimeSpan.Zero, music.Duration);
            _ = _systemMediaControlsService.UpdateMediaInfo(music.Title, music.Author, music.Album, albumCoverData);           
        }

        public async void ThemeChangedUpdateCover()
        {
            if (AppObservableObj.CurrentPlayingMusic is null) return;
            var albumCoverData = await ToolUtils.GetRawImage(AppObservableObj.CurrentPlayingMusic);
            await UpdateCover(albumCoverData);
        }

        private async Task UpdateCover(byte[] cover)
        {
            try
            {
                if (cover != null)
                {
                    bool isDarkMode = true;
                    if (AppSettings.AppTheme == "Light")
                    {
                        isDarkMode = false;
                    }
                    else if (AppSettings.AppTheme == "Default")
                    {
                        // 注意：GetIsLightTheme() 最好是同步方法
                        isDarkMode = !GetIsLightTheme();
                    }
                    AppObservableObj.LyricPageBackgroundSource = await ImageHelper.ApplyMicaEffectWin2DAsync(cover, isDarkMode);
                }
                else
                {
                    AppObservableObj.LyricPageBackgroundSource = null;
                }
            }
            catch
            {
                AppObservableObj.LyricPageBackgroundSource = null;
            }
        }

        public void SetMusicBrowsePage(MusicBrowsePage musicBrowsePage)
        {
            _musicBrowsePage = musicBrowsePage;
            InitializeSortComboBox();
        }

        [RelayCommand]
        public void OnPlayModeChanged()
        {
            switch (AppObservableObj.CurrentPlayMode)
            {
                case PlayMode.SingleLoop:
                    //AppData.PlayMode = PlayMode.ListLoop;
                    AppObservableObj.CurrentPlayMode = PlayMode.ListLoop;
                    AppObservableObj.PlayModeFlyoutText = ToolUtils.GetString("IconListLoop");
                    break;
                case PlayMode.ListLoop:
                    //AppData.PlayMode = PlayMode.RandomLoop;
                    AppObservableObj.CurrentPlayMode = PlayMode.RandomLoop;
                    AppObservableObj.PlayModeFlyoutText = ToolUtils.GetString("IconRandomLoop");
                    break;
                case PlayMode.RandomLoop:
                    //AppData.PlayMode = PlayMode.RepeatOff;
                    AppObservableObj.CurrentPlayMode = PlayMode.RepeatOff;
                    AppObservableObj.PlayModeFlyoutText = ToolUtils.GetString("IconSinglePlayback");
                    break;
                case PlayMode.RepeatOff:
                    //AppData.PlayMode = PlayMode.SingleLoop;
                    AppObservableObj.CurrentPlayMode = PlayMode.SingleLoop;
                    AppObservableObj.PlayModeFlyoutText = ToolUtils.GetString("IconSingleTuneCirculation");
                    break;
            }
            _musicPlaybackService.UpdateCurrentPlayList();
            App.MainWindow.UpdateAppNotifyIconControl();
            _musicPlaybackService.UpdateSettings();

        }
        [RelayCommand]
        public void OnPlayButtonChanged()
        {
            PlayButton_Click();
        }

        public void PlayButton_Click()
        {
            _musicPlaybackService.PlayButton();
            UpdatePlayPauseButtonIcon();
        }


        public void UpdatePlayPauseButtonIcon()
        {
            App.MainWindow.UpdateTaskbarIcon();
            _systemMediaControlsService.UpdateSystemMediaControlsState();
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
            _musicPlaybackService.PlayNextTrack();
        }

        public void LastMusicButton_Click()
        {
            PlayLastTrack();
        }

        public void PlayMusic(Music music)
        {
            App.MainWindow.DispatcherQueue.TryEnqueue(() =>
            {
                _musicBrowsePage.PlayMusic(music);
            });
        }

        public void ShowPlayingDetail()
        {
            _musicBrowsePage.ShowPlayingDetail();
        }


        private void PlayLastTrack()
        {
            int index = AppObservableObj.CurrentPlayingList.AsValueEnumerable()
                        .Select((music, i) => new { Music = music, Index = i })
                        .FirstOrDefault(x => x.Music.Id == AppObservableObj.CurrentPlayingMusic.Id)
                        ?.Index ?? -1;
            if (index > 0)
            {
                _musicBrowsePage.PlayMusic(AppObservableObj.CurrentPlayingList[index - 1]);
            }
            else if (index == 0 && AppObservableObj.CurrentPlayingList.Count > 1)
            {
                _musicBrowsePage.PlayMusic(AppObservableObj.CurrentPlayingList[AppObservableObj.CurrentPlayingList.Count - 1]);

            }
        }
        [RelayCommand]
        private async Task OnPlayBarFavouriteButtonChanged()
        {
            await _musicBrowsePage.AddToFavourite(AppObservableObj.CurrentPlayingMusic);
            NotifySubPageUpdateFavouriteState();
            AppData.allSongs = await MusicDatabaseService.GetMusicListAsync();
        }

        private void NotifySubPageUpdateFavouriteState()
        {
            var songCollectionPage = App.Services.GetRequiredService<SongCollectionViewModel>();
            var songListPage = App.Services.GetRequiredService<SongListViewModel>();
            var favouritePlayListPage = App.Services.GetRequiredService<FavouritePlayListViewModel>();
            var playListSongPage = App.Services.GetRequiredService<PlayListSongViewModel>();
            Task.WhenAll(
                Task.Run(() => favouritePlayListPage.UpdateFavouriteMusic(AppObservableObj.CurrentPlayingMusic)),
                Task.Run(() => songListPage.UpdateFavouriteMusic(AppObservableObj.CurrentPlayingMusic)),
                Task.Run(() => songCollectionPage.UpdateFavouriteMusic(AppObservableObj.CurrentPlayingMusic)),
                Task.Run(() => playListSongPage.UpdateFavouriteMusic(AppObservableObj.CurrentPlayingMusic))
            );
        }
        [RelayCommand]
        private void OnStopButtonChanged()
        {
            _musicPlaybackService.MusicEnd();
            UpdatePlayPauseButtonIcon();
            ProgressSlider = 0;
        }
        [RelayCommand]
        private void OnFastForwardButton()
        {
            AdjustPlaybackPosition(5);
        }
        [RelayCommand]
        private void OnFastBackwardButton()
        {
            AdjustPlaybackPosition(-5);
        }
        public async void AdjustPlaybackPosition(int seconds)
        {
            ProgressSlider = await _musicPlaybackService.AdjustPlaybackPosition(seconds);
        }
        [RelayCommand]
        private void OnVolumeSliderIconButtonChanged()
        {
            AppObservableObj.IsMuted = !AppObservableObj.IsMuted;
            AppObservableObj.Volume = AppObservableObj.IsMuted ? 0 : AppObservableObj.TempVolume;
        }

        [RelayCommand]
        private void OnVolumeUpChanged()
        {
            AdjustVolume(1);
        }
        [RelayCommand]
        private void OnVolumeDownChanged()
        {
            AdjustVolume(-1);
        }

        public void AdjustVolume(int delta)
        {
            double newVolume = AppObservableObj.Volume + delta;
            newVolume = Math.Max(0, Math.Min(newVolume, 100));
            AppObservableObj.Volume = newVolume;
        }
        [RelayCommand]
        private void OnFullScreenButtonChanged()
        {
            if (App.MainWindow.AppWindow is not null)
            {
                if (AppObservableObj.IsFullScreen)
                {
                    App.MainWindow.AppWindow.SetPresenter(AppWindowPresenterKind.Default);
                }
                else
                {
                    App.MainWindow.AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
                }
                AppObservableObj.IsFullScreen = !AppObservableObj.IsFullScreen;
            }

        }

        [RelayCommand]
        private void PlayDetailButtonVisibleChanged() {
            AppObservableObj.IsPlayDetailButtonVisible = !AppObservableObj.IsPlayDetailButtonVisible;
        }

        private void OnSelectionChanged()
        {
            if (SortOptions.Count == 2)
            {
                AllSortOptions();
            }
            int currentSelectedIndex = GetSelectorBarItemIndex(SelectedPage);
            currentPage = typeof(SongListPage);
            switch (SelectedPage.Name)
            {
                case "Song":
                    AppObservableObj.PageType = "song";
                    currentPage = typeof(SongListPage);
                    break;
                case "Album":
                    if (CurrentAlbum is not null && !string.IsNullOrEmpty(CurrentAlbum.Album))
                    {
                        AppObservableObj.PageType = "album";
                        paramName = CurrentAlbum.Album;
                        currentPage = typeof(SongCollectionPage);
                    }
                    else
                    {
                        AppObservableObj.PageType = "albumBrowse";
                        currentPage = typeof(AlbumPage);
                    }
                    break;
                case "Artist":
                    if (CurrentArtist is not null && !string.IsNullOrEmpty(CurrentArtist.Author))
                    {
                        AppObservableObj.PageType = "artist";
                        paramName = CurrentArtist.Author;
                        currentPage = typeof(SongCollectionPage);
                    }
                    else
                    {
                        AppObservableObj.PageType = "artistBrowse";
                        currentPage = typeof(ArtistPage);
                    }
                    break;
                case "Folder":
                    if (CurrentFolder is not null && !string.IsNullOrEmpty(CurrentFolder.LastLevelFolderPath))
                    {
                        AppObservableObj.PageType = "folder";
                        paramName = CurrentFolder.LastLevelFolderPath;
                        currentPage = typeof(SongCollectionPage);
                    }
                    else
                    {
                        AppObservableObj.PageType = "folderBrowse";
                        currentPage = typeof(FolderBrowsePage);
                    }
                    break;
                case "Favourite":
                    AppObservableObj.PageType = "favourite";
                    currentPage = typeof(FavouritePlayListPage);
                    break;
                case "PlayList":
                    if (currentPlayList is not null)
                    {
                        AppObservableObj.PageType = "playlist";
                        paramName = currentPlayList.Name;
                        currentPlayListId = currentPlayList.Id;
                        currentPage = typeof(PlayListSongPage);
                    }
                    else
                    {
                        AppObservableObj.PageType = "playlistBrowse";
                        currentPage = typeof(PlayListPage);
                    }
                    break;
            }
            var slideNavigationTransitionEffect = currentSelectedIndex - previousSelectedIndex > 0 ? SlideNavigationTransitionEffect.FromRight : SlideNavigationTransitionEffect.FromLeft;
            _musicBrowsePage.NavigatePage(currentPage, new SlideNavigationTransitionInfo() { Effect = slideNavigationTransitionEffect }, AppSettings.SlideAnimationTime);
            previousSelectedIndex = currentSelectedIndex;
            _musicBrowsePage.DisableBackButton();
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
                "PlayList" => 5,
                _ => -1
            };
        }

        private void OnSelectSortChanged()
        {
            try
            {
                if (SelectedSortOption is not null)
                {
                    AppData.sortOrder = SelectedSortOption.Tag;
                    _musicBrowsePage.SelectSortOptionChanged();
                }
            }
            catch (Exception)
            {
            }
        }        
    }
}
