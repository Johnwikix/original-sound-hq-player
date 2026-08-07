using BassPlayerIpc.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.ViewModel;
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
                _ = AutoPlayNextTrack();
            }
            else if (typeId == MessageTypeId.VolumeWriteBack)
            {
                var vol = BinarySerializer.ReadVolumeResponse(payload.Span);
                App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                {
                    AppViewModel.Volume = vol.Volume;
                });
            }
        }

        public void EqUpdate()
        {
            IpcService.UpdateEq();
        }

        /// <summary>Full EQ state sync awaiting the server's real applied state.</summary>
        public async Task<bool?> UpdateEqStateAsync()
        {
            return await IpcService.UpdateEqAsync();
        }

        public void UpdateSettings()
        {
            IpcService.UpdateSettings();
        }

        /// <summary>自动切歌单飞标记：防止重复 PlayEnded 通知导致并发触发两次切歌。</summary>
        private int _autoPlayInFlight;

        public async Task AutoPlayNextTrack()
        {
            if (Interlocked.Exchange(ref _autoPlayInFlight, 1) != 0) return;
            try
            {
                AppViewModel.StopProgressTimer();
                switch (AppViewModel.CurrentPlayMode)
                {
                    case PlayMode.SingleLoop:
                        await MusicBrowsePlayMusic(AppViewModel.CurrentPlayingMusic);
                        break;
                    case PlayMode.ListLoop:
                    case PlayMode.RandomLoop:
                        int currentIndex = AppViewModel.GetCurrentIndex();
                        int nextIndex = (currentIndex + 1) % AppViewModel.CurrentPlayingList.Count;
                        await MusicBrowsePlayMusic(AppViewModel.CurrentPlayingList[nextIndex]);
                        break;
                    case PlayMode.RepeatOff:
                        MusicEnd();
                        break;
                }
            }
            finally
            {
                Interlocked.Exchange(ref _autoPlayInFlight, 0);
            }
        }

        private static async Task MusicBrowsePlayMusic(Music music)
        {
            await App.Services.GetRequiredService<MusicBrowseViewModel>().PlayMusic(music);
        }

        public void MusicEnd()
        {
            try
            {
                App.Services.GetService<PlaybackStatsService>()?.FlushSession();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "结算播放统计失败: {Message}", ex.Message);
            }
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
                int currentIndex = AppViewModel.GetCurrentIndex();
                int nextIndex = (currentIndex + 1) % AppViewModel.CurrentPlayingList.Count;
                MusicBrowsePlayMusic(AppViewModel.CurrentPlayingList[nextIndex]);
            }
            catch (Exception ex) { _logger.LogError(ex, $"PlayNextTrack failed: {ex.Message}"); }
        }

        public void PlayMusic(Music music)
        {
            IpcService.Play(music.Path);
            AppViewModel.StartProgressTimer();
        }

        public async Task PlayButton()
        {
            try
            {
                bool? state = await IpcService.PlayButton();
                if (state is bool s)
                {
                    App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                    {
                        AppViewModel.IsPlaying = s;
                        if (s) AppViewModel.StartProgressTimer();
                        else AppViewModel.StopProgressTimer();
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PlayButton failed");
            }
        }

        public void ChangeWaveChannelTime(long positionMs)
        {
            IpcService.SetPosition(positionMs);
        }

        public void SetVolume(double volume)
        {
            IpcService.ChangeVolume(volume);
        }

        public async Task<(long currentMs, long totalMs)?> GetTimeProgress()
        {
            try
            {
                return await IpcService.GetTimeProgress();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"GetTimeProgress failed: {ex.Message}");
                return null;
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
