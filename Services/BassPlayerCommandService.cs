using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
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
        //public float volume = 0.5f;
        public bool isInitializing = true;
        public MusicBrowseViewModel MusicBrowseViewModel { get; }
        private IpcService IpcService { get; set; }

        public BassPlayerCommandService()
        {
            IpcService = App.Services.GetRequiredService<IpcService>();
            MusicBrowseViewModel = App.Services.GetRequiredService<MusicBrowseViewModel>();
            InitializingData();
            IpcService.NotificationReceived += IpcService_NotificationReceived;
        }

        private void IpcService_NotificationReceived(ResponseMessage obj)
        {
            if (obj.Type == MessageType.PlayState) {
                AppSettings.isPlaying = bool.Parse(obj.Result);
                MusicBrowseViewModel.UpdatePlayPauseButtonIcon();
                if (AppSettings.isPlaying)
                {
                    MusicBrowseViewModel.StartProgressTimer();
                }
                else {
                    MusicBrowseViewModel.StopProgressTimer();
                }
                App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                {
                    MusicBrowseViewModel.IsPlaying = AppSettings.isPlaying;
                });
            }
            if (obj.Type == MessageType.PlayEnded) {
                AppSettings.isPlaying = bool.Parse(obj.Result);
                MusicBrowseViewModel.UpdatePlayPauseButtonIcon();
                App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                {
                    MusicBrowseViewModel.IsPlaying = AppSettings.isPlaying;
                });
                AutoPlayNextTrack();
            }
            if (obj.Type == MessageType.VolumeWriteBack) {
                App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                {
                    MusicBrowseViewModel.Volume = double.Parse(obj.Result);
                });
            }
        }

        public async void InitializeMusicUrl(string musicUrl) {
            await IpcService.SetMusicUrl(musicUrl);
            await IpcService.UpdateEq();
            IpcService.UpdateSettings();           
        }

        public async void EqUpdate() {
            await IpcService.UpdateEq();
        }

        public void UpdateSettings() {
            IpcService.UpdateSettings();
        }

        private async void InitializingData()
        {
            MusicBrowseViewModel.SequentialPlayingList = new ObservableCollection<Music>(await MusicDatabaseService.LoadPlayList());
            UpdateCurrentPlayList();
        }

        public void UpdateCurrentPlayList(bool IsChangeList = true)
        {
            if (!IsChangeList)
            {
                return;
            }
            if (MusicBrowseViewModel.CurrentPlayMode != PlayMode.RandomLoop)
            {
                MusicBrowseViewModel.CurrentPlayingList = MusicBrowseViewModel.SequentialPlayingList;
            }
            else
            {
                MusicBrowseViewModel.CurrentPlayingList = MusicBrowseViewModel.SequentialPlayingList.CreateShuffled();
            }
        }       

        public void AutoPlayNextTrack()
        {
            MusicBrowseViewModel.StopProgressTimer();
            switch (AppData.PlayMode)
            {
                case PlayMode.SingleLoop:
                    MusicBrowseViewModel.PlayMusic(MusicBrowseViewModel.CurrentPlayingMusic);
                    break;
                case PlayMode.ListLoop:
                    int currentIndex = MusicBrowseViewModel.CurrentPlayingList.AsValueEnumerable().ToList().FindIndex(m => m.Id == MusicBrowseViewModel.CurrentPlayingMusic.Id);
                    int nextIndex = (currentIndex + 1) % MusicBrowseViewModel.CurrentPlayingList.Count;
                    MusicBrowseViewModel.PlayMusic(MusicBrowseViewModel.CurrentPlayingList[nextIndex]);
                    break;
                case PlayMode.RandomLoop:
                    Random random = new Random();
                    int randomIndex = random.Next(MusicBrowseViewModel.CurrentPlayingList.Count);
                    MusicBrowseViewModel.PlayMusic(MusicBrowseViewModel.CurrentPlayingList[randomIndex]);
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
                int currentIndex = MusicBrowseViewModel.CurrentPlayingList.AsValueEnumerable().ToList().FindIndex(m => m.Id == MusicBrowseViewModel.CurrentPlayingMusic.Id);
                int nextIndex = (currentIndex + 1) % MusicBrowseViewModel.CurrentPlayingList.Count;
                MusicBrowseViewModel.PlayMusic(MusicBrowseViewModel.CurrentPlayingList[nextIndex]);
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
            _ = MusicDatabaseService.SavePlayState([.. MusicBrowseViewModel.SequentialPlayingList], AppData.PlayMode, MusicBrowseViewModel.CurrentPlayingMusic?.Id, (float)(MusicBrowseViewModel.Volume/100), AppData.sortOrder);
        }        

        public void PlayButton()
        {           
            IpcService.PlayButton();           
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
            return await IpcService.GetCurrentPostion();
        }

        public async Task<double> GetTotalPosition()
        {
            return await IpcService.GetDuration();
        }

        public async Task<double> AdjustPlaybackPosition(int seconds)
        {           
            return await IpcService.AdjustPlaybackPosition(seconds);
        }

        public void ChangingSetting()
        {
            IpcService.UpdateSettings(true);           
        }
        
        public async Task Dispose()
        {
            await IpcService?.Dispose();
        }
    }
}
