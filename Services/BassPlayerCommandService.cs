using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using WinUIMusicPlayer.Extensions;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.ViewModel;
using ZLinq;
using static WinUIMusicPlayer.Utils.ToolUtils;

namespace WinUIMusicPlayer.Services
{
    public class BassPlayerCommandService
    {
        public int? lastPlayedMusicId;
        public bool isPausing = false;
        public bool isSettingsChangeStop = false;
        public bool isInitializing = true;
        public MusicBrowseViewModel MusicBrowseViewModel { get; }
        public AppObservableObj AppObservableObj { get; }
        private IpcService IpcService { get; set; }
        private MusicDatabaseService _musicDatabaseService { get; }

        public BassPlayerCommandService(AppObservableObj appObservableObj,MusicDatabaseService musicDatabaseService,IpcService ipcService)
        {
            IpcService = ipcService;
            MusicBrowseViewModel = App.Services.GetRequiredService<MusicBrowseViewModel>();
            AppObservableObj = appObservableObj;
            _musicDatabaseService = musicDatabaseService;
            InitializingData();
            IpcService.NotificationReceived += IpcService_NotificationReceived;
        }

        private void IpcService_NotificationReceived(ResponseMessage obj)
        {
            if (obj.Type == MessageType.PlayState)
            {
                AppSettings.isPlaying = bool.Parse(obj.Result);
                MusicBrowseViewModel.UpdatePlayPauseButtonIcon();
                if (AppSettings.isPlaying)
                {
                    MusicBrowseViewModel.StartProgressTimer();
                }
                else
                {
                    MusicBrowseViewModel.StopProgressTimer();
                }
                App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                {
                    MusicBrowseViewModel.IsPlaying = AppSettings.isPlaying;
                });
            }
            if (obj.Type == MessageType.PlayEnded)
            {
                AppSettings.isPlaying = bool.Parse(obj.Result);
                MusicBrowseViewModel.UpdatePlayPauseButtonIcon();
                App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                {
                    MusicBrowseViewModel.IsPlaying = AppSettings.isPlaying;
                });
                AutoPlayNextTrack();
            }
            if (obj.Type == MessageType.VolumeWriteBack)
            {
                App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                {
                    AppObservableObj.Volume = double.Parse(obj.Result);
                });
            }
        }

        public async void InitializeMusicUrl(string musicUrl)
        {
            await IpcService.SetMusicUrl(musicUrl);
            await IpcService.UpdateEq();
            IpcService.UpdateSettings();
        }

        public async void EqUpdate()
        {
            await IpcService.UpdateEq();
        }

        public void UpdateSettings()
        {
            IpcService.UpdateSettings();
        }

        private async void InitializingData()
        {
            AppObservableObj.SequentialPlayingList = new ObservableCollection<Music>(await _musicDatabaseService.LoadPlayList());
            UpdateCurrentPlayList();
        }

        public void UpdateCurrentPlayList(bool IsChangeList = true)
        {
            if (!IsChangeList)
            {
                return;
            }
            if (AppObservableObj.CurrentPlayMode != PlayMode.RandomLoop)
            {
                AppObservableObj.CurrentPlayingList = AppObservableObj.SequentialPlayingList;
            }
            else
            {
                AppObservableObj.CurrentPlayingList = AppObservableObj.SequentialPlayingList.CreateShuffled();
            }
        }

        public void AutoPlayNextTrack()
        {
            MusicBrowseViewModel.StopProgressTimer();
            switch (AppObservableObj.CurrentPlayMode)
            {
                case PlayMode.SingleLoop:
                    MusicBrowseViewModel.PlayMusic(AppObservableObj.CurrentPlayingMusic);
                    break;
                case PlayMode.ListLoop:
                    int currentIndex = AppObservableObj.CurrentPlayingList.AsValueEnumerable().ToList().FindIndex(m => m.Id == AppObservableObj.CurrentPlayingMusic.Id);
                    int nextIndex = (currentIndex + 1) % AppObservableObj.CurrentPlayingList.Count;
                    MusicBrowseViewModel.PlayMusic(AppObservableObj.CurrentPlayingList[nextIndex]);
                    break;
                case PlayMode.RandomLoop:
                    Random random = new Random();
                    int randomIndex = random.Next(AppObservableObj.CurrentPlayingList.Count);
                    MusicBrowseViewModel.PlayMusic(AppObservableObj.CurrentPlayingList[randomIndex]);
                    break;
                case PlayMode.RepeatOff:
                    MusicEnd();
                    break;
            }
        }

        public void MusicEnd()
        {
            IpcService.MusicEnd();
            MusicBrowseViewModel.StopProgressTimer();
            App.MainWindow.DispatcherQueue.TryEnqueue(() =>
            {
                MusicBrowseViewModel.ProgressSlider = 0;
                AppSettings.isPlaying = false;
                MusicBrowseViewModel.IsPlaying = false;
                MusicBrowseViewModel.UpdatePlayPauseButtonIcon();
            });
        }

        public void PlayNextTrack()
        {
            try
            {
                int currentIndex = AppObservableObj.CurrentPlayingList.AsValueEnumerable().ToList().FindIndex(m => m.Id == AppObservableObj.CurrentPlayingMusic.Id);
                int nextIndex = (currentIndex + 1) % AppObservableObj.CurrentPlayingList.Count;
                MusicBrowseViewModel.PlayMusic(AppObservableObj.CurrentPlayingList[nextIndex]);
            }
            catch
            {
            }
        }

        public void ToggleEqualizer()
        {
            IpcService.ToggleEqualizer();
        }

        public void SetEqualizerGain(int bandIndex, float gain)
        {
            IpcService.SetEqualizerGain(bandIndex, gain);

        }

        public void SetEqualizer()
        {
            IpcService.SetEqualizer();

        }

        public void ClearEqualizer()
        {
            IpcService.ClearEqualizer();
        }
        public void PlayMusic(Music music)
        {
            IpcService.Play(music.Path);
            MusicBrowseViewModel.StartProgressTimer();
            _ = _musicDatabaseService.SavePlayState([.. AppObservableObj.SequentialPlayingList], AppObservableObj.CurrentPlayMode, AppObservableObj.CurrentPlayingMusic?.Id, (float)(AppObservableObj.Volume), AppData.sortOrder);
        }

        public void PlayButton()
        {
            IpcService.PlayButton();
            if (AppSettings.isPlaying) {
                AppSettings.isPlaying = false;
                MusicBrowseViewModel.UpdatePlayPauseButtonIcon();
                MusicBrowseViewModel.StopProgressTimer();
                App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                {
                    MusicBrowseViewModel.IsPlaying = AppSettings.isPlaying;
                });
            }
        }

        public void ChangeWaveChannelTime(System.TimeSpan timeSpan)
        {
            IpcService.SetPosition(timeSpan.TotalSeconds);
        }

        public void SetVolume(double volume)
        {
            IpcService.ChangeVolume(volume);
        }

        public async Task<double> GetCurrentPosition()
        {
            try
            {
                return await IpcService.GetCurrentPostion();
            }
            catch {
                return 0;
            }            
        }

        public async Task<double> GetTotalPosition()
        {
            try
            {
                return await IpcService.GetDuration();
            }
            catch {
                return 0;
            }            
        }

        public async Task<double> AdjustPlaybackPosition(int seconds)
        {
            try
            {
                return await IpcService.AdjustPlaybackPosition(seconds);
            }
            catch { 
                return 0;
            }            
        }

        public void ChangingSetting()
        {
            IpcService.UpdateSettings(true);
        }

        public void FadeOut()
        {
            IpcService.FadeOut();
        }

        public async Task Dispose()
        {
            await IpcService?.Dispose();
        }
    }
}
