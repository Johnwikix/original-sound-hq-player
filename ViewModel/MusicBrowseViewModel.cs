using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Devices.Portable;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Reader;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.View;
using static WinUIMusicPlayer.Utils.ToolUtils;

namespace WinUIMusicPlayer.ViewModel
{
    public partial class MusicBrowseViewModel : ObservableObject
    {
        private PlayMode _currentPlayMode = PlayMode.ListLoop;
        public PlayMode CurrentPlayMode
        {
            get => _currentPlayMode;
            set => SetProperty(ref _currentPlayMode, value);
        }

        private Music _currentPlayingMusic;
        public Music CurrentPlayingMusic
        {
            get => _currentPlayingMusic;
            set => SetProperty(ref _currentPlayingMusic, value);
        }

        private string _playModeFlyoutText;
        public string PlayModeFlyoutText
        {
            get => _playModeFlyoutText;
            set => SetProperty(ref _playModeFlyoutText, value);
        }

        private ObservableCollection<Music> _sequentialPlayingList;
        public ObservableCollection<Music> SequentialPlayingList
        {
            get => _sequentialPlayingList;
            set
            {
                SetProperty(ref _sequentialPlayingList, value);
            }
        }

        private ObservableCollection<Music> _currentPlayingList;
        public ObservableCollection<Music> CurrentPlayingList
        {
            get => _currentPlayingList;
            set {
                SetProperty(ref _currentPlayingList, value);
            } 
        }

        private string _musicInfo;
        public string MusicInfo
        {
            get => _musicInfo;
            set => SetProperty(ref _musicInfo, value);
        }

