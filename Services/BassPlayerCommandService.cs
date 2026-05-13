using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using WinUIMusicPlayer.Extensions;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.View;
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
        public AppViewModel AppViewModel { get; }
        private IpcService IpcService { get; set; }
        private MusicDatabaseService _musicDatabaseService { get; }
        private ILogger<BassPlayerCommandService> _logger;

        public BassPlayerCommandService(AppViewModel appViewModel, MusicDatabaseService musicDatabaseService, ILogger<BassPlayerCommandService> logger)
        {
            IpcService = App.Services.GetRequiredService<IpcService>();
            AppViewModel = appViewModel;
            _musicDatabaseService = musicDatabaseService;
            _logger = logger;
            IpcService.NotificationReceived += IpcService_NotificationReceived;
        }

        private void IpcService_NotificationReceived(ResponseMessage obj)
        {
            if (obj.Type == MessageType.PlayState)
            {
                App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                {
                    AppViewModel.IsPlaying = bool.Parse(obj.Result);
                });
                if (bool.Parse(obj.Result))
                {
                    AppViewModel.StartProgressTimer();
                }
                else
                {
                    AppViewModel.StopProgressTimer();
                }                
            }
            if (obj.Type == MessageType.PlayEnded)
            {

                App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                {
                    AppViewModel.IsPlaying = bool.Parse(obj.Result);
                });
                AutoPlayNextTrack().Wait();
            }
            if (obj.Type == MessageType.VolumeWriteBack)
            {
                App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                {
                    AppViewModel.Volume = double.Parse(obj.Result);
                });
            }
        }
        public async void EqUpdate()
        {
            await IpcService.UpdateEq();
        }

        public void UpdateSettings()
        {
            IpcService.UpdateSettings();
        }

        public async Task AutoPlayNextTrack()
        {
            AppViewModel.StopProgressTimer();
            switch (AppViewModel.CurrentPlayMode)
            {
                case PlayMode.SingleLoop:
                    await MusicBrowsePlayMusic(AppViewModel.CurrentPlayingMusic);
                    break;
                case PlayMode.ListLoop:
                case PlayMode.RandomLoop:
                    int currentIndex = AppViewModel.CurrentPlayingList.AsValueEnumerable().ToList().FindIndex(m => m.Id == AppViewModel.CurrentPlayingMusic.Id);
                    int nextIndex = (currentIndex + 1) % AppViewModel.CurrentPlayingList.Count;
                    await MusicBrowsePlayMusic(AppViewModel.CurrentPlayingList[nextIndex]);
                    break;
                case PlayMode.RepeatOff:
                    MusicEnd();
                    break;
            }
        }

        private static async Task MusicBrowsePlayMusic(Music music)
        {
            await App.Services.GetRequiredService<MusicBrowseViewModel>().PlayMusic(music);
        }

        public void MusicEnd()
        {
            IpcService.MusicEnd();
            AppViewModel.StopProgressTimer();
            App.MainWindow.DispatcherQueue.TryEnqueue(() =>
            {
                AppViewModel.ProgressSlider = 0;
                AppViewModel.IsPlaying = false;
            });
        }

        public void PlayNextTrack()
        {
            try
            {
                int currentIndex = AppViewModel.CurrentPlayingList.AsValueEnumerable().ToList().FindIndex(m => m.Id == AppViewModel.CurrentPlayingMusic.Id);
                int nextIndex = (currentIndex + 1) % AppViewModel.CurrentPlayingList.Count;
                MusicBrowsePlayMusic(AppViewModel.CurrentPlayingList[nextIndex]);
            }
            catch (Exception ex) { _logger.LogError(ex, $"PlayNextTrack 操作失败: {ex.Message}"); }
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
            AppViewModel.StartProgressTimer();
        }

        public void PlayButton()
        {
            IpcService.PlayButton();
            App.MainWindow.DispatcherQueue.TryEnqueue(() =>
            {
                if (AppViewModel.IsPlaying)
                {
                    AppViewModel.IsPlaying = false;
                    AppViewModel.StopProgressTimer();
                }
            });
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
            catch (Exception ex) { _logger.LogError(ex, $"GetCurrentPosition 操作失败: {ex.Message}");
                return 0;
            }            
        }

        public async Task<double> GetTotalPosition()
        {
            try
            {
                return await IpcService.GetDuration();
            }
            catch (Exception ex) { _logger.LogError(ex, $"GetTotalPosition 操作失败: {ex.Message}");
                return 0;
            }            
        }

        public async Task<double> AdjustPlaybackPosition(int seconds)
        {
            try
            {
                return await IpcService.AdjustPlaybackPosition(seconds);
            }
            catch (Exception ex) { _logger.LogError(ex, $"AdjustPlaybackPosition 操作失败: {ex.Message}");
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
    }
}
