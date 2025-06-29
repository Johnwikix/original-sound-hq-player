using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
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

        private int _volume = 50;
        public int Volume
        {
            get => _volume;
            set
            {
                if (SetProperty(ref _volume, value))
                {
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
        private MusicPlaybackService _musicPlaybackService;
        private SystemMediaControlsService _systemMediaControlsService;
        private MusicBrowsePage _musicBrowsePage;

        public MusicBrowseViewModel(SystemMediaControlsService systemMediaControlsService)
        {
            var mode = AppData.PlayMode;
            CurrentPlayMode = AppData.PlayMode;
            _systemMediaControlsService = systemMediaControlsService;
            InitializeSystemMediaControls();
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
            _systemMediaControlsService.UpdateSystemMediaControlsState();
        }

        public void UpdatePlayPauseButtonIcon()
        {
            App.MainWindow.UpdateTaskbarIcon();
            App.MainWindow.UpdateIconControl();
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

        private async Task PlayLastTrack()
        {
            int index = _musicPlaybackService.currentPlayingList.IndexOf(_musicPlaybackService.currentPlayingMusic);
            if (index > 0)
            {
                await _musicBrowsePage.PlayMusic(_musicPlaybackService.currentPlayingList[index - 1]);
            }
            else if (index == 0 && _musicPlaybackService.currentPlayingList.Count > 1)
            {
                await _musicBrowsePage.PlayMusic(_musicPlaybackService.currentPlayingList[_musicPlaybackService.currentPlayingList.Count - 1]);

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
    }
}
