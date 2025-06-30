using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using NAudio.Gui;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
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

        private ObservableCollection<Music> _currentPlayingList;
        public ObservableCollection<Music> CurrentPlayingList
        {
            get => _currentPlayingList;
            set => SetProperty(ref _currentPlayingList, value);
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
                            double currentPlayPosition = 0;
                            if (_musicPlaybackService.waveChannel != null)
                            {
                                currentPlayPosition = _musicPlaybackService.waveChannel.CurrentTime.TotalSeconds;
         
                                if (Math.Abs(value - currentPlayPosition) > 4.0)
                                {        
                                    _musicPlaybackService.waveChannel.CurrentTime = TimeSpan.FromSeconds(value);                               
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
            set
            {
                if (SetProperty(ref _progressSliderMax, value))
                {
                }
            }
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
                        if (_musicPlaybackService.waveChannel != null)
                        {
                            _musicPlaybackService.waveChannel.Volume = AppSettings.isDsd ? _musicPlaybackService.volume * (float)Math.Pow(10, AppSettings.dsdGain / 20.0) : _musicPlaybackService.volume;
                        }
                    }
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
        private MusicPlaybackService _musicPlaybackService;
        private SystemMediaControlsService _systemMediaControlsService;
        private MusicBrowsePage _musicBrowsePage;

        public MusicBrowseViewModel(SystemMediaControlsService systemMediaControlsService)
        {
            var mode = AppData.PlayMode;
            CurrentPlayMode = AppData.PlayMode;
            _systemMediaControlsService = systemMediaControlsService;
            InitializeSystemMediaControls();
            Volume = (double)(AppData.Volume * 100);
            _tempVolume = (double)(AppData.Volume * 100);
            AppSettings.OutputSettingsChanged += AppSettings_OutputSettingsChanged;
        }

        private void AppSettings_OutputSettingsChanged(object? sender, EventArgs e)
        {
            _musicPlaybackService.ChangingSetting();
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
        }

        public void SetMusicBrowsePage(MusicBrowsePage musicBrowsePage)
        {
            _musicBrowsePage = musicBrowsePage;
        }

        [RelayCommand]
        public void OnPlayModeChanged()
        {
            switch (CurrentPlayMode)
            {
                case PlayMode.SingleLoop:
                    AppData.PlayMode = PlayMode.ListLoop;
                    CurrentPlayMode = PlayMode.ListLoop;
                    break;
                case PlayMode.ListLoop:
                    AppData.PlayMode = PlayMode.RandomLoop;
                    CurrentPlayMode = PlayMode.RandomLoop;
                    break;
                case PlayMode.RandomLoop:
                    AppData.PlayMode = PlayMode.RepeatOff;
                    CurrentPlayMode = PlayMode.RepeatOff;
                    break;
                case PlayMode.RepeatOff:
                    AppData.PlayMode = PlayMode.SingleLoop;
                    CurrentPlayMode = PlayMode.SingleLoop;
                    break;
            }
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
            App.MainWindow.UpdateIconControl();
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
            _musicPlaybackService.isManualSelect = true;
            _musicPlaybackService.PlayNextTrack();
            _musicPlaybackService.isManualSelect = false;
        }

        public async void LastMusicButton_Click()
        {
            _musicPlaybackService.isManualSelect = true;
            await PlayLastTrack();
            _musicPlaybackService.isManualSelect = false;
        }

        public void PlayMusic(Music music)
        {
            App.MainWindow.DispatcherQueue.TryEnqueue(async () =>
            {
                await _musicBrowsePage.PlayMusic(music);
            });
        }
           

        private async Task PlayLastTrack()
        {
            int index = _musicPlaybackService.MusicBrowseViewModel.CurrentPlayingList.IndexOf(CurrentPlayingMusic);
            if (index > 0)
            {
                await _musicBrowsePage.PlayMusic(_musicPlaybackService.MusicBrowseViewModel.CurrentPlayingList[index - 1]);
            }
            else if (index == 0 && _musicPlaybackService.MusicBrowseViewModel.CurrentPlayingList.Count > 1)
            {
                await _musicBrowsePage.PlayMusic(_musicPlaybackService.MusicBrowseViewModel.CurrentPlayingList[_musicPlaybackService.MusicBrowseViewModel.CurrentPlayingList.Count - 1]);

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

        public void AdjustVolume(int delta)
        {
            double newVolume = Volume + delta;
            newVolume = Math.Max(0, Math.Min(newVolume, 100));
            Volume = newVolume;
        }
    }
}
