using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using SQLite;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using NAudio.Wave;
using static WinUIMusicPlayer.Utils.ToolUtils;
using Windows.UI.Popups;
using WinRT.Interop;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using NAudio.CoreAudioApi;
using NAudio.Gui;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUIMusicPlayer.View
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MusicBrowsePage : Page
    {
        private SQLiteAsyncConnection dbConnection;
        private MediaPlayer mediaPlayer;
        private Music currentPlayingMusic;
        private IWavePlayer waveOut;
        private AudioFileReader audioFileReader;
        private float volume = 0.5f;
        private bool isPlaying;
        private System.Timers.Timer progressTimer;
        private bool isUserDraggingProgressSlider = false;
        private PlayMode currentPlayMode = PlayMode.ListLoop;
        private int? lastPlayedMusicId;
        private bool isManualSelect = false;
        private bool isPausing = false;
        private bool isSettingsChangeStop = false;
        private TimeSpan currentPosition;

        public MusicBrowsePage()
        {
            this.InitializeComponent();
            InitializeDatabase();
            progressTimer = new System.Timers.Timer(1000);
            progressTimer.Elapsed += ProgressTimer_Elapsed;
            ProgressSlider.Loaded += ProgressSlider_Loaded;

            // 订阅窗口关闭事件
            var window = Window.Current;
            if (window != null)
            {
                window.Closed += Window_Closed;
            }
            AppSettings.OutputSettingsChanged += AppSettings_OutputSettingsChanged;
        }

        private void AppSettings_OutputSettingsChanged(object sender, EventArgs e)
        {
            // 如果当前正在播放，停止播放并重新初始化音频资源
            if (isPlaying)
            {
                isManualSelect = true;
                currentPosition = audioFileReader.CurrentTime;
                if (progressTimer != null)
                {
                    progressTimer.Stop();
                    ProgressSlider.Value = 0;
                }
                if (waveOut != null)
                {
                    waveOut.Stop();
                    waveOut.Dispose();
                    waveOut = null;
                }
                if (audioFileReader != null)
                {
                    audioFileReader.Dispose();
                    audioFileReader = null;
                }
                if (currentPlayingMusic != null)
                {
                    _ = PlayMusic(currentPlayingMusic);
                }
            }
        }
        private void Window_Closed(object sender, WindowEventArgs args)
        {
            // 停止定时器
            if (progressTimer != null)
            {
                progressTimer.Stop();
                progressTimer.Elapsed -= ProgressTimer_Elapsed;
                progressTimer.Dispose();
                progressTimer = null;
            }

            // 停止并释放 waveOut
            if (waveOut != null)
            {
                waveOut.Stop();
                waveOut.Dispose();
                waveOut = null;
            }

            // 释放 audioFileReader
            if (audioFileReader != null)
            {
                audioFileReader.Dispose();
                audioFileReader = null;
            }
        }

        private async void ShowMessage(string message)
        {
            try
            {
                ContentDialog contentDialog = new ContentDialog
                {
                    Title = "错误",
                    Content = message,
                    CloseButtonText = "确定",
                    XamlRoot = this.XamlRoot
                };
                await contentDialog.ShowAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"错误: {ex.Message}");
            }            
        }

        private async Task LoadPlayState()
        {
            var playState = await dbConnection.Table<PlayState>().FirstOrDefaultAsync();
            if (playState == null)
            {
                // 如果没有记录，默认设置为列表循环
                playState = new PlayState
                {
                    PlayMode = PlayMode.ListLoop,
                    Volume = 0.5f,
                    LastPlayedMusicId = null
                };
                await dbConnection.InsertAsync(playState);
            }
            currentPlayMode = playState.PlayMode;
            lastPlayedMusicId = playState.LastPlayedMusicId;
            volume = playState.Volume;
            VolumeSlider.Value = volume * 100;
            currentPlayingMusic = await dbConnection.Table<Music>().Where(m => m.Id == lastPlayedMusicId).FirstOrDefaultAsync();
            if (currentPlayingMusic != null)
            {
                MusicTitleTextBlock.Text = currentPlayingMusic.Title;
                MusicAuthorTextBlock.Text = currentPlayingMusic.Author;
                if (currentPlayingMusic.Cover != null) {
                    using (var ms = new MemoryStream(currentPlayingMusic.Cover))
                    {
                        var bitmapImage = new BitmapImage();
                        await bitmapImage.SetSourceAsync(ms.AsRandomAccessStream());
                        AlbumCoverImage.Source = bitmapImage;
                    }
                }                
            }
            UpdatePlayModeIcon();
        }

        private async Task SavePlayState()
        {
            var playState = await dbConnection.Table<PlayState>().FirstOrDefaultAsync();
            if (playState == null)
            {
                playState = new PlayState
                {
                    Id = 1
                };
            }
            playState.PlayMode = currentPlayMode;
            playState.LastPlayedMusicId = currentPlayingMusic?.Id;
            playState.Volume = volume;
            if (playState.Id == 0)
            {
                await dbConnection.InsertAsync(playState);
            }
            else
            {
                await dbConnection.UpdateAsync(playState);
            }
        }

        private async void PlayModeButton_Click(object sender, RoutedEventArgs e)
        {
            switch (currentPlayMode)
            {
                case PlayMode.SingleLoop:
                    currentPlayMode = PlayMode.ListLoop;
                    break;
                case PlayMode.ListLoop:
                    currentPlayMode = PlayMode.RandomLoop;
                    break;
                case PlayMode.RandomLoop:
                    currentPlayMode = PlayMode.SingleLoop;
                    break;
            }
            await SavePlayState();
            UpdatePlayModeIcon();
        }

        private void UpdatePlayModeIcon()
        {
            switch (currentPlayMode)
            {
                case PlayMode.SingleLoop:
                    PlayModeIcon.Glyph = "\ue8ed"; // 单曲循环图标
                    break;
                case PlayMode.ListLoop:
                    PlayModeIcon.Glyph = "\ue8ee"; // 列表循环图标
                    break;
                case PlayMode.RandomLoop:
                    PlayModeIcon.Glyph = "\ue8b1"; // 随机循环图标
                    break;
            }
        }

        protected override async void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            await LoadMusicAsync();
        }

        private async void InitializeDatabase()
        {
            var dbPath = Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, "MusicDatabase.db");
            dbConnection = new SQLiteAsyncConnection(dbPath);
            await dbConnection.CreateTableAsync<Music>();
            await dbConnection.CreateTableAsync<PlayState>();
            await LoadMusicAsync();
            await LoadPlayState();
            PlayTimeTextBlock.Text = "00:00/00:00";
        }

        private async Task LoadMusicAsync()
        {
            try
            {
                var musicList = (await dbConnection.Table<Music>().ToListAsync()).OrderBy(m=> m.Title).ToList();
                MusicListView.ItemsSource = musicList;
            }
            catch (SQLiteException ex)
            {
                System.Diagnostics.Debug.WriteLine($"SQLite 错误: {ex.Message}");
            }
        }

        private async void MusicListView_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
        {
            var selectedMusic = MusicListView.SelectedItem as Music;
            if (selectedMusic != null)
            {
                isManualSelect = true;
                await PlayMusic(selectedMusic);
                isManualSelect = false;
            }
        }

        private async void PlayButton_Click(object sender, RoutedEventArgs e)
        {            
            if (isPlaying)
            {
                if (AppSettings.OutputMode == "WasapiExclusive")
                {
                    // 设置暂停标志
                    isPausing = true;
                    currentPosition = audioFileReader.CurrentTime;
                    waveOut.Stop();
                    isPlaying = false;
                    progressTimer.Stop();
                }
                else {
                    waveOut.Pause();
                    isPlaying = false;
                    progressTimer.Stop();
                }               
            }
            else {
                if (waveOut == null)
                {
                    if (currentPlayingMusic != null)
                    {
                        await PlayMusic(currentPlayingMusic);
                    }
                    else
                    {
                        var musicList = (await dbConnection.Table<Music>().ToListAsync()).OrderBy(m => m.Title).ToList(); ;
                        await PlayMusic(musicList[0]);
                    }                        
                }
                else {
                    if (AppSettings.OutputMode == "WasapiExclusive" && isPausing)
                    {
                        isPausing = false;
                        // 不重新初始化 waveOut
                        audioFileReader.CurrentTime = currentPosition;
                        waveOut.Play();
                        isPlaying = true;
                        progressTimer.Start();
                    }
                    else {
                        waveOut.Play();
                        isPlaying = true;
                        progressTimer.Start();
                    }                    
                }                
            }
            UpdatePlayPauseButtonIcon();
        }

        private void UpdatePlayPauseButtonIcon()
        {
            if (isPlaying)
            {
                ((FontIcon)PlayPauseButton.Content).Glyph = "\uE769"; // 暂停图标
            }
            else
            {
                ((FontIcon)PlayPauseButton.Content).Glyph = "\uE768"; // 播放图标
            }
        }
        private async Task<bool> InitializeAudioResources(Music music)
        {
            try {
                if (waveOut != null)
                {
                    waveOut.Stop();
                    waveOut.Dispose();
                    waveOut = null;
                }

                if (audioFileReader != null)
                {
                    audioFileReader.Dispose();
                    audioFileReader = null;
                }                
                // 加载新音频
                audioFileReader = new AudioFileReader(music.Path);
                audioFileReader.Volume = volume;
                MMDevice selectedDevice = null;
                if (AppSettings.OutputDevice.mMDevice != null)
                {
                    selectedDevice = AppSettings.OutputDevice.mMDevice;
                }
                switch (AppSettings.OutputMode)
                {
                    case "WaveOut":
                        waveOut = new WaveOutEvent();  
                        break;
                    case "WasapiShared":
                        waveOut = new WasapiOut(selectedDevice,AudioClientShareMode.Shared,false,AppSettings.Latency);  
                        break;       
                    case "WasapiExclusive":                             
                        waveOut = new WasapiOut(selectedDevice,AudioClientShareMode.Exclusive,true, AppSettings.Latency);
                        break;
                    case "DirectSound":
                        waveOut = new DirectSoundOut(AppSettings.Latency);
                        break;
                    default:
                        waveOut = new WaveOutEvent();                        
                        break;
                }
                if (waveOut is WaveOutEvent defaultWaveOutEvent)
                {
                    defaultWaveOutEvent.DesiredLatency = AppSettings.Latency;
                    defaultWaveOutEvent.NumberOfBuffers = 3;
                }
                waveOut.Init(audioFileReader);
                return true;
            }
            catch (Exception ex)
            {
                ShowMessage($"播放失败{ex.Message}");
                System.Diagnostics.Debug.WriteLine($"错误: {ex.Message}");
                return false;
            }
        }

        private async Task PlayMusic(Music music)
        {
            currentPlayingMusic = music;
            MusicTitleTextBlock.Text = music.Title;
            MusicAuthorTextBlock.Text = music.Author;
            MusicListView.SelectedItem = music;
            MusicListView.ScrollIntoView(music);

            if (music.Cover != null)
            {
                using (var ms = new MemoryStream(music.Cover))
                {
                    var bitmapImage = new BitmapImage();
                    await bitmapImage.SetSourceAsync(ms.AsRandomAccessStream());
                    AlbumCoverImage.Source = bitmapImage;
                }
            }
            if (await InitializeAudioResources(music))
            {
                try {                

                    waveOut.Play();
                    waveOut.PlaybackStopped += WaveOut_PlaybackStopped;
                    ((FontIcon)PlayPauseButton.Content).Glyph = "\uE769";
                    isPlaying = true;

                    // 初始化进度条
                    if (audioFileReader.TotalTime != null)
                    {
                        ProgressSlider.Maximum = audioFileReader.TotalTime.TotalSeconds;
                    }
                    ProgressSlider.Value = 0;
                    progressTimer.Start();

                    await SavePlayState();
                }
                catch (Exception ex)
                {
                    ShowMessage($"播放失败{ex.Message}");
                    System.Diagnostics.Debug.WriteLine($"错误: {ex.Message}");
                }                
            }            
        }

        private async void WaveOut_PlaybackStopped(object sender, StoppedEventArgs e)
        {
            if (isPausing)
            {
                // 如果是暂停，不执行自动播放
                return;
            }
            if (isManualSelect)
            {
                isManualSelect = false;
                return;
            }
            // 判断是否是设置更改导致的播放停止，如果是则不执行自动联播
            else if (isSettingsChangeStop)
            {
                isSettingsChangeStop = false;
                return;
            }
            else
            {
                await OnMusicEnded();
            }
        }

        private async Task OnMusicEnded()
        {
            AutoPlay();
        }

        private async void AutoPlay() {
            switch (currentPlayMode)
            {
                case PlayMode.SingleLoop:
                    await PlayMusic(currentPlayingMusic);
                    break;
                case PlayMode.ListLoop:
                    var musicList = (await dbConnection.Table<Music>().ToListAsync()).OrderBy(m => m.Title).ToList();
                    int currentIndex = musicList.FindIndex(m => m.Id == currentPlayingMusic.Id);
                    int nextIndex = (currentIndex + 1) % musicList.Count;
                    await PlayMusic(musicList[nextIndex]);
                    break;
                case PlayMode.RandomLoop:
                    var allMusic = (await dbConnection.Table<Music>().ToListAsync()).OrderBy(m => m.Title).ToList();
                    Random random = new Random();
                    int randomIndex = random.Next(allMusic.Count);
                    await PlayMusic(allMusic[randomIndex]);
                    break;
            }
        }

        private async void RemoveMusicButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button != null && button.Tag is int musicId)
            {
                await dbConnection.DeleteAsync<Music>(musicId);
                await LoadMusicAsync();
            }
        }

        private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            volume = (float)e.NewValue / 100;
            if (audioFileReader != null)
            {                
                audioFileReader.Volume = volume;
                if (e.NewValue > 66)
                {
                    VolumeSliderIcon.Glyph = "\ue995";
                }
                else if (e.NewValue > 33)
                {
                    VolumeSliderIcon.Glyph = "\ue994";
                }
                else if (e.NewValue > 0)
                {
                    VolumeSliderIcon.Glyph = "\uE993";
                }
                else
                {
                    VolumeSliderIcon.Glyph = "\uE992";
                }
            }
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

        private void Thumb_DragStarted(object sender, DragStartedEventArgs e)
        {
            isUserDraggingProgressSlider = true;
        }

        private void Thumb_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            isUserDraggingProgressSlider = false;
            if (audioFileReader != null && isPlaying)
            {
                double newPosition = Math.Max(0, Math.Min(ProgressSlider.Value, audioFileReader.TotalTime.TotalSeconds));
                audioFileReader.CurrentTime = TimeSpan.FromSeconds(newPosition);
            }
        }

        private T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typedChild)
                {
                    return typedChild;
                }
                else
                {
                    var foundChild = FindVisualChild<T>(child);
                    if (foundChild != null)
                    {
                        return foundChild;
                    }
                }
            }
            return null;
        }

        private void ProgressTimer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            if (audioFileReader != null && isPlaying && !isUserDraggingProgressSlider)
            {
                this.DispatcherQueue.TryEnqueue(() =>
                {
                    try {
                        if (audioFileReader.CurrentTime != null)
                        {
                            ProgressSlider.Value = audioFileReader.CurrentTime.TotalSeconds;
                            string currentTime = audioFileReader.CurrentTime.ToString(@"mm\:ss");
                            string totalTime = audioFileReader.TotalTime.ToString(@"mm\:ss");
                            PlayTimeTextBlock.Text = $"{currentTime}/{totalTime}";
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"错误: {ex.Message}");
                    }                    
                });
            }
        }

        private void ProgressSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (!isUserDraggingProgressSlider && audioFileReader != null && isPlaying)
            {
                ProgressSlider.Value = audioFileReader.CurrentTime.TotalSeconds;
            }
        }
    }
}
