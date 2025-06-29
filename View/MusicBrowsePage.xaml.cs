using Microsoft.Extensions.DependencyInjection;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
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
using WinUIMusicPlayer.Services.NavigationService;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.View.SubView;
using WinUIMusicPlayer.ViewModel;
using static WinUIMusicPlayer.Utils.ToolUtils;
using AppWindow = Microsoft.UI.Windowing.AppWindow;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUIMusicPlayer.View
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MusicBrowsePage : Page
    {
        public MusicPlaybackService musicPlaybackService;
        public MainWindow mainWindow;
        public string paramName = "defualt";
        public string pageType = "MusicBrowsePage";
        public System.Type currentPage = typeof(SongListPage);
        private bool isMouseOverVolumeSlider = false;
        public string currentAlbumName;
        public string currentArtistName;
        public string currentFolderName;
        public PlayList currentPlayList;
        public int currentPlayListId;
        private bool isMuted = false;
        private DispatcherTimer typingTimer;
        private SystemMediaControlsService systemMediaControlsService;
        private bool isFullScreen = false;
        private AppWindow appWindow;
        private WindowId windowId;
        private bool isMouseOverProgressBar = false;
        private NotificationService notificationService;
        public EventHandler refreshSong;
        public EventHandler refreshPage;
        public EventHandler<PlayList> addPlayListEvent;
        public int previousSelectedIndex = 0;
        private bool isInPlayingDetailMode = false;
        private ObservableCollection<LyricLine> _uiLyrics = new ObservableCollection<LyricLine>();
        private DeviceWatcher deviceWatcher;
        private List<FileSystemWatcher> watchers = new List<FileSystemWatcher>();
        private readonly SemaphoreSlim scanSemaphore = new SemaphoreSlim(1, 1);
        public event EventHandler clearUsbDeviceMusicList;
        public event EventHandler refreshUsbDeviceMusicList;
        private int _lastLyricIndex = -1;
        private EqualizerDialog equalizerDialog;
        private CanvasControl _spectrumCanvas;
        private float[] _spectrumData = new float[16];
        private readonly object _lockObject = new object(); // 锁对象
        private bool isSearching = false;
        private string lastSearchText = string.Empty;
        private bool isInitialized = false;
        private readonly INavigationService _navigationService;
        public MusicBrowseViewModel ViewModel { get; }
        public MusicBrowsePage(MusicPlaybackService musicPlaybackService,
            NotificationService notificationService, 
            MusicBrowseViewModel viewModel,
            SystemMediaControlsService systemMediaControlsService
            )
        {
            isInitialized = false;            
            this.InitializeComponent();
            ViewModel = viewModel;
            ViewModel.SetMusicService(musicPlaybackService);
            ViewModel.SetMusicBrowsePage(this);
            DataContext = this;
            var navigationServiceFactory = App.Services.GetRequiredService<INavigationServiceFactory>();
            _navigationService = navigationServiceFactory.CreateNavigationService(ContentFrame);
            _navigationService.ContentFrame = ContentFrame;
            _navigationService.RegisterPage<FavouritePlayListPage>();
            _navigationService.RegisterPage<SongCollectionPage>();
            _navigationService.RegisterPage<AlbumPage>();
            _navigationService.RegisterPage<ArtistPage>();
            _navigationService.RegisterPage<FolderBrowsePage>();
            _navigationService.RegisterPage<PlayListPage>();
            _navigationService.RegisterPage<PlayListSongPage>();
            _navigationService.RegisterPage<SongListPage>();
            this.musicPlaybackService = musicPlaybackService;
            this.systemMediaControlsService = systemMediaControlsService;
            InitializeDatabase();
            ProgressSlider.Loaded += ProgressSlider_Loaded;
            this.KeyDown += MusicBrowsePage_KeyDown;
            AppSettings.OutputSettingsChanged += AppSettings_OutputSettingsChanged!;
            mainWindow = (App.MainWindow as MainWindow);
            if (mainWindow != null)
            {
                mainWindow.WindowClosed += MainWindow_Closed;
                mainWindow.updateMusicList += MainWindow_updateMusicList;
                //mainWindow.playLastSong += PlayLastSong;
                //mainWindow.playStop += PlayNStop;
                //mainWindow.playNextSong += PlayNextSong;
                //mainWindow.changePlayMode += MainWindow_changePlayMode;
                mainWindow.updateSelectSection += MainWindow_updateSelectSection;
            }
            musicPlaybackService.playingMusic += MusicPlaybackService_playingMusic;
            musicPlaybackService.updatePlayTimeText += MusicPlaybackService_updatePlayTimeText;
            musicPlaybackService.updateProgressSliders += MusicPlaybackService_updateProgressSliders;
            musicPlaybackService.updateProgressMax += MusicPlaybackService_updateProgressMax;
            musicPlaybackService.showMessage += ShowMessage;
            musicPlaybackService.updatePlayPauseButton += MusicPlaybackService_updatePlayPauseButton;
            musicPlaybackService.updateCurrentLyricIndex += MusicPlaybackService_updateCurrentLyricIndex;
            //TODO 波形可视化
            //musicPlaybackService.updateSpectrumData += MusicPlaybackService_updateSpectrumData;
            LyricsListView.ItemsSource = _uiLyrics;
            equalizerDialog = new EqualizerDialog();
            equalizerDialog.EqualizerGainChanged += (s, frequency) =>
            {
                float feq= FrequencyMap[frequency];
                musicPlaybackService.SetEqualizerGain(feq, (float)AppSettings.equalizer[frequency]);
            };
            equalizerDialog.clearEqualizer += (s, e) =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (!AppSettings.IsEqualizerEnabled)
                    {
                        musicPlaybackService.ClearEqualizer();
                    }
                    else
                    {
                        musicPlaybackService.SetEqualizer();
                        musicPlaybackService.ToggleEqualizer();
                    }
                });                
            };
            this.notificationService = notificationService;
            InitializeTimer();            
            InitializeAppWindow();           
            StartWatchingUsbStorageDevices();
            StartWatchingFileFolder();
            OnFileChanged(null, null);
            //SelectBarItem(AppSettings.DefualtPlayList);
            isInitialized = true;
            //TODO 波形可视化
            //_spectrumCanvas = SpectrumCanvas; // 保存XAML中定义的CanvasControl
            //_spectrumCanvas.Draw += Canvas_Draw; // 注册绘制事件
            //_forceDrawTimer = new System.Timers.Timer(16);
            //_forceDrawTimer.Elapsed += (s, e) => _spectrumCanvas.Invalidate();           
        }

        private void MainWindow_updateSelectSection(object? sender, EventArgs e)
        {
            SelectBarItem(AppSettings.DefualtPlayList);
        }

        private void Canvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
        {
            // 添加调试信息
            float[] data;
            lock (_lockObject)
            {
                // 复制数据到局部变量，减少锁持有时间
                data = new float[_spectrumData.Length];
                Array.Copy(_spectrumData, data, _spectrumData.Length);
            }
            //Debug.WriteLine($"绘制时间：{DateTime.Now:HH:mm:ss.fff}, data: [{string.Join(", ", data)}]");
            var ds = args.DrawingSession;
            float width = (float)sender.ActualWidth;
            float height = (float)sender.ActualHeight;
            float barWidth = width / _spectrumData.Length * 0.8f;
            float spacing = width / _spectrumData.Length * 0.2f;

            // 优化的绘制代码（不使用CanvasGeometry）
            for (int i = 0; i < _spectrumData.Length; i++)
            {
                float barHeight = _spectrumData[i] * height;
                if (barHeight < 1) continue; // 跳过太小的条

                float x = i * (barWidth + spacing);
                float y = height - barHeight;

                // 直接绘制，最高效
                ds.FillRectangle(x, y, barWidth, barHeight, Colors.Blue);

                // 顶部高亮
                if (barHeight > 2)
                {
                    ds.FillRectangle(x, y, barWidth, 2, Colors.White);
                }
            }
        }

        private void MusicPlaybackService_updateSpectrumData(object? sender, float[] spectrumData)
        {
            lock (_lockObject)
            {
                Array.Copy(spectrumData, _spectrumData, spectrumData.Length);
                //Debug.WriteLine($"更新时间：{DateTime.Now:HH:mm:ss.fff}, data: [{string.Join(", ", _spectrumData)}]");
            }
        }


        //private void MainWindow_changePlayMode(object? sender, PlayMode mode)
        //{    
        //   AppData.PlayMode = mode;
        //   ViewModel.CurrentPlayMode = mode;
        //    //UpdatePlayModeIcon();
        //}

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

        private async void StartWatchingFileFolder()
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

                    // 开始监听
                    watcher.EnableRaisingEvents = true;

                    watchers.Add(watcher);
                }
            }
        }

        private async void OnFileChanged(object sender, FileSystemEventArgs e)
        {
            if (!await scanSemaphore.WaitAsync(0))
            {
                Debug.WriteLine("已经有扫描操作在进行，忽略此次事件");
                return;
            }
            try
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    ProcessingRing.Visibility = Visibility.Visible;
                });
                await AutoRescanService.AutoScan();
                DispatcherQueue.TryEnqueue(() =>
                {
                    ProcessingRing.Visibility = Visibility.Collapsed;
                });
            }
            finally
            {
                scanSemaphore.Release();
            }
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
        private void MusicPlaybackService_updateCurrentLyricIndex(object? sender, int currentIndex)
        {
            if (_lastLyricIndex == currentIndex || !isInPlayingDetailMode)
                return;
            this.DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    // 创建当前歌词的副本
                    for (int i = 0; i < _uiLyrics.Count; i++)
                    {
                        // 获取旧歌词
                        var lyric = _uiLyrics[i];
                        _uiLyrics[i].IsCurrent = (i == currentIndex);
                    }
                    // 滚动到当前歌词
                    if (currentIndex >= 0 && currentIndex < _uiLyrics.Count)
                    {
                        LyricsListView.ScrollIntoView(_uiLyrics[currentIndex]);
                        await Task.Delay(50);
                        // 获取目标项的容器
                        if (currentIndex >= 0 && currentIndex < _uiLyrics.Count)
                        {
                            var container = LyricsListView.ContainerFromItem(_uiLyrics[currentIndex]) as ListViewItem;
                            if (container != null)
                            {
                                // 计算需要滚动的位置，使项目居中
                                var scrollViewer = FindVisualChild<ScrollViewer>(LyricsListView);
                                if (scrollViewer != null)
                                {
                                    // 计算项目在ListView中的位置
                                    var transform = container.TransformToVisual(LyricsListView);
                                    var itemPosition = transform.TransformPoint(new Windows.Foundation.Point(0, 0));
                                    // 计算目标项目与ListView中心的偏移量
                                    var itemOffset = itemPosition.Y + (container.ActualHeight / 2);
                                    var listViewCenter = scrollViewer.ViewportHeight / 2;
                                    var scrollOffset = itemOffset - listViewCenter;
                                    scrollViewer.ChangeView(null, scrollViewer.VerticalOffset + scrollOffset, null, false);
                                }
                            }
                        }
                    }
                    _lastLyricIndex = currentIndex;
                }
                catch (Exception ex)
                {
                    notificationService.SendNotification(ToolUtils.GetString("Error"), $"{ToolUtils.GetString("UpdatingLyricsFailed")}: {ex.Message}");
                }
            });
        }

        private async void LoadLyricsToUI()
        {
            _lastLyricIndex = -1;
            _uiLyrics.Clear();
            // 设置播放服务中的歌词
            await musicPlaybackService.SetLyrics();
            // 解析歌词并添加到UI集合
            List<LyricLine> parsedLyrics = musicPlaybackService._lyrics;
            _uiLyrics.Clear();
            foreach (var lyric in parsedLyrics)
            {
                _uiLyrics.Add(lyric);
            }
        }

        private void PlayNextSong(object? sender, EventArgs e)
        {
            ViewModel.NextMusicButton_Click();
        }

        private void PlayNStop(object? sender, EventArgs e)
        {
            ViewModel.PlayButton_Click();
        }

        private void PlayLastSong(object? sender, EventArgs e)
        {
            ViewModel.LastMusicButton_Click();
        }

        public async void MainWindow_updateMusicList(object? sender, EventArgs e)
        {
            AppData.allSongs = await MusicDatabaseService.GetMusicListAsync();
            if (ContentFrame != null && ContentFrame.Content != null)
            {
                refreshPage?.Invoke(this, EventArgs.Empty);
                refreshSong?.Invoke(this, EventArgs.Empty);
            }
        }

        private void SelectBarItem(string name)
        {
            foreach (var item in selectPage.Items)
            {
                if (item is SelectorBarItem selectorBarItem && selectorBarItem.Tag.ToString() == name)
                {
                    selectPage.SelectedItem = selectorBarItem;
                    break;
                }
            }
        }

        private async void MainWindow_Closed(object? sender, EventArgs e)
        {
            await musicPlaybackService.DisposeAudio();
            if (mainWindow != null)
            {
                mainWindow.WindowClosed -= MainWindow_Closed;
                mainWindow.updateMusicList -= MainWindow_updateMusicList;
                //mainWindow.playLastSong -= PlayLastSong;
                //mainWindow.playStop -= PlayNStop;
                //mainWindow.playNextSong -= PlayNextSong;
            }
            musicPlaybackService.playingMusic -= MusicPlaybackService_playingMusic;
            musicPlaybackService.updatePlayTimeText -= MusicPlaybackService_updatePlayTimeText;
            musicPlaybackService.updateProgressSliders -= MusicPlaybackService_updateProgressSliders;
            musicPlaybackService.updateProgressMax -= MusicPlaybackService_updateProgressMax;
            musicPlaybackService.showMessage -= ShowMessage;
            musicPlaybackService.updatePlayPauseButton -= MusicPlaybackService_updatePlayPauseButton;
        }

        private void InitializeAppWindow()
        {
            appWindow = ToolUtils.GetAppWindowForCurrentWindow(App.MainWindow);
        }

        private void InitializeTimer()
        {
            typingTimer = new DispatcherTimer();
            typingTimer.Interval = TimeSpan.FromMilliseconds(300);
            typingTimer.Tick += TypingTimer_Tick;
        }

        //private void InitializeSystemMediaControls(SystemMediaControlsService systemMediaControlsService)
        //{
        //    this.systemMediaControlsService = systemMediaControlsService;

        //    // 订阅事件
        //    systemMediaControlsService.PlayRequested += (s, e) =>
        //    {
        //        this.DispatcherQueue.TryEnqueue(() =>
        //        {
        //            ViewModel.PlayButton_Click();
        //        });
        //    };

        //    systemMediaControlsService.PauseRequested += (s, e) =>
        //    {
        //        this.DispatcherQueue.TryEnqueue(() =>
        //        {
        //            ViewModel.PlayButton_Click();
        //        });
        //    };

        //    systemMediaControlsService.NextTrackRequested += (s, e) =>
        //    {
        //        this.DispatcherQueue.TryEnqueue(() =>
        //        {
        //            NextMusicButton_Click(null, null);
        //        });
        //    };

        //    systemMediaControlsService.PreviousTrackRequested += (s, e) =>
        //    {
        //        this.DispatcherQueue.TryEnqueue(() =>
        //        {
        //            LastMusicButton_Click(null, null);
        //        });
        //    };
        //}

        public void ShowTransmission()
        {
            ProcessingRing.Visibility = Visibility.Visible;
        }

        public void HideTransmission()
        {
            ProcessingRing.Visibility = Visibility.Collapsed;
        }

        private async Task ReadUsbDevice()
        {
            try
            {
                AppData.usbStorageDevices.Clear();
                AppData.usbStorageDevices = await UsbStorageDeviceReader.GetUsbStorageDevicesAsync();
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (AppData.usbStorageDevices.Count > 0)
                    {
                        UsbDeviceCombox.Visibility = Visibility.Visible;
                        UsbDeviceCombox.ItemsSource = AppData.usbStorageDevices;
                        UsbDeviceCombox.SelectedIndex = 0;
                    }
                    else
                    {
                        UsbDeviceCombox.Visibility = Visibility.Collapsed;
                        UsbDeviceCombox.ItemsSource = null;
                        UsbDeviceCombox.SelectedIndex = -1;
                    }
                });
            }
            catch (Exception ex)
            {
                UsbDeviceCombox.Visibility = Visibility.Collapsed;
                System.Diagnostics.Debug.WriteLine($"读取USB设备失败: {ex.Message}");
            }
        }

        private async void UsbDeviceCombox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            AppData.usbStorageDevice = null;
            AppData.musicOnUsbDevice.Clear();
            clearUsbDeviceMusicList?.Invoke(this, EventArgs.Empty);
            if (UsbDeviceCombox.SelectedItem is UsbStorageDevice usbStorageDevice)
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
                        notificationService.SendNotification(ToolUtils.GetString("Error"), ToolUtils.GetString("NoMusicInUSBDevice"));
                    }
                }
                refreshUsbDeviceMusicList?.Invoke(this, EventArgs.Empty);
            }
        }

        public void DisableBackButton()
        {
            if (currentPage == typeof(SongCollectionPage) || currentPage == typeof(PlayListSongPage))
            {
                BackButton.IsEnabled = true;
            }
            else
            {
                BackButton.IsEnabled = false;
            }
        }

        private void MusicPlaybackService_updateProgressMax(object? sender, double max)
        {
            this.DispatcherQueue.TryEnqueue(() =>
            {
                if (ProgressSlider != null)
                {
                    ProgressSlider.Maximum = max;
                }
            });
        }

        private void MusicPlaybackService_updatePlayPauseButton(object? sender, string e)
        {
            ViewModel.UpdatePlayPauseButtonIcon();
        }

        private async void ShowMessage(object? sender, string message)
        {
            try
            {
                notificationService.SendNotification(ToolUtils.GetString("Error"), message);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"{ToolUtils.GetString("Error")}: {ex.Message}");
            }
        }

        private void MusicPlaybackService_updateProgressSliders(object? sender, double value)
        {
            try
            {
                if (DispatcherQueue != null)
                {
                    DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
                    {
                        if (ProgressSlider != null)
                        {
                            ProgressSlider.Value = value;
                        }
                    });
                }
            }
            catch (NullReferenceException ex)
            {
                Debug.WriteLine($"更新进度条失败: {ex.Message}");
            }
        }

        private void MusicPlaybackService_updatePlayTimeText(object? sender, string time)
        {
            try
            {
                if (DispatcherQueue != null)
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (PlayTimeTextBlock != null)
                        {
                            PlayTimeTextBlock.Text = time;
                        }
                    });
                }
            }
            catch (NullReferenceException ex)
            {
                Debug.WriteLine($"更新进度条失败: {ex.Message}");
            }
        }

        private async void MusicPlaybackService_playingMusic(object? sender, Music music)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                _ = PlayMusic(music);
            });            
        }

        public async void LoadPlayListSong(PlayList playList)
        {
            pageType = "playlist";
            paramName = playList.Name;
            currentPlayList = playList;
            currentPlayListId = playList.Id;
            currentPage = typeof(PlayListSongPage);
            _navigationService.Navigate(currentPage, this, new DrillInNavigationTransitionInfo());
        }

        public async void LoadAlbumMusic(string Album)
        {
            pageType = "album";
            paramName = Album;
            currentAlbumName = Album;
            currentPage = typeof(SongCollectionPage);
            _navigationService.Navigate(currentPage, this, new DrillInNavigationTransitionInfo());
        }

        public void SelectBarAlbum(string Album)
        {
            pageType = "album";
            paramName = Album;
            currentAlbumName = Album;
            currentPage = typeof(SongCollectionPage);
            if (ContentFrame != null && ContentFrame.Content != null)
            {
                if (ContentFrame.Content is AlbumPage)
                {
                    _navigationService.Navigate(currentPage, this, new DrillInNavigationTransitionInfo());
                    //ContentFrame.Navigate(currentPage, this, new DrillInNavigationTransitionInfo());
                }
                else
                {
                    SelectBarItem("album");
                }
            }
        }

        public async void LoadArtistMusic(string artist)
        {
            pageType = "artist";
            paramName = artist;
            currentArtistName = artist;
            currentPage = typeof(SongCollectionPage);
            _navigationService.Navigate(currentPage, this, new DrillInNavigationTransitionInfo());
            //ContentFrame.Navigate(currentPage, this, new DrillInNavigationTransitionInfo());
        }

        public void SelectBarArtist(string artist)
        {
            pageType = "artist";
            paramName = artist;
            currentArtistName = artist;
            currentPage = typeof(SongCollectionPage);
            if (ContentFrame != null && ContentFrame.Content != null)
            {
                if (ContentFrame.Content is ArtistPage)
                {
                    _navigationService.Navigate(currentPage, this, new DrillInNavigationTransitionInfo());
                    //ContentFrame.Navigate(currentPage, this, new DrillInNavigationTransitionInfo());
                }
                else
                {
                    SelectBarItem("artist");
                }
            }
        }

        public async void LoadFolderMusic(string folder)
        {
            pageType = "folder";
            paramName = folder;
            currentFolderName = folder;
            currentPage = typeof(SongCollectionPage);
            _navigationService.Navigate(currentPage, this, new DrillInNavigationTransitionInfo());
            //ContentFrame.Navigate(currentPage, this, new DrillInNavigationTransitionInfo());
        }


        public async Task AddToFavourite(Music music)
        {
            music.IsFavorite = !music.IsFavorite;
            await MusicDatabaseService.AddToFavourite(music, musicPlaybackService.currentPlayingMusic);
            if (musicPlaybackService.currentPlayingMusic != null)
            {
                if (musicPlaybackService.currentPlayingMusic.Id == music.Id)
                {
                    musicPlaybackService.currentPlayingMusic.IsFavorite = music.IsFavorite;
                    ViewModel.CurrentPlayingMusic.IsFavorite = music.IsFavorite;
                    //((FontIcon)PlayBarFavouriteButton.Content).Glyph = music.IsFavorite ? "\ueb52" : "\ueb51";
                }
            }
        }


        private async void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            typingTimer.Stop();
            typingTimer.Start();
            //GC.Collect();
            //GC.WaitForPendingFinalizers();
            //if (string.IsNullOrEmpty(SearchTextBox.Text))
            //{
            //    AppData.searchText = SearchTextBox.Text;
            //    if (ContentFrame != null && ContentFrame.Content != null)
            //    {
            //        refreshPage?.Invoke(this, EventArgs.Empty);
            //        refreshSong?.Invoke(this, EventArgs.Empty);
            //    }
            //}
        }

        private async void TypingTimer_Tick(object sender, object e)
        {
            typingTimer.Stop();

            // 防止重入
            if (isSearching)
            {
                return;
            }

            var currentText = SearchTextBox.Text;

            // 防止重复搜索
            if (currentText == lastSearchText)
            {
                return;
            }

            isSearching = true;
            lastSearchText = currentText;

            try
            {
                AppData.searchText = currentText;
                if (ContentFrame?.Content != null)
                {
                    refreshPage?.Invoke(this, EventArgs.Empty);
                    refreshSong?.Invoke(this, EventArgs.Empty);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"搜索执行失败: {ex.Message}");
            }
            finally
            {
                isSearching = false;
            }
        }

        private void MusicBrowsePage_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            switch (e.Key)
            {
                case Windows.System.VirtualKey.Left:
                    AdjustPlaybackPosition(-5);
                    e.Handled = true;
                    break;
                case Windows.System.VirtualKey.Right:
                    AdjustPlaybackPosition(5);
                    e.Handled = true;
                    break;
            }
        }

        private void AdjustPlaybackPosition(int seconds)
        {
            ProgressSlider.Value = musicPlaybackService.AdjustPlaybackPosition(seconds);
        }

        private void AppSettings_OutputSettingsChanged(object sender, EventArgs e)
        {
            musicPlaybackService.ChangingSetting();
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (ContentFrame.Content is SongCollectionPage)
            {
                switch (pageType)
                {
                    case "album":
                        currentPage = typeof(AlbumPage);
                        _navigationService.Navigate(typeof(AlbumPage), this, new DrillInNavigationTransitionInfo());
                        //ContentFrame.Navigate(typeof(AlbumPage), this, new DrillInNavigationTransitionInfo());
                        break;
                    case "artist":
                        currentPage = typeof(ArtistPage);
                        _navigationService.Navigate(typeof(ArtistPage), this, new DrillInNavigationTransitionInfo());
                        //ContentFrame.Navigate(typeof(ArtistPage), this, new DrillInNavigationTransitionInfo());
                        break;
                    case "folder":
                        currentPage = typeof(AlbumPage);
                        _navigationService.Navigate(typeof(FolderBrowsePage), this, new DrillInNavigationTransitionInfo());
                        //ContentFrame.Navigate(typeof(FolderBrowsePage), this, new DrillInNavigationTransitionInfo());
                        break;
                    default:
                        break;
                }
            }
            if (ContentFrame.Content is PlayListSongPage)
            {
                currentPage = typeof(PlayListPage);
                _navigationService.Navigate(typeof(PlayListPage), this, new DrillInNavigationTransitionInfo());
                //ContentFrame.Navigate(typeof(PlayListPage), this, new DrillInNavigationTransitionInfo());
            }
            DisableBackButton();
        }

        private void ResetNavigationButtons()
        {
            foreach (var item in selectPage.Items)
            {
                if (item is SelectorBarItem selectorBarItem)
                {
                    selectorBarItem.FontSize = 20;
                }
            }
        }
        private void SelectPage_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
        {
            DateTime startTime = DateTime.Now;
            ResetNavigationButtons();
            SelectorBarItem selectedItem = sender.SelectedItem;
            selectedItem.FontSize = 26;
            int currentSelectedIndex = sender.Items.IndexOf(selectedItem);
            currentPage = typeof(SongListPage);
            switch (selectedItem.Name)
            {
                case "Song":
                    currentPage = typeof(SongListPage);
                    break;
                case "Album":
                    if (!string.IsNullOrEmpty(currentAlbumName))
                    {
                        pageType = "album";
                        paramName = currentAlbumName;
                        currentPage = typeof(SongCollectionPage);
                    }
                    else
                    {
                        currentPage = typeof(AlbumPage);
                    }
                    break;
                case "Artist":
                    if (!string.IsNullOrEmpty(currentArtistName))
                    {
                        pageType = "artist";
                        paramName = currentArtistName;
                        currentPage = typeof(SongCollectionPage);
                    }
                    else
                    {
                        currentPage = typeof(ArtistPage);
                    }
                    break;
                case "Folder":
                    if (!string.IsNullOrEmpty(currentFolderName))
                    {
                        pageType = "folder";
                        paramName = currentFolderName;
                        currentPage = typeof(SongCollectionPage);
                    }
                    else
                    {
                        currentPage = typeof(FolderBrowsePage);
                    }
                    break;
                case "Favourite":
                    currentPage = typeof(FavouritePlayListPage);
                    break;
                case "PlayList":
                    if (currentPlayList != null)
                    {
                        pageType = "playlist";
                        paramName = currentPlayList.Name;
                        currentPlayListId = currentPlayList.Id;
                        currentPage = typeof(PlayListSongPage);
                    }
                    else
                    {
                        currentPage = typeof(PlayListPage);
                    }
                    break;
            }
            var slideNavigationTransitionEffect = currentSelectedIndex - previousSelectedIndex > 0 ? SlideNavigationTransitionEffect.FromRight : SlideNavigationTransitionEffect.FromLeft;
            _navigationService.Navigate(currentPage, this, new SlideNavigationTransitionInfo() { Effect = slideNavigationTransitionEffect });
            previousSelectedIndex = currentSelectedIndex;
            DisableBackButton();
        }

        private async Task LoadPlayState()
        {
            //var volume = AppData.Volume;
            musicPlaybackService.lastPlayedMusicId = AppData.LastPlayedMusicId;
            musicPlaybackService.volume = AppData.Volume;
            VolumeSlider.Value = musicPlaybackService.volume * 100;
            ViewModel.Volume = (int)VolumeSlider.Value;
            musicPlaybackService.currentPlayingMusic = await MusicDatabaseService.LoadCurrentPlayingMusic(AppData.LastPlayedMusicId);
            ViewModel.CurrentPlayingMusic = musicPlaybackService.currentPlayingMusic;
            if (musicPlaybackService.currentPlayingMusic != null)
            {
                UpdatePlayBar(musicPlaybackService.currentPlayingMusic);
                //await LoadCover(musicPlaybackService.currentPlayingMusic);                
                LoadLyricsToUI();
            }
            //UpdatePlayModeIcon();
            musicPlaybackService.isInitializing = false;
        }
        private async void AddPlayList_Click(object sender, RoutedEventArgs e)
        {
            ContentDialog contentDialog = new ContentDialog
            {
                Title = ToolUtils.GetString("FlyoutAddToPlaylist"),
                Content = new Microsoft.UI.Xaml.Controls.TextBox { PlaceholderText = ToolUtils.GetString("EnterPlaylistName") },
                PrimaryButtonText = ToolUtils.GetString("PrimaryButton"),
                CloseButtonText = ToolUtils.GetString("CloseButton"),
                XamlRoot = this.XamlRoot
            };
            contentDialog.RequestedTheme = AppSettings.elementTheme;
            ContentDialogResult result = await contentDialog.ShowAsync();

            if (result == ContentDialogResult.Primary)
            {
                Microsoft.UI.Xaml.Controls.TextBox textBox = (Microsoft.UI.Xaml.Controls.TextBox)contentDialog.Content;
                string playlistName = textBox.Text;
                if (!string.IsNullOrEmpty(playlistName))
                {
                    PlayList newPlaylist = new PlayList { Name = playlistName };
                    await MusicDatabaseService.InsertPlayList(newPlaylist);
                    addPlayListEvent?.Invoke(this, newPlaylist);
                    //await LoadPlayList();
                }
            }
        }

        //private void UpdateIconPlayModeMenuFlyout() {
        //    mainWindow.UpdateAppNotifyIconControl();
        //}

        private async void CurrentPlayListButton_Click(object sender, RoutedEventArgs e)
        {
            if (musicPlaybackService.currentPlayingList != null)
            {
                CurrentPlayListView.ItemsSource = musicPlaybackService.currentPlayingList;
                CurrentPlayListTeachingTip.IsOpen = true;
                UpdateCurrentPlayList();
            }
        }

        private void UpdateCurrentPlayList()
        {
            if (musicPlaybackService.currentPlayingList != null)
            {
                if (musicPlaybackService.currentPlayingMusic != null)
                {
                    var selectedMusic = musicPlaybackService.currentPlayingList.FirstOrDefault(music =>
                    music.Id == musicPlaybackService.currentPlayingMusic.Id);

                    if (selectedMusic != null)
                    {                        
                        _ = Task.Delay(100).ContinueWith(_ =>
                        {
                            DispatcherQueue.TryEnqueue(() =>
                            {
                                CurrentPlayListView.SelectedItem = selectedMusic;
                                CurrentPlayListView.ScrollIntoView(selectedMusic);
                            });
                        }); 
                    }
                }                
            }
        }

        private async void CurrentPlayListView_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
        {
            var selectedMusic = CurrentPlayListView.SelectedItem as Music;
            if (selectedMusic != null)
            {
                await PlayMusic(selectedMusic);
            }
        }

        private void FullScreenButton_Click(object sender, RoutedEventArgs e)
        {
            if (appWindow != null)
            {
                if (isFullScreen)
                {
                    appWindow.SetPresenter(AppWindowPresenterKind.Default);
                    FullScreenIcon.Glyph = "\uE740";
                }
                else
                {
                    appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
                    FullScreenIcon.Glyph = "\uE73F";
                }
                isFullScreen = !isFullScreen;
            }

        }

        //private async void NextMusicButton_Click(object sender, RoutedEventArgs e)
        //{
        //    musicPlaybackService.isManualSelect = true;
        //    musicPlaybackService.PlayNextTrack();
        //    musicPlaybackService.isManualSelect = false;
        //}

        //private async void LastMusicButton_Click(object sender, RoutedEventArgs e)
        //{
        //    musicPlaybackService.isManualSelect = true;
        //    await PlayLastTrack();
        //    musicPlaybackService.isManualSelect = false;
        //}

        private void FastForwardButton_Click(object sender, RoutedEventArgs e)
        {
            AdjustPlaybackPosition(5);
        }

        private void FastBackwardButton_Click(object sender, RoutedEventArgs e)
        {
            AdjustPlaybackPosition(-5);
        }

        private async Task PlayLastTrack()
        {
            int index = musicPlaybackService.currentPlayingList.IndexOf(musicPlaybackService.currentPlayingMusic);
            if (index > 0)
            {
                await PlayMusic(musicPlaybackService.currentPlayingList[index - 1]);
            }
            else if (index == 0 && musicPlaybackService.currentPlayingList.Count > 1)
            {
                await PlayMusic(musicPlaybackService.currentPlayingList[musicPlaybackService.currentPlayingList.Count - 1]);

            }
        }

        private async void InitializeDatabase()
        {
            try
            {
                musicPlaybackService.isInitializing = true;
                await LoadPlayState();
                PlayTimeTextBlock.Text = "00:00/00:00";
                musicPlaybackService.OutputDeviceChange();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"错误: {ex.Message}");
                notificationService.SendNotification(ToolUtils.GetString("Error"), ex.Message);
            }
        }

        //private void PlayButton_Click(object sender, RoutedEventArgs e)
        //{
        //    //if (AppSettings.isPlaying)
        //    //{
        //    //    _forceDrawTimer.Stop();
        //    //}
        //    //else {
        //    //    _forceDrawTimer.Start();
        //    //}
        //    musicPlaybackService.PlayButton();
        //    UpdatePlayPauseButtonIcon();
        //    systemMediaControlsService.UpdateSystemMediaControlsState();
        //}

        //private void UpdatePlayPauseButtonIcon()
        //{
        //    DispatcherQueue.TryEnqueue(() =>
        //    {
        //        //if (AppSettings.isPlaying)
        //        //{
        //        //    ((FontIcon)PlayPauseButton.Content).Glyph = "\uE769"; // 暂停图标
        //        //}
        //        //else
        //        //{
        //        //    ((FontIcon)PlayPauseButton.Content).Glyph = "\uE768"; // 播放图标
        //        //}
        //        if (mainWindow != null)
        //        {
        //            mainWindow.UpdateTaskbarIcon();
        //            mainWindow.UpdateIconControl();
        //        }
        //    });
        //}

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            //_forceDrawTimer.Stop();
            musicPlaybackService.StopPlaying();
            ViewModel.UpdatePlayPauseButtonIcon();
            musicPlaybackService.Reset();
            ProgressSlider.Value = 0;
        }

        private async void PlayBarFavouriteButton_Click(object sender, RoutedEventArgs e)
        {
            if (musicPlaybackService.currentPlayingMusic != null)
            {
                //((FontIcon)PlayBarFavouriteButton.Content).Glyph = !musicPlaybackService.currentPlayingMusic.IsFavorite ? "\ueb52" : "\ueb51";
                await AddToFavourite(musicPlaybackService.currentPlayingMusic);
                AppData.allSongs = await MusicDatabaseService.GetMusicListAsync();
                NotifySubPageUpdateFavouriteState();
            }
        }

        private void NotifySubPageUpdateFavouriteState()
        {
            if (ContentFrame.Content is SongCollectionPage)
            {
                var songCollectionPage = ContentFrame.Content as SongCollectionPage;
                songCollectionPage.UpdateFavouriteMusic(musicPlaybackService.currentPlayingMusic);
            }
            if (ContentFrame.Content is SongListPage)
            {
                var songListPage = ContentFrame.Content as SongListPage;
                songListPage.UpdateFavouriteMusic(musicPlaybackService.currentPlayingMusic);
            }
            if (ContentFrame.Content is FavouritePlayListPage)
            {
                var favouritePlayListPage = ContentFrame.Content as FavouritePlayListPage;
                favouritePlayListPage.UpdateFavouriteMusic(musicPlaybackService.currentPlayingMusic);
            }
        }

        private async void UpdatePlayBar(Music music)
        {
            BitmapImage DetailCover = await GetImageFromMusic(music, 0);
            DispatcherQueue.TryEnqueue(() =>
            {
                ViewModel.MusicInfo = $"{music.Extension} {music.SampleRate}Hz {music.BitDepth}bit {music.BitRate}kbps";                   
                ViewModel.MusicDetailCover = DetailCover;
            });            
            systemMediaControlsService.UpdateSystemMediaControlsState();
            await Task.Delay(300);
            if (isInPlayingDetailMode)
            {
                _ = systemMediaControlsService.UpdateMediaInfo(music.Title, music.Author, music.Album, PlayingDetailAlbumCoverImage);
            }
            else
            {
                _ = systemMediaControlsService.UpdateMediaInfo(music.Title, music.Author, music.Album, AlbumCoverImage);
            }
        }

        private void UpdateViewList(Music music)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                var songListPage = ContentFrame.Content as SongListPage;
                var songCollectionPage = ContentFrame.Content as SongCollectionPage;
                var FavouritePlayListPage = ContentFrame.Content as FavouritePlayListPage;
                var playListSongPage = ContentFrame.Content as PlayListSongPage;
                if (songListPage != null)
                {
                    songListPage.UpdateMusicListView();
                }
                if (songCollectionPage != null)
                {
                    songCollectionPage.UpdateMusicListView();
                }
                if (FavouritePlayListPage != null)
                {
                    FavouritePlayListPage.UpdateMusicListView();
                }
                if (playListSongPage != null)
                {
                    playListSongPage.UpdateMusicListView();
                }
            });
        }

        public async Task PlayMusic(Music music, TimeSpan currentPos = new TimeSpan(), bool isSettingChanged = false)
        {
            try
            {
                //_forceDrawTimer.Start();
                musicPlaybackService.currentPlayingMusic = music;
                ViewModel.CurrentPlayingMusic = music;
                LoadLyricsToUI();
                UpdatePlayBar(ViewModel.CurrentPlayingMusic);
                UpdateViewList(music);
                UpdateCurrentPlayList();
                await musicPlaybackService.PlayMusic(music, currentPos, isSettingChanged);
                
            }
            catch (Exception ex)
            {
                notificationService.SendNotification(ToolUtils.GetString("Error"), ex.Message);
            }
        }

        private void SortByComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count > 0)
            {
                ComboBoxItem selectedItem = (ComboBoxItem)e.AddedItems[0];
                AppData.sortOrder = selectedItem.Tag.ToString();
                if (ContentFrame != null && ContentFrame.Content != null)
                {
                    if (ContentFrame.Content is SongCollectionPage)
                    {
                        var page = ContentFrame.Content as SongCollectionPage;
                        page.SortMusicList(AppData.sortOrder, pageType);
                    }
                    if (ContentFrame.Content is SongListPage)
                    {
                        var page = ContentFrame.Content as SongListPage;
                        if (page != null)
                        {
                            page.SortMusicList(AppData.sortOrder);
                        }
                    }
                    if (ContentFrame.Content is FavouritePlayListPage)
                    {
                        var page = ContentFrame.Content as FavouritePlayListPage;
                        if (page != null)
                        {
                            page.SortMusicList(AppData.sortOrder);
                        }
                    }
                    if (ContentFrame.Content is AlbumPage)
                    {
                        var page = ContentFrame.Content as AlbumPage;
                        if (page != null)
                        {
                            page.SortMusicList(AppData.sortOrder);
                        }
                    }
                    if (ContentFrame.Content is ArtistPage)
                    {
                        var page = ContentFrame.Content as ArtistPage;
                        if (page != null)
                        {
                            page.SortMusicList(AppData.sortOrder);
                        }
                    }
                    if (ContentFrame.Content is FolderBrowsePage)
                    {
                        var page = ContentFrame.Content as FolderBrowsePage;
                        if (page != null)
                        {
                            page.SortMusicList(AppData.sortOrder);
                        }
                    }
                    if (ContentFrame.Content is PlayListSongPage)
                    {
                        var page = ContentFrame.Content as PlayListSongPage;
                        if (page != null)
                        {
                            page.SortMusicList(AppData.sortOrder);
                        }
                    }
                }
            }
        }

        private void VolumeSliderIconButton_Click(object sender, RoutedEventArgs e)
        {
            isMuted = !isMuted;            
            if (isMuted)
            {
                ViewModel.Volume =0;
                musicPlaybackService.volume = 0;
                //if (musicPlaybackService.wasapiOut != null)
                //{
                //    musicPlaybackService.wasapiOut.Volume = 0;
                //}
                if (musicPlaybackService.waveChannel != null)
                {
                    musicPlaybackService.waveChannel.Volume = 0;
                }
            }
            else
            {
                //VolumeIconChange((int)VolumeSlider.Value);                
                ViewModel.Volume = (int)VolumeSlider.Value;
                musicPlaybackService.volume = (float)VolumeSlider.Value / 100;
                //if (musicPlaybackService.wasapiOut != null)
                //{
                //    musicPlaybackService.wasapiOut.Volume = (float)VolumeSlider.Value / 100;
                //}
                if (musicPlaybackService.waveChannel != null)
                {
                    musicPlaybackService.waveChannel.Volume = AppSettings.isDsd ? ((float)VolumeSlider.Value / 100) * (float)Math.Pow(10, AppSettings.dsdGain / 20.0) : (float)VolumeSlider.Value / 100;
                }
            }
        }

        private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (isInitialized) {
                musicPlaybackService.volume = (float)e.NewValue / 100;
                //if (musicPlaybackService.wasapiOut != null)
                //{
                //    musicPlaybackService.wasapiOut.Volume = musicPlaybackService.volume;
                //}
                if (musicPlaybackService.waveChannel != null)
                {
                    musicPlaybackService.waveChannel.Volume = AppSettings.isDsd ? musicPlaybackService.volume * (float)Math.Pow(10, AppSettings.dsdGain / 20.0) : musicPlaybackService.volume;
                }
                ViewModel.Volume = (int)e.NewValue;
                //_ = musicPlaybackService.SavePlayState();
            }
        }

        private void VolumeSlider_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            isMouseOverVolumeSlider = true;
        }

        private void VolumeSlider_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            isMouseOverVolumeSlider = false;
        }

        private void ProgressSlider_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            isMouseOverProgressBar = true;
        }

        private void ProgressSlider_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            isMouseOverProgressBar = false;
        }
        private void ProgressSlider_Loaded(object sender, RoutedEventArgs e)
        {
            var thumb = FindVisualChild<Thumb>(ProgressSlider);
            if (thumb != null)
            {
                thumb.DragStarted += Thumb_DragStarted;
                thumb.DragCompleted += Thumb_DragCompleted;
            }
        }

        private void VolumeSlider_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            if (isMouseOverVolumeSlider)
            {
                var delta = e.GetCurrentPoint(VolumeSlider).Properties.MouseWheelDelta;
                if (delta > 0)
                {
                    AdjustVolume(1);
                }
                else if (delta < 0)
                {
                    AdjustVolume(-1);
                }
                e.Handled = true;
            }
        }

        private void AdjustVolume(int delta)
        {
            double newVolume = VolumeSlider.Value + delta;
            newVolume = Math.Max(0, Math.Min(newVolume, 100));
            VolumeSlider.Value = newVolume;
        }

        private void AuthorTextBlock_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            if (sender is TextBlock textBlock)
            {
                string artist = textBlock.Text;
                SelectBarArtist(artist);
            }
        }

        private void AlbumTextBlock_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            if (sender is TextBlock textBlock)
            {
                string albumName = textBlock.Text;
                SelectBarAlbum(albumName);
            }
        }

        private void Thumb_DragStarted(object sender, DragStartedEventArgs e)
        {
            musicPlaybackService.isUserDraggingProgressSlider = true;
        }

        private void Thumb_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            musicPlaybackService.isUserDraggingProgressSlider = false;

            if (AppSettings.isPlaying)
            {
                if (musicPlaybackService.waveChannel != null)
                {
                   
                    //if (AppSettings.isDsd)
                    //{
                    //    double newPosition = Math.Max(0, Math.Min(ProgressSlider.Value, musicPlaybackService.adapter.TotalTime.TotalSeconds));
                    //    musicPlaybackService.adapter.SetCurrentTime(TimeSpan.FromSeconds(newPosition));
                    //}
                    //else
                    //{
                        double newPosition = Math.Max(0, Math.Min(ProgressSlider.Value, musicPlaybackService.waveChannel.TotalTime.TotalSeconds));
                        musicPlaybackService.waveChannel.CurrentTime = TimeSpan.FromSeconds(newPosition);
                    //}
                }
                //else if (musicPlaybackService.ffmpegDecoder != null)
                //{
                //    double newPosition = Math.Max(0, Math.Min(ProgressSlider.Value, (double)musicPlaybackService.ffmpegDecoder.Length / musicPlaybackService.ffmpegDecoder.WaveFormat.BytesPerSecond));
                //    musicPlaybackService.ffmpegDecoder.Position = (long)(newPosition * musicPlaybackService.ffmpegDecoder.WaveFormat.BytesPerSecond);
                //}
            }
        }

        private void ProgressSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (isMouseOverProgressBar)
            {
                if (!musicPlaybackService.isUserDraggingProgressSlider && AppSettings.isPlaying)
                {
                    double currentPlayPosition = 0;
                    if (musicPlaybackService.waveChannel != null)
                    {
                        currentPlayPosition = musicPlaybackService.waveChannel.CurrentTime.TotalSeconds;
                        //if (AppSettings.isDsd)
                        //{
                        //    currentPlayPosition = musicPlaybackService.adapter.GetCurrentTime().TotalSeconds;
                        //}
                        if (Math.Abs(e.NewValue - currentPlayPosition) > 4.0)
                        {
                            DateTime startTime = DateTime.Now;
                            //if (AppSettings.isDsd)
                            //{
                            //    musicPlaybackService.adapter.SetCurrentTime(TimeSpan.FromSeconds(e.NewValue));
                            //}
                            //else {
                                musicPlaybackService.waveChannel.CurrentTime = TimeSpan.FromSeconds(e.NewValue);
                            //}                                
                            Debug.WriteLine($"ProgressSlider_ValueChanged完成,耗时:{(DateTime.Now - startTime).TotalMilliseconds}ms");
                        }
                    }
                    //else if (musicPlaybackService.ffmpegDecoder != null)
                    //{
                    //    currentPlayPosition = (double)musicPlaybackService.ffmpegDecoder.Position / musicPlaybackService.ffmpegDecoder.WaveFormat.BytesPerSecond;
                    //    if (Math.Abs(e.NewValue - currentPlayPosition) > 2.0)
                    //    {
                    //        musicPlaybackService.ffmpegDecoder.Position = (long)(e.NewValue * musicPlaybackService.ffmpegDecoder.WaveFormat.BytesPerSecond);
                    //    }
                    //}
                }
            }
        }

        private async void AlbumCoverImage_Tapped(object sender, TappedRoutedEventArgs e)
        {
            isInPlayingDetailMode = true;
            ConnectedAnimationService.GetForCurrentView().PrepareToAnimate("CoverToDetail", AlbumCoverImage);
            ViewModel.MusicDetailCover = await GetImageFromMusic(musicPlaybackService.currentPlayingMusic, 0);
            TopPanel.Visibility = Visibility.Collapsed;
            ContentFrame.Visibility = Visibility.Collapsed;
            AlbumCoverAuthorTitleModel.Visibility = Visibility.Collapsed;
            PlayingDetail.Visibility = Visibility.Visible;
            ConnectedAnimation animation = ConnectedAnimationService.GetForCurrentView().GetAnimation("CoverToDetail");
            if (animation != null)
            {
                animation.Configuration = new DirectConnectedAnimationConfiguration();
                animation.TryStart(PlayingDetailAlbumCoverImage);
            }
        }

        private void CancelPlayingDetailButton_Click(object sender, RoutedEventArgs e)
        {
            isInPlayingDetailMode = false;
            ConnectedAnimationService.GetForCurrentView().PrepareToAnimate("DetailToCover", PlayingDetailAlbumCoverImage);
            TopPanel.Visibility = Visibility.Visible;
            ContentFrame.Visibility = Visibility.Visible;
            PlayingDetail.Visibility = Visibility.Collapsed;
            AlbumCoverAuthorTitleModel.Visibility = Visibility.Visible;
            ConnectedAnimation animation = ConnectedAnimationService.GetForCurrentView().GetAnimation("DetailToCover");
            if (animation != null)
            {
                animation.Configuration = new BasicConnectedAnimationConfiguration();
                animation.TryStart(AlbumCoverImage);
            }
        }

        private void EqualizerButton_Click(object sender, RoutedEventArgs e)
        {
            equalizerDialog.RequestedTheme = AppSettings.elementTheme;
            equalizerDialog.XamlRoot = this.XamlRoot;
            _=equalizerDialog.ShowAsync();
           
        }
    }
}
