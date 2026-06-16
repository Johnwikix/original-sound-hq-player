using BassPlayerIpc.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
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

        private void IpcService_NotificationReceived(MessageTypeId typeId, ReadOnlyMemory<byte> payload)
        {
            if (typeId == MessageTypeId.PlayState)
            {
                var state = BinarySerializer.ReadPlayStateResponse(payload.Span);
                App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                {
                    AppViewModel.IsPlaying = state.IsPlaying;
                    if (state.IsPlaying)
                        AppViewModel.StartProgressTimer();
                    else
                        AppViewModel.StopProgressTimer();
                });
            }
            else if (typeId == MessageTypeId.PlayEnded)
            {
                App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                {
                    AppViewModel.IsPlaying = false;
                });
                AutoPlayNextTrack().Wait();
            }
            else if (typeId == MessageTypeId.VolumeWriteBack)
            {
                var vol = BinarySerializer.ReadVolumeResponse(payload.Span);
                App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                {
                    AppViewModel.Volume = vol.Volume;
                });
            }
            else if (typeId == MessageTypeId.NotificationDropped)
            {
                _logger.LogWarning("Notification dropped by server - client may miss state updates");
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
            App.MainWindow.DispatcherQueue.TryEnqueue(() =>
            {
                AppViewModel.StopProgressTimer();
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
            catch (Exception ex) { _logger.LogError(ex, $"PlayNextTrack failed: {ex.Message}"); }
        }

        public void ToggleEqualizer()
        {
            IpcService.ToggleEqualizer();
        }

        public void SetEqualizerGain(byte bandIndex, float gain)
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

        public void ChangeWaveChannelTime(long positionMs)
        {
            IpcService.SetPosition(positionMs);
        }

        public void SetVolume(double volume)
        {
            IpcService.ChangeVolume(volume);
        }

        public async Task<(long currentMs, long totalMs)> GetTimeProgress()
        {
            try
            {
                return await IpcService.GetTimeProgress();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"GetTimeProgress failed: {ex.Message}");
                return (0, 0);
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

        public Task<List<(int id, string name)>> GetWasapiDevices()
            => IpcService.GetWasapiDevices();

        public Task<List<(int id, string name)>> GetAsioDevices()
            => IpcService.GetAsioDevices();
    }
}