        public BitmapImage _musicDetailCover;
        public BitmapImage MusicDetailCover
        {
            get => _musicDetailCover;
            set => SetProperty(ref _musicDetailCover, value);
        }

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
                    if (IsMouseOverProgressBar)
                    {
                        if (!IsUserDraggingProgressSlider)
                        {
                            if (_musicPlaybackService.multiTypeAudioReader != null)
                            {
                                double currentPlayPosition = _musicPlaybackService.multiTypeAudioReader.CurrentTime.TotalSeconds;

                                if (Math.Abs(value - currentPlayPosition) > 2.0)
                                {     
                                    Task.Run(() =>
                                    {
                                        _musicPlaybackService.isManualSelect = true;
                                        _musicPlaybackService.ChangeWaveChannelTime(TimeSpan.FromSeconds(value));
                                        _musicPlaybackService.isManualSelect = false;
                                    });                                                            
                                }
                            }
                        }
                    }
                }
            }
        }

        private string _playTimeText = "00:00/00:00";
        public string PlayTimeText
        {
            get => _playTimeText;
            set => SetProperty(ref _playTimeText, value);
        }

        private double _progressSliderMax = 100;
        public double ProgressSliderMax
        {
            get => _progressSliderMax;
            set => SetProperty(ref _progressSliderMax, value);
        }

        private double _tempVolume = 50;
        private double _volume = 50;
        public double Volume
        {
            get => _volume;
            set
            {
                if (SetProperty(ref _volume, value))
                {
                    if (IsInitialized)
                    {
                        if (value > 0) {
                            IsMuted = false;
                        }
                        if (!IsMuted) {
                            _tempVolume = value;
                        }                        
                        _musicPlaybackService.volume = (float) value / 100;
                        if (_musicPlaybackService.multiTypeAudioReader != null)
                        {
                            _musicPlaybackService.multiTypeAudioReader.Volume = AppSettings.isDsd ? _musicPlaybackService.volume * (float)Math.Pow(10, AppSettings.dsdGain / 20.0) : _musicPlaybackService.volume;
                        }
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


        private bool _isPlaying = false;
        public bool IsPlaying
        {
            get => _isPlaying;
            set => SetProperty(ref _isPlaying, value);
        }
        private bool _isMouseOverProgressBar = false;
        public bool IsMouseOverProgressBar
        {
            get => _isMouseOverProgressBar;
            set => SetProperty(ref _isMouseOverProgressBar, value);
        }
        private bool _isMuted = false;
        public bool IsMuted
            {
            get => _isMuted;
            set => SetProperty(ref _isMuted, value);
        }
        private bool _isInitialized = false;
        public bool IsInitialized
        {
            get => _isInitialized;
            set
            {
                if (SetProperty(ref _isInitialized, value))
                {
                }
            }
        }
        private ObservableCollection<LyricLine> _uiLyrics;
        public ObservableCollection<LyricLine> UILyrics
        {
            get => _uiLyrics;
            set => SetProperty(ref _uiLyrics, value);
        }
        private int _lastLyricIndex = -1;
        public int LastLyricIndex
        {
            get => _lastLyricIndex;
            set => SetProperty(ref _lastLyricIndex, value);
        }
        private Visibility _usbDeviceVisibility=Visibility.Collapsed;
        public Visibility UsbDeviceVisibility
        {
            get => _usbDeviceVisibility;
            set => SetProperty(ref _usbDeviceVisibility, value);
        }
        private ObservableCollection<UsbStorageDevice> _usbStorageDevices;
        public ObservableCollection<UsbStorageDevice> UsbStorageDevices
        {
            get => _usbStorageDevices;
            set => SetProperty(ref _usbStorageDevices, value);
        }
        private int _usbSelectedIndex = 0;
        public int UsbSelectedIndex
        {
            get => _usbSelectedIndex;
            set =>SetProperty(ref _usbSelectedIndex, value);
        }
        private Visibility _processRingVisibility = Visibility.Collapsed;
        public Visibility ProcessRingVisibility
        {
            get => _processRingVisibility;
            set => SetProperty(ref _processRingVisibility, value);
        }
        private bool _isFullScreen = false;
        public bool IsFullScreen
        {
            get => _isFullScreen;
            set => SetProperty(ref _isFullScreen, value);
        }
        private string _infoBarTitle = string.Empty;
        public string InfoBarTitle
        {
            get => _infoBarTitle;
            set => SetProperty(ref _infoBarTitle, value);
        }
        private bool _infoBarIsOpen = false;
        public bool InfoBarIsOpen
        {
            get => _infoBarIsOpen;
            set => SetProperty(ref _infoBarIsOpen, value);
        }
        private string _infoBarMessage = string.Empty;
        public string InfoBarMessage
        {
            get => _infoBarMessage;
            set => SetProperty(ref _infoBarMessage, value);
        }
        private string _pageType=string.Empty;
        public string PageType
        {
            get => _pageType;
            set => SetProperty(ref _pageType, value);
        }
        private bool _isInPlayingDetailMode = false;
        public bool IsInPlayingDetailMode {
            get => _isInPlayingDetailMode;
            set => SetProperty(ref _isInPlayingDetailMode, value);
        }
        private bool _isAcrylicBrushOpacity = false;
        public bool IsAcrylicBrushOpacity
        {
            get => _isAcrylicBrushOpacity;
            set => SetProperty(ref _isAcrylicBrushOpacity, value);
        }

        private float _topControlsOpacity = 1.0f;
        public float TopControlsOpacity
        {
            get => _topControlsOpacity;
            set => SetProperty(ref _topControlsOpacity, value);
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
        private SortOption _selectedSortOption;
        public SortOption SelectedSortOption
        {
            get => _selectedSortOption;
            set 
            {
                if (SetProperty(ref _selectedSortOption, value)) {
                    OnSelectSortChanged();
                }
            }
        }
        public System.Type currentPage = typeof(SongListPage);
        public Music CurrentAlbum;
        public Music CurrentArtist;
        public Music CurrentFolder;
        public PlayList currentPlayList;
        public string paramName = "defualt";
        public int previousSelectedIndex = 0;
        public int currentPlayListId;
        public MusicPlaybackService _musicPlaybackService;
        private SystemMediaControlsService _systemMediaControlsService;
        private MusicBrowsePage _musicBrowsePage;
        private DeviceWatcher deviceWatcher;
        private List<FileSystemWatcher> watchers = [];
        private readonly SemaphoreSlim scanSemaphore = new(1, 1);
        public MusicBrowseViewModel(SystemMediaControlsService systemMediaControlsService)
        {
            CurrentPlayMode = AppData.PlayMode;
            PlayModeFlyoutText = ToolUtils.GetPlayModeText(AppData.PlayMode);
            UILyrics = new ObservableCollection<LyricLine>();
            _systemMediaControlsService = systemMediaControlsService;
            InitializeSystemMediaControls();
            Volume = (double)(AppData.Volume * 100);
            _tempVolume = (double)(AppData.Volume * 100);
            AppSettings.OutputSettingsChanged += AppSettings_OutputSettingsChanged;
            if (AppSettings.IsFolderWatchEnabled) {
                StartWatchingFileFolder();
            }
            SortOptions = new ObservableCollection<SortOption>(_allSortOptions);
            StartWatchingUsbStorageDevices();
            //UpdateDisplayTexts();           
        }

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
            var matchingItem = SortOptions.FirstOrDefault(item => item.Tag == AppData.sortOrder);
            SelectedSortOption = matchingItem ?? SortOptions.FirstOrDefault();
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

        private async void DeviceWatcher_Added(DeviceWatcher sender, DeviceInformation args)
        {
            // 当 USB 存储设备插入时触发            
            System.Diagnostics.Debug.WriteLine($"USB 存储设备已插入: {args.Name},{args}");
            Task.Delay(1500).Wait(); // 等待设备稳定
            await ReadUsbDevice();
        }

        private async void DeviceWatcher_Removed(DeviceWatcher sender, DeviceInformationUpdate args)
        {
            // 当 USB 存储设备移除时触发            
            System.Diagnostics.Debug.WriteLine($"USB 存储设备已移除");
            await ReadUsbDevice();
        }

        private void DeviceWatcher_EnumerationCompleted(DeviceWatcher sender, object args)
        {
            // 设备枚举完成时触发
            System.Diagnostics.Debug.WriteLine("设备枚举已完成");
        }

        public void UpdateInfoBar(string message) {
            App.MainWindow.DispatcherQueue.TryEnqueue(() =>
            {
                InfoBarIsOpen = true;
                InfoBarTitle = ToolUtils.GetString("InfoBarTitleConverter");
                InfoBarMessage = message;
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
                        UsbDeviceVisibility = Visibility.Visible;
                        UsbStorageDevices = AppData.usbStorageDevices;
                        UsbSelectedIndex = 0;
                    }
                    else
                    {
                        UsbSelectedIndex = -1;
                        UsbDeviceVisibility = Visibility.Collapsed;                        
                        UsbStorageDevices = null;
                        AppData.musicOnUsbDevice.Clear();
                        ClearAllUsbStatus();
                    }
                });
            }
            catch (Exception ex)
            {
                App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                {
                    UsbDeviceVisibility = Visibility.Collapsed;
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
            if (usbDeviceMusics != null && usbDeviceMusics.Count > 0)
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
            catch (Exception ex) {
                Debug.WriteLine(ex.Message);
            }            
        }

        public async void OnFileChanged(object sender, FileSystemEventArgs e)
        {
            if (!await scanSemaphore.WaitAsync(0) || !AppSettings.IsFolderWatchEnabled)
            {
                Debug.WriteLine("已经有扫描操作在进行，忽略此次事件");
                return;
            }
            try
            {
                App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                {
                    ProcessRingVisibility = Visibility.Visible;
                });
                await AutoRescanService.AutoScan();
                App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                {
                    ProcessRingVisibility = Visibility.Collapsed;
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

        public void SetMusicService(MusicPlaybackService musicPlaybackService)
        {
            _musicPlaybackService = musicPlaybackService;
            InitializeDatabase();
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
            _musicPlaybackService.volume = AppData.Volume;
            CurrentPlayingMusic = await MusicDatabaseService.LoadCurrentPlayingMusic(AppData.LastPlayedMusicId);
            if (CurrentPlayingMusic != null)
            {
                UpdatePlayBar(CurrentPlayingMusic);
                LoadLyricsToUI();
            }
            _musicPlaybackService.isInitializing = false;
        }

        public async void LoadLyricsToUI()
        {
            LastLyricIndex = -1;
            UILyrics.Clear();
            // 设置播放服务中的歌词
            await _musicPlaybackService.SetLyrics();
            // 解析歌词并添加到UI集合
            List<LyricLine> parsedLyrics = _musicPlaybackService._lyrics;
            UILyrics.Clear();
            foreach (var lyric in parsedLyrics)
            {
                UILyrics.Add(lyric);
            }
        }

        public void UpdateLyricsToUI(int index) {
            if (LastLyricIndex == index || !IsInPlayingDetailMode)
                return;
            App.MainWindow.DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    for (int i = 0; i < UILyrics.Count; i++)
                    {
                        var lyric = UILyrics[i];
                        UILyrics[i].IsCurrent = (i == index);
                    }
                    LastLyricIndex = index;
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
            App.MainWindow.DispatcherQueue.TryEnqueue(() =>
            {
                MusicInfo = $"{music.Extension} {music.SampleRate}Hz {music.BitDepth}bit {music.BitRate}kbps";
                MusicDetailCover = DetailCover;
                _musicBrowsePage.ChangeAcrylicBrushBackgroundOpacity();
            });
            _systemMediaControlsService.UpdateSystemMediaControlsState();
            await _systemMediaControlsService.UpdateMediaInfo(music.Title, music.Author, music.Album, albumCoverData);
        }

        public void SetMusicBrowsePage(MusicBrowsePage musicBrowsePage)
        {
            _musicBrowsePage = musicBrowsePage;
            InitializeSortComboBox();
        }

        [RelayCommand]
        public void OnPlayModeChanged()
        {
            switch (CurrentPlayMode)
            {
                case PlayMode.SingleLoop:
                    AppData.PlayMode = PlayMode.ListLoop;
                    CurrentPlayMode = PlayMode.ListLoop;
                    PlayModeFlyoutText = ToolUtils.GetString("IconListLoop");
                    break;
                case PlayMode.ListLoop:
                    AppData.PlayMode = PlayMode.RandomLoop;
                    CurrentPlayMode = PlayMode.RandomLoop;
                    PlayModeFlyoutText = ToolUtils.GetString("IconRandomLoop");
                    break;
                case PlayMode.RandomLoop:
                    AppData.PlayMode = PlayMode.RepeatOff;
                    CurrentPlayMode = PlayMode.RepeatOff;
                    PlayModeFlyoutText = ToolUtils.GetString("IconSinglePlayback");
                    break;
                case PlayMode.RepeatOff:
                    AppData.PlayMode = PlayMode.SingleLoop;
                    CurrentPlayMode = PlayMode.SingleLoop;
                    PlayModeFlyoutText = ToolUtils.GetString("IconSingleTuneCirculation");
                    break;
            }
            _musicPlaybackService.UpdateCurrentPlayList();
            App.MainWindow.UpdateAppNotifyIconControl();
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
            //App.MainWindow.UpdateIconControl();
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
            //_musicPlaybackService.isManualSelect = true;
            _musicPlaybackService.PlayNextTrack();
            //_musicPlaybackService.isManualSelect = false;
        }

        public void LastMusicButton_Click()
        {
            //_musicPlaybackService.isManualSelect = true;
            PlayLastTrack();
            //_musicPlaybackService.isManualSelect = false;
        }

        public void PlayMusic(Music music)
        {
            App.MainWindow.DispatcherQueue.TryEnqueue(() =>
            {
                _musicBrowsePage.PlayMusic(music);
            });
        }

        public async void ShowPlayingDetail() {
            await _musicBrowsePage.ShowPlayingDetail();
        }
           

        private void PlayLastTrack()
        {
            int index = CurrentPlayingList
                        .Select((music, i) => new { Music = music, Index = i })
                        .FirstOrDefault(x => x.Music.Id == CurrentPlayingMusic.Id)
                        ?.Index ?? -1;
            if (index > 0)
            {
                _musicBrowsePage.PlayMusic(CurrentPlayingList[index - 1]);
            }
            else if (index == 0 && CurrentPlayingList.Count > 1)
            {
                _musicBrowsePage.PlayMusic(CurrentPlayingList[CurrentPlayingList.Count - 1]);

            }
        }
        [RelayCommand]
        private async Task OnPlayBarFavouriteButtonChanged()
        {
            await _musicBrowsePage.AddToFavourite(CurrentPlayingMusic);            
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
                Task.Run(() => favouritePlayListPage.UpdateFavouriteMusic(CurrentPlayingMusic)),
                Task.Run(() => songListPage.UpdateFavouriteMusic(CurrentPlayingMusic)),
                Task.Run(() => songCollectionPage.UpdateFavouriteMusic(CurrentPlayingMusic)),
                Task.Run(() => playListSongPage.UpdateFavouriteMusic(CurrentPlayingMusic))
            );
        }
        [RelayCommand]
        private void OnStopButtonChanged()
        {
            _musicPlaybackService.StopPlaying();
            UpdatePlayPauseButtonIcon();
            _musicPlaybackService.Reset();
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
        public void AdjustPlaybackPosition(int seconds)
        {
            ProgressSlider = _musicPlaybackService.AdjustPlaybackPosition(seconds);
        }
        [RelayCommand]
        private void OnVolumeSliderIconButtonChanged()
        {
            IsMuted = !IsMuted;
            Volume = IsMuted ? 0 : _tempVolume;
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
            double newVolume = Volume + delta;
            newVolume = Math.Max(0, Math.Min(newVolume, 100));
            Volume = newVolume;
        }
        [RelayCommand]
        private void OnFullScreenButtonChanged()
        {
            if (App.MainWindow.AppWindow != null)
            {
                if (IsFullScreen)
                {
                    App.MainWindow.AppWindow.SetPresenter(AppWindowPresenterKind.Default);
                    //HideNavigationViewButtonVisibility = false;
                }
                else
                {
                    App.MainWindow.AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
                    //HideNavigationViewButtonVisibility = true;
                }
                IsFullScreen = !IsFullScreen;
            }

        }
        private void OnSelectionChanged()
        {
            if (SortOptions.Count == 2) {
                AllSortOptions();
            }            
            int currentSelectedIndex = GetSelectorBarItemIndex(SelectedPage);
            currentPage = typeof(SongListPage);
            switch (SelectedPage.Name)
            {
                case "Song":
                    PageType = "song";
                    currentPage = typeof(SongListPage);
                    break;
                case "Album":
                    if (CurrentAlbum != null && !string.IsNullOrEmpty(CurrentAlbum.Album))
                    {
                        PageType = "album";
                        paramName = CurrentAlbum.Album;
                        currentPage = typeof(SongCollectionPage);
                    }
                    else
                    {
                        PageType = "albumBrowse";
                        currentPage = typeof(AlbumPage);
                    }
                    break;
                case "Artist":
                    if (CurrentArtist != null && !string.IsNullOrEmpty(CurrentArtist.Author))
                    {
                        PageType = "artist";
                        paramName = CurrentArtist.Author;
                        currentPage = typeof(SongCollectionPage);
                    }
                    else
                    {
                        PageType = "artistBrowse";
                        currentPage = typeof(ArtistPage);                        
                    }
                    break;
                case "Folder":
                    if (CurrentFolder != null && !string.IsNullOrEmpty(CurrentFolder.LastLevelFolderPath))
                    {
                        PageType = "folder";
                        paramName = CurrentFolder.LastLevelFolderPath;
                        currentPage = typeof(SongCollectionPage);
                    }
                    else
                    {
                        PageType = "folderBrowse";
                        currentPage = typeof(FolderBrowsePage);
                    }
                    break;
                case "Favourite":
                    PageType = "favourite";
                    currentPage = typeof(FavouritePlayListPage);
                    break;
                case "PlayList":
                    if (currentPlayList != null)
                    {
                        PageType = "playlist";
                        paramName = currentPlayList.Name;
                        currentPlayListId = currentPlayList.Id;
                        currentPage = typeof(PlayListSongPage);
                    }
                    else
                    {
                        PageType = "playlistBrowse";
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
            if (item == null) return -1;
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

        private void OnSelectSortChanged() {
            try
            {
                if (SelectedSortOption != null)
                {
                    AppData.sortOrder = SelectedSortOption.Tag;
                    _musicBrowsePage.SelectSortOptionChanged();
                }
            }
            catch (Exception ex) {
            }            
        }
    }
}
