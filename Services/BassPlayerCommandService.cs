using ManagedBass;
using ManagedBass.Asio;
using ManagedBass.Dsd;
using ManagedBass.Fx;
using ManagedBass.Wasapi;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Extensions;
using WinUIMusicPlayer.Manager;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Utils;
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
        public float volume = 0.5f;
        public bool isInitializing = true;
        private readonly NotificationService notificationService;
        private readonly StringBuilder _timeStringBuilder = new StringBuilder(16);
        public MusicBrowseViewModel MusicBrowseViewModel { get; }
        private readonly Lock _streamLock = new();
        private readonly Lock _waveChannelLock = new();
        private readonly int[] _bandIndices = new int[10];
        private readonly float[] _eqFrequencies = { 32, 64, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 }; // 10频段
        private double MinDb = -60;
        private double MaxDb = 0;
        private double MiddleDb = -30;
        private PeakEQ _peakEQ;
        private IpcService IpcService { get; set; }

        public BassPlayerCommandService(NotificationService notificationService)
        {
            this.notificationService = notificationService;
            IpcService = App.Services.GetRequiredService<IpcService>();
            MusicBrowseViewModel = App.Services.GetRequiredService<MusicBrowseViewModel>();
            InitializingData();
            //BassManager.Initialize();
            //_syncEndCallback = OnPlayBackEnded;
            //_syncFailCallback = OnPlaybackFailed;
            //_myWasapiProcedure = OnWasapiProc;
            //_myAsioProcedure = OnAsioProc;
        }

        public async void SetMusicUrl(string musicUrl) {
            IpcService.SetMusicUrl(musicUrl);
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

        //private int OnWasapiProc(IntPtr buffer, int length, IntPtr user)
        //{
        //    if (_currentStream != 0)
        //    {
        //        return Bass.ChannelGetData(_currentStream, buffer, length);
        //    }
        //    return 0;
        //}

        //public void ToggleEqualizer()
        //{
        //    if (AppSettings.IsEqualizerEnabled
        //        && !(AppSettings.IsDopEnabled
        //        && (AppSettings.OutputMode.Contains("WasapiExclusive") || AppSettings.OutputMode == "ASIO")
        //        && (MusicBrowseViewModel.CurrentPlayingMusic.Extension.Equals("dsf", StringComparison.OrdinalIgnoreCase) || MusicBrowseViewModel.CurrentPlayingMusic.Extension.Equals("dff", StringComparison.OrdinalIgnoreCase)))
        //       )
        //    {
        //        try
        //        {
        //            if (_currentStream != 0)
        //            {
        //                _peakEQ = new PeakEQ(_currentStream, Q: 0, Bandwith: 1.0);
        //                // 为每个频段添加Band
        //                for (int i = 0; i < _eqFrequencies.Length; i++)
        //                {
        //                    _bandIndices[i] = _peakEQ.AddBand(_eqFrequencies[i]);
        //                }
        //            }
        //        }
        //        catch (Exception ex)
        //        {
        //            Debug.WriteLine($"初始化均衡器时出错: {ex.Message}");
        //            _peakEQ = null;
        //        }
        //    }
        //}

        //public void SetEqualizerGain(int bandIndex, float gain)
        //{
        //    if (bandIndex < 0 || bandIndex >= _eqFrequencies.Length)
        //    {
        //        return;
        //    }
        //    if (_peakEQ == null)
        //    {
        //        return;
        //    }
        //    try
        //    {
        //        // 使用UpdateBand方法更新指定频段的增益
        //        _peakEQ.UpdateBand(_bandIndices[bandIndex], gain);
        //    }
        //    catch (Exception ex)
        //    {
        //        Debug.WriteLine($"设置均衡器参数失败: {ex.Message}");
        //    }
        //}

        //public void SetEqualizer()
        //{
        //    if (_peakEQ is null) return;
        //    for (int i = 0; i < 10; i++)
        //    {
        //        _peakEQ.UpdateBand(_bandIndices[i], (float)AppSettings.equalizer[FloatToString[_eqFrequencies[i]]]);
        //    }
        //}

        //public void ClearEqualizer()
        //{
        //    DisposeEq();
        //}

        //private bool SwitchDevice(ChannelInfo channelInfo)
        //{
        //    bool result = false;
        //    switch (AppSettings.OutputMode)
        //    {
        //        case "WasapiShared":
        //            result = BassWasapi.Init(AppSettings.BassOutputDeviceId,
        //                    channelInfo.Frequency,
        //                    channelInfo.Channels,
        //                    WasapiInitFlags.Shared,
        //                    AppSettings.Latency / 1000.0f, 0, _myWasapiProcedure);
        //            break;
        //        case "WasapiExclusivePush":
        //            result = BassWasapi.Init(AppSettings.BassOutputDeviceId,
        //                    channelInfo.Frequency,
        //                    channelInfo.Channels,
        //                    WasapiInitFlags.Exclusive,
        //                    AppSettings.Latency / 1000.0f, AppSettings.Latency / 8000.0f, _myWasapiProcedure);
        //            break;
        //        case "WasapiExclusiveEvent":
        //            result = BassWasapi.Init(AppSettings.BassOutputDeviceId,
        //                    channelInfo.Frequency,
        //                    channelInfo.Channels,
        //                    WasapiInitFlags.Exclusive | WasapiInitFlags.EventDriven,
        //                    AppSettings.Latency / 1000.0f, AppSettings.Latency / 8000.0f, _myWasapiProcedure);
        //            break;
        //        case "ASIO":
        //            result = BassAsio.Init(AppSettings.BassASIODeviceId, AsioInitFlags.Thread);
        //            break;
        //    }
        //    if (AppSettings.OutputMode.Contains("Wasapi"))
        //    {
        //        BassWasapi.GetInfo(out var info);
        //        MaxDb = info.MaxVolume;
        //        MinDb = info.MinVolume;
        //        MiddleDb = (MinDb + MaxDb) / 2;
        //    }
        //    return result;
        //}
        //private int OnAsioProc(bool input, int channel, IntPtr buffer, int length, IntPtr user)
        //{
        //    if (_currentStream != 0)
        //    {
        //        return Bass.ChannelGetData(user.ToInt32(), buffer, length);
        //    }
        //    return 0;
        //}
        //private bool InitializePlayback()
        //{
        //    try
        //    {
        //        // 初始化播放模式
        //        Bass.ChannelGetInfo(_currentStream, out var channelInfo);
        //        var result = SwitchDevice(channelInfo);
        //        if (!result)
        //        {
        //            StopWasapiPlayback();
        //            StopAsioPlayback();
        //            result = SwitchDevice(channelInfo);
        //            if (!result)
        //            {
        //                return false;
        //            }
        //        }
        //        // 设置音量
        //        if (AppSettings.OutputMode.Contains("WasapiShared"))
        //        {
        //            BassWasapi.SetVolume(WasapiVolumeTypes.Session, (float)volume);
        //        }
        //        else if (AppSettings.OutputMode.Contains("WasapiExclusive"))
        //        {
        //            if (volume > 0.7)
        //            {
        //                volume = (float)DbToLinear(MiddleDb);
        //                BassWasapi.SetVolume(WasapiVolumeTypes.LogaritmicCurve, (float)MiddleDb);
        //                App.MainWindow.DispatcherQueue.TryEnqueue(() =>
        //                {
        //                    MusicBrowseViewModel.Volume = volume * 100;
        //                });
        //            }
        //            else
        //            {
        //                BassWasapi.SetVolume(WasapiVolumeTypes.LogaritmicCurve, (float)LinearToDb(volume));
        //            }
        //        }
        //        else if (AppSettings.OutputMode == "ASIO")
        //        {

        //            if (AppSettings.IsDopEnabled
        //                && (MusicBrowseViewModel.CurrentPlayingMusic.Extension.Equals("dsf", StringComparison.OrdinalIgnoreCase)
        //                || MusicBrowseViewModel.CurrentPlayingMusic.Extension.Equals("dff", StringComparison.OrdinalIgnoreCase))
        //                )
        //            {
        //                Bass.ChannelGetAttribute(_currentStream, ChannelAttribute.DSDRate, out float dsdRate);
        //                if (!BassAsio.SetDSD(true)) return false;
        //                BassAsio.Rate = dsdRate;
        //                if (!BassAsio.ChannelSetFormat(false, 0, AsioSampleFormat.DSD_MSB)) return false;
        //                if (!BassAsio.ChannelEnable(false, 0, _myAsioProcedure, new IntPtr(_currentStream))) return false;
        //                if (!BassAsio.ChannelJoin(false, 1, 0)) return false;
        //            }
        //            else
        //            {
        //                if (!BassAsio.ChannelEnableBass(false, 0, _currentStream, true)) return false;
        //                if (!BassAsio.ChannelSetFormat(false, 0, AsioSampleFormat.Float)) return false;
        //                BassAsio.Rate = channelInfo.Frequency;
        //            }
        //            BassAsio.ChannelSetVolume(false, -1, volume);
        //        }
        //        Debug.WriteLine($"WASAPI模式启动成功");
        //        return true;
        //    }
        //    catch (Exception ex)
        //    {
        //        Debug.WriteLine(ex, $"启动WASAPI独占模式时出错");
        //        return false;
        //    }
        //}

        //private void SetSource(Music music)
        //{
        //    try
        //    {
        //        DisposeStream();
        //        BassDsd.DefaultGain = AppSettings.dsdGain;
        //        BassDsd.DefaultFrequency = AppSettings.dsdPcmFreq;
        //        if (AppSettings.OutputMode.Contains("WasapiExclusive"))
        //        {
        //            if (AppSettings.IsDopEnabled && (music.Extension.Equals("dsf", StringComparison.OrdinalIgnoreCase) || music.Extension.Equals("dff", StringComparison.OrdinalIgnoreCase)))
        //            {
        //                _currentStream = BassDsd.CreateStream(music.Path, 0, 0, BassFlags.DSDOverPCM | BassFlags.Float | BassFlags.Decode | BassFlags.AsyncFile);
        //            }
        //            else
        //            {
        //                _currentStream = Bass.CreateStream(music.Path, 0, 0, BassFlags.Unicode | BassFlags.Float | BassFlags.AsyncFile | BassFlags.Decode);
        //            }
        //        }
        //        else if (AppSettings.OutputMode.Contains("WasapiShared"))
        //        {
        //            _currentStream = Bass.CreateStream(music.Path, 0, 0, BassFlags.Unicode | BassFlags.Float | BassFlags.AsyncFile | BassFlags.Decode);
        //        }
        //        else if (AppSettings.OutputMode == "ASIO")
        //        {
        //            if (AppSettings.IsDopEnabled && (music.Extension.Equals("dsf", StringComparison.OrdinalIgnoreCase) || music.Extension.Equals("dff", StringComparison.OrdinalIgnoreCase)))
        //            {
        //                _currentStream = BassDsd.CreateStream(music.Path, 0, 0, BassFlags.DSDRaw | BassFlags.Decode | BassFlags.AsyncFile);
        //            }
        //            else
        //            {
        //                _currentStream = Bass.CreateStream(music.Path, 0, 0, BassFlags.Float | BassFlags.AsyncFile | BassFlags.Decode);
        //            }
        //        }
        //        else
        //        {
        //            _currentStream = Bass.CreateStream(music.Path, 0, 0, BassFlags.Default | BassFlags.AsyncFile);
        //        }
        //        if (_currentStream == 0)
        //        {
        //            notificationService.SendNotification(ToolUtils.GetString("Error"), $"创建流失败: {Bass.LastError}");
        //            return;
        //        }
        //        Bass.ChannelSetSync(_currentStream, SyncFlags.End, 0, _syncEndCallback); // 设置播放结束回调
        //        Bass.ChannelSetSync(_currentStream, SyncFlags.Stalled, 0, _syncFailCallback); // 设置播放失败回调
        //        ToggleEqualizer();
        //        // 根据模式设置音量
        //        if (!AppSettings.OutputMode.Contains("Wasapi") && AppSettings.OutputMode != "ASIO")
        //        {
        //            Bass.ChannelSetAttribute(
        //                _currentStream,
        //                ChannelAttribute.Volume,
        //                volume
        //            );
        //        }
        //        //_totalSeconds = Bass.ChannelBytes2Seconds(_currentStream, Bass.ChannelGetLength(_currentStream));
        //    }
        //    catch (Exception ex)
        //    {
        //        Debug.WriteLine($"SetSource异常: {ex.Message}");
        //    }
        //}

        public void PlayMusic(Music music)
        {
            IpcService.UpdateSettings();
            IpcService.Play(music.Path);
            //MusicBrowseViewModel.StartProgressTimer();
        }

        //public void Stop()
        //{
        //    if (_currentStream != 0)
        //    {
        //        Bass.ChannelStop(_currentStream);
        //        MusicBrowseViewModel.StopProgressTimer();
        //        AppSettings.isPlaying = false;
        //        App.MainWindow.DispatcherQueue.TryEnqueue(() =>
        //        {
        //            MusicBrowseViewModel.IsPlaying = false;
        //        });
        //    }
        //}

        public void PlayButton()
        {
            IpcService.UpdateSettings();
            IpcService.PlayButton();
            //if (AppSettings.isPlaying)
            //{
            //    if (AppSettings.OutputMode.Contains("Wasapi"))
            //    {
            //        BassWasapi.Stop();
            //    }
            //    else if (AppSettings.OutputMode == "ASIO")
            //    {
            //        BassAsio.Stop();
            //    }
            //    else
            //    {
            //        Bass.ChannelStop(_currentStream);
            //    }
            //    isPausing = true;
            //    AppSettings.isPlaying = false;
            //    App.MainWindow.DispatcherQueue.TryEnqueue(() =>
            //    {
            //        MusicBrowseViewModel.IsPlaying = false;
            //    });
            //    MusicBrowseViewModel.StopProgressTimer();
            //}
            //else
            //{
            //    if (_currentStream != 0)
            //    {
            //        if (AppSettings.OutputMode.Contains("Wasapi"))
            //        {
            //            BassWasapi.Start();
            //        }
            //        else if (AppSettings.OutputMode == "ASIO")
            //        {
            //            BassAsio.Start();
            //        }
            //        else
            //        {
            //            Bass.ChannelPlay(_currentStream, false);
            //        }
            //    }
            //    else
            //    {
            //        if (MusicBrowseViewModel.CurrentPlayingMusic is not null)
            //        {
            //            MusicBrowseViewModel.PlayMusic(MusicBrowseViewModel.CurrentPlayingMusic);
            //        }
            //        else if (MusicBrowseViewModel.CurrentPlayingList is not null && MusicBrowseViewModel.CurrentPlayingList.Count > 0)
            //        {
            //            MusicBrowseViewModel.PlayMusic(MusicBrowseViewModel.CurrentPlayingList[0]);
            //        }
            //        else
            //        {
            //            notificationService.SendNotification(ToolUtils.GetString("Error"), "没有可播放的音乐");
            //            return;
            //        }
            //    }
            //    isPausing = false;
            //    AppSettings.isPlaying = true;
            //    App.MainWindow.DispatcherQueue.TryEnqueue(() =>
            //    {
            //        MusicBrowseViewModel.IsPlaying = true;
            //    });
            //    MusicBrowseViewModel.StartProgressTimer();
            //}
        }

        //public void Play(bool isSettingChanged = false)
        //{
        //    if (_currentStream != 0)
        //    {
        //        if (AppSettings.OutputMode.Contains("Wasapi"))
        //        {
        //            // 独占模式下使用WASAPI播放
        //            if (InitializePlayback())
        //            {
        //                BassWasapi.Start();
        //            }
        //            else
        //            {
        //                // 如果独占模式启动失败，回退到共享模式
        //                Bass.ChannelPlay(_currentStream, false);
        //            }
        //        }
        //        else if (AppSettings.OutputMode == "ASIO")
        //        {
        //            if (InitializePlayback())
        //            {
        //                BassAsio.Start();
        //            }
        //            else
        //            {
        //                // 如果ASIO模式启动失败，回退到共享模式
        //                Bass.ChannelPlay(_currentStream, false);
        //            }
        //        }
        //        else
        //        {
        //            // 共享模式下直接播放
        //            Bass.ChannelPlay(_currentStream, false);
        //        }
        //        if (AppSettings.IsEqualizerEnabled)
        //        {
        //            SetEqualizer();
        //        }
        //        MusicBrowseViewModel.StartProgressTimer();
        //        App.MainWindow.DispatcherQueue.TryEnqueue(() =>
        //        {
        //            try
        //            {
        //                MusicBrowseViewModel.ProgressSliderMax = Bass.ChannelBytes2Seconds(_currentStream, Bass.ChannelGetLength(_currentStream));
        //                if (isSettingChanged)
        //                {
        //                    MusicBrowseViewModel.ProgressSlider = Bass.ChannelBytes2Seconds(_currentStream, Bass.ChannelGetPosition(_currentStream));
        //                }
        //                else
        //                {
        //                    MusicBrowseViewModel.ProgressSlider = 0;
        //                }
        //                AppSettings.isPlaying = true;
        //                MusicBrowseViewModel.IsPlaying = true;
        //                MusicBrowseViewModel.UpdatePlayPauseButtonIcon();
        //                _ = MusicDatabaseService.SavePlayState([.. MusicBrowseViewModel.SequentialPlayingList], AppData.PlayMode, MusicBrowseViewModel.CurrentPlayingMusic?.Id, volume, AppData.sortOrder);
        //            }
        //            catch (Exception)
        //            {
        //            }
        //        });
        //    }
        //}

        public void ChangeWaveChannelTime(TimeSpan timeSpan)
        {
            //IpcService.SetPosition(timeSpan.TotalSeconds);
            //lock (_waveChannelLock)
            //{
            //    if (_currentStream != 0)
            //    {
            //        var targetBytes = Bass.ChannelSeconds2Bytes(_currentStream, timeSpan.TotalSeconds);
            //        Bass.ChannelSetPosition(_currentStream, targetBytes);
            //    }
            //}
        }

        public void SetVolume(double volume)
        {
            //if (_currentStream != 0)
            //{
            //    if (AppSettings.OutputMode.Contains("WasapiExclusive"))
            //    {
            //        BassWasapi.SetVolume(WasapiVolumeTypes.LogaritmicCurve, (float)LinearToDb(volume));
            //    }
            //    else if (AppSettings.OutputMode.Contains("WasapiShared"))
            //    {
            //        BassWasapi.SetVolume(WasapiVolumeTypes.Session, (float)volume);
            //    }
            //    else if (AppSettings.OutputMode == "ASIO")
            //    {
            //        BassAsio.ChannelSetVolume(false, -1, volume);
            //    }
            //    else
            //    {
            //        Bass.ChannelSetAttribute(_currentStream, ChannelAttribute.Volume, volume);
            //    }
            //}
        }

        public async Task<double> GetCurrentPosition()
        {
            //if (_currentStream != 0)
            //{
            //    var positionBytes = Bass.ChannelGetPosition(_currentStream);
            //    return Bass.ChannelBytes2Seconds(_currentStream, positionBytes);
            //}
            return await IpcService.GetCurrentPostion();
        }

        public async Task<double> GetTotalPosition()
        {
            //if (_currentStream != 0)
            //{
            //    var totalBytes = Bass.ChannelGetLength(_currentStream);
            //    return Bass.ChannelBytes2Seconds(_currentStream, totalBytes);
            //}
            return await IpcService.GetDuration();
        }

        public double AdjustPlaybackPosition(int seconds)
        {
            //double newPosition = 0;
            //if (AppSettings.isPlaying)
            //{
            //    if (_currentStream != 0)
            //    {
            //        newPosition = GetCurrentPosition() + seconds;
            //        newPosition = Math.Max(0, Math.Min(newPosition, GetTotalPosition()));
            //        ChangeWaveChannelTime(TimeSpan.FromSeconds(newPosition));
            //    }
            //}
            //return newPosition;
            return 0;
        }

        public void ChangingSetting()
        {
            IpcService.UpdateSettings();
            //try
            //{
            //    //isManualPlayingNext = true;
            //    lock (_streamLock)
            //    {
            //        var currentTime = GetCurrentPosition();
            //        if (AppSettings.isPlaying)
            //        {
            //            Stop();
            //            SetSource(MusicBrowseViewModel.CurrentPlayingMusic);
            //            Play(true);
            //        }
            //        else
            //        {
            //            SetSource(MusicBrowseViewModel.CurrentPlayingMusic);
            //        }
            //        ChangeWaveChannelTime(TimeSpan.FromSeconds(currentTime));
            //    }
            //}
            //catch (Exception ex)
            //{
            //    notificationService.SendNotification(ToolUtils.GetString("Error"), ex.Message);
            //}

        }

        //public double LinearToDb(double linearValue)
        //{
        //    if (linearValue <= 0)
        //        return MinDb;
        //    if (linearValue >= 1)
        //    {
        //        return MaxDb;
        //    }
        //    // 映射到0到-65.25dB的范围
        //    double dbValue = MaxDb + (MinDb - MaxDb) * (1 - Math.Log10(9 * linearValue + 1) / Math.Log10(10));
        //    return dbValue;
        //}

        //public double DbToLinear(double dbValue)
        //{
        //    dbValue = Math.Clamp(dbValue, MinDb, MaxDb);
        //    if (dbValue <= MinDb)
        //        return 0;
        //    double dbPosition = (dbValue - MaxDb) / (MinDb - MaxDb);
        //    return (Math.Pow(10, (1 - dbPosition) * Math.Log10(10)) - 1) / 9;
        //}

        //private void DisposeStream()
        //{
        //    if (_currentStream != 0)
        //    {
        //        Bass.StreamFree(_currentStream);
        //        _currentStream = 0;
        //    }
        //    StopWasapiPlayback();
        //    StopAsioPlayback();
        //    DisposeEq();
        //}

        //private void StopWasapiPlayback()
        //{
        //    try
        //    {
        //        if (BassWasapi.IsStarted)
        //        {
        //            BassWasapi.Stop(true);
        //        }
        //        BassWasapi.Free();
        //        AppSettings.isPlaying = false;
        //    }
        //    catch (Exception ex)
        //    {
        //        Debug.WriteLine(ex, $"停止WASAPI播放时出错");
        //    }
        //}
        //private void StopAsioPlayback()
        //{
        //    try
        //    {
        //        if (BassAsio.IsStarted)
        //        {
        //            BassAsio.Stop();
        //        }
        //        var asioFree = BassAsio.Free();
        //        if (!asioFree)
        //        {
        //            Debug.WriteLine($"释放ASIO失败: {Bass.LastError}");
        //        }
        //        else
        //        {
        //            Debug.WriteLine($"释放ASIO成功");
        //        }
        //        AppSettings.isPlaying = false;
        //    }
        //    catch (Exception ex)
        //    {
        //        Debug.WriteLine(ex, $"停止ASIO播放时出错");
        //    }
        //}
        //public void DisposeEq()
        //{
        //    _peakEQ?.Dispose();
        //    _peakEQ = null;
        //}

        //public void Dispose()
        //{
        //    DisposeEq();
        //    DisposeStream();
        //    BassManager.Free();
        //}
    }
}
