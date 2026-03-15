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
using System.Linq;
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
        private SystemMediaControlsService SystemMediaControlsService{ get; set; }
        private MusicBrowsePage MusicBrowsePage { get; set; }
        private DeviceWatcher DeviceWatcher { get; set; }
        private List<FileSystemWatcher> Watchers { get; set; } = [];
        private readonly SemaphoreSlim scanSemaphore = new(1, 1);

        public AppViewModel AppViewModel { get;}
        private MusicDatabaseService _musicDatabaseService { get; }
        public MusicBrowseViewModel(BassPlayerCommandService bassPlayerCommand,SystemMediaControlsService systemMediaControlsService,AppViewModel appViewModel,MusicDatabaseService musicDatabaseService,AudioConverterService converterService)
        {
            this.AppViewModel = appViewModel;
            _musicDatabaseService = musicDatabaseService;
            ConverterService = converterService;
            MusicPlaybackService = bassPlayerCommand;
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
            ProgressDialog.RequestedTheme = AppSettings.ElementTheme;
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
            ProgressDialog.XamlRoot = MusicBrowsePage.XamlRoot;
            _ = ProgressDialog.ShowAsync();

            foreach (Music music in musics) {
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
                ProgressDialog.XamlRoot = MusicBrowsePage.XamlRoot;
                _ = ProgressDialog.ShowAsync();
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
                System.Diagnostics.Debug.WriteLine($"读取USB设备失败: {ex.Message}");
            }
        }

        public async void UsbDeviceComboxSelectionChanged(UsbStorageDevice usbStorageDevice)
        {
            Debug.WriteLine($"USB设备已选择: {usbStorageDevice.UniqueId}");
            AppData.UsbStorageDevice = usbStorageDevice;
            List<UsbDeviceMusic> usbDeviceMusics = await _musicDatabaseService.GetUsbDeviceMusics(usbStorageDevice.UniqueId);
            if (usbDeviceMusics is not null && usbDeviceMusics.Count > 0)
            {
                // 检查是否需要重新扫描
                DateTime startTime = DateTime.Now;
                UsbDeviceSubFolderRescan usbDeviceSubFolderRescan = new UsbDeviceSubFolderRescan();
                await usbDeviceSubFolderRescan.UsbDeviceSubFolderAutoScan(usbDeviceMusics, usbStorageDevice.Path, usbStorageDevice.UniqueId);
                Debug.WriteLine($"UsbDeviceSubFolderAutoScan完成,耗时:{(DateTime.Now - startTime).TotalSeconds}秒");
                AppData.MusicOnUsbDevice = await _musicDatabaseService.GetUsbDeviceMusics(usbStorageDevice.UniqueId);
                Debug.WriteLine($"USB设备扫描完成,耗时:{(DateTime.Now - startTime).TotalSeconds}秒");
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
                Debug.WriteLine(ex.Message);
            }
        }

        public async void OnFileChanged(object sender, FileSystemEventArgs e)
        {
            if (!await scanSemaphore.WaitAsync(0) || !AppViewModel.IsFolderWatchEnabled)
            {
                return;
            }
            try
            {
                App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                {
                    AppViewModel.ProcessRingVisibility = Visibility.Visible;
                });
                await AutoRescanService.AutoScan();
                App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                {
                    AppViewModel.ProcessRingVisibility = Visibility.Collapsed;
                });
            }
            finally
            {
                scanSemaphore.Release();
            }
        }

        public async Task InsertPlayList(PlayList newPlaylist) {
            await _musicDatabaseService.InsertPlayList(newPlaylist);
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
                UpdatePlayBar(AppViewModel.CurrentPlayingMusic);
                AppViewModel.LoadLyricsToUI();
            }
        }       

        public async void UpdatePlayBar(Music music)
        {
            AppViewModel.LyricPageBackgroundData = await ToolUtils.GetRawImage(music);
            BitmapImage DetailCover = await ToolUtils.ConvertByteArrayToBitmapImage(AppViewModel.LyricPageBackgroundData);
            App.MainWindow.DispatcherQueue.TryEnqueue(() =>
            {
                AppViewModel.MusicInfo = $"{music.Extension} {music.SampleRate}Hz {music.BitDepth}bit {music.BitRate}kbps";
                AppViewModel.MusicDetailCover = DetailCover;
            });
            SystemMediaControlsService.UpdateSystemMediaControlsState();
            SystemMediaControlsService.UpdateTimelineProperties(TimeSpan.Zero, music.Duration);
            _ = SystemMediaControlsService.UpdateMediaInfo(music.Title, music.Author, music.Album, AppViewModel.LyricPageBackgroundData);           
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
                MusicBrowsePage.PlayMusic(AppViewModel.CurrentPlayingList[index - 1]);
            }
            else if (index == 0 && AppViewModel.CurrentPlayingList.Count > 1)
            {
                MusicBrowsePage.PlayMusic(AppViewModel.CurrentPlayingList[AppViewModel.CurrentPlayingList.Count - 1]);

            }
        }


        [RelayCommand]
        private void OnStopButtonChanged()
        {
            MusicPlaybackService.MusicEnd();
            AppViewModel.ProgressSlider = 0;
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
            AppViewModel.ProgressSlider = await MusicPlaybackService.AdjustPlaybackPosition(seconds);
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
        private void PlayDetailButtonVisibleChanged() {
            AppViewModel.IsPlayDetailButtonVisible = !AppViewModel.IsPlayDetailButtonVisible;
        }

        private void OnSelectionChanged()
        {
            int currentSelectedIndex = GetSelectorBarItemIndex(SelectedPage);
            AppData.CurrentPage = typeof(SongListPage);
            switch (SelectedPage.Name)
            {
                case "Song":
                    AppViewModel.PageType = "song";
                    AppData.CurrentPage = typeof(SongListPage);
                    break;
                case "Album":
                    if (AppViewModel.CurrentAlbumObj is not null && !string.IsNullOrEmpty(AppViewModel.CurrentAlbumObj.Album))
                    {
                        AppViewModel.PageType = "album";
                        AppData.CurrentPage = typeof(SongCollectionPage);
                    }
                    else
                    {
                        AppViewModel.PageType = "albumBrowse";
                        AppData.CurrentPage = typeof(AlbumPage);
                    }
                    break;
                case "Artist":
                    if (AppViewModel.CurrentArtistObj is not null && !string.IsNullOrEmpty(AppViewModel.CurrentArtistObj.Author))
                    {
                        AppViewModel.PageType = "artist";
                        AppData.CurrentPage = typeof(SongArtistListPage);
                    }
                    else
                    {
                        AppViewModel.PageType = "artistBrowse";
                        AppData.CurrentPage = typeof(ArtistPage);
                    }
                    break;
                case "Folder":
                    if (AppViewModel.CurrentFolderObj is not null && !string.IsNullOrEmpty(AppViewModel.CurrentFolderObj.LastLevelFolderPath))
                    {
                        AppViewModel.PageType = "folder";
                        AppData.CurrentPage = typeof(SongFolderListPage);
                    }
                    else
                    {
                        AppViewModel.PageType = "folderBrowse";
                        AppData.CurrentPage = typeof(FolderBrowsePage);
                    }
                    break;
                case "Favourite":
                    AppViewModel.PageType = "favourite";
                    AppData.CurrentPage = typeof(FavouritePlayListPage);
                    break;
                case "PlayList":
                    if (AppViewModel.CurrentPlayList is not null)
                    {
                        AppViewModel.PageType = "playlist";
                        AppViewModel.CurrentPlayListId = AppViewModel.CurrentPlayList.Id;
                        AppData.CurrentPage = typeof(PlayListSongPage);
                    }
                    else
                    {
                        AppViewModel.PageType = "playlistBrowse";
                        AppData.CurrentPage = typeof(PlayListPage);
                    }
                    break;
            }
            var slideNavigationTransitionEffect = currentSelectedIndex - PreviousSelectedIndex > 0 ? SlideNavigationTransitionEffect.FromRight : SlideNavigationTransitionEffect.FromLeft;
            MusicBrowsePage.NavigatePage(AppData.CurrentPage, new SlideNavigationTransitionInfo() { Effect = slideNavigationTransitionEffect }, AppViewModel.SlideAnimationTime);
            PreviousSelectedIndex = currentSelectedIndex;
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
    }
}
