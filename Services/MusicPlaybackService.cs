using CSCore;
using CSCore.Ffmpeg;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Reader;
using static WinUIMusicPlayer.Utils.ToolUtils;

namespace WinUIMusicPlayer.Services
{
    public class MusicPlaybackService
    {
        public Music currentPlayingMusic;
        private System.Timers.Timer progressTimer;
        public List<Music> currentPlayingList;
        public MultiTypeAudioReader multiTypeAudioReader;
        public IWavePlayer waveOut;
        public CSCore.SoundOut.WasapiOut wasapiOut;
        private MMDevice selectedDevice = null;
        private CSCore.CoreAudioAPI.MMDevice csCoreMMdevice = null;
        public IWaveSource ffmpegDecoder;
        public int? lastPlayedMusicId;
        public bool isManualSelect = false;
        public bool isPausing = false;
        public bool isSettingsChangeStop = false;
        public float volume = 0.5f;
        public event EventHandler<Music> playingMusic;
        public event EventHandler<string> updatePlayTimeText;
        public event EventHandler<double> updateProgressSliders;
        public event EventHandler<double> updateProgressMax;
        public event EventHandler<string> showMessage;
        public event EventHandler<string> updatePlayPauseButton;
        public PlayMode currentPlayMode = PlayMode.ListLoop;
        public List<Music> musicList;
        public bool isUserDraggingProgressSlider = false;
        public bool isInitializing = true;
        private NotificationService notificationService = new NotificationService();

        public MusicPlaybackService()
        {
            progressTimer = new System.Timers.Timer(1000);
            progressTimer.Elapsed += ProgressTimer_Elapsed;
            InitializingData();
        }

        private async void InitializingData() {
            currentPlayingList = await MusicDatabaseService.LoadPlayList();
        }

        private void ProgressTimer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            try
            {
                if (AppSettings.isPlaying && !isUserDraggingProgressSlider)
                {
                    double currentTimeSeconds = 0;
                    double totalSeconds = 0;

                    if (multiTypeAudioReader != null)
                    {
                        currentTimeSeconds = multiTypeAudioReader.CurrentTime.TotalSeconds;
                        totalSeconds = multiTypeAudioReader.TotalTime.TotalSeconds;
                    }
                    else if (ffmpegDecoder != null)
                    {
                        currentTimeSeconds = (double)ffmpegDecoder.Position / ffmpegDecoder.WaveFormat.BytesPerSecond;
                        totalSeconds = (double)ffmpegDecoder.Length / ffmpegDecoder.WaveFormat.BytesPerSecond;
                    }
                    updateProgressSliders?.Invoke(this, currentTimeSeconds);

                    // 格式化显示时间
                    TimeSpan currentTime = TimeSpan.FromSeconds(currentTimeSeconds);
                    TimeSpan totalTime = TimeSpan.FromSeconds(totalSeconds);
                    string currentTimeText = currentTime.ToString(@"mm\:ss");
                    string totalTimeText = totalTime.ToString(@"mm\:ss");

                    updatePlayTimeText?.Invoke(this, $"{currentTimeText}/{totalTimeText}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"进度条更新失败: {ex.Message}");
            }
        }

        public double AdjustPlaybackPosition(int seconds)
        {
            double newPosition = 0;
            if (AppSettings.isPlaying)
            {
                if (multiTypeAudioReader != null)
                {
                    // 对于FLAC文件，计算新位置
                    double currentTimeSeconds = multiTypeAudioReader.CurrentTime.TotalSeconds;
                    double totalSeconds = multiTypeAudioReader.TotalTime.TotalSeconds;

                    newPosition = currentTimeSeconds + seconds;
                    newPosition = Math.Max(0, Math.Min(newPosition, totalSeconds));

                    // 设置新位置
                    multiTypeAudioReader.CurrentTime = TimeSpan.FromSeconds(newPosition);
                }
                else if (ffmpegDecoder != null)
                {
                    // 对于其他格式，使用audioFileReader

                    newPosition = (double)ffmpegDecoder.Position / ffmpegDecoder.WaveFormat.BytesPerSecond + seconds;
                    newPosition = Math.Max(0, Math.Min(newPosition, (double)ffmpegDecoder.Length / ffmpegDecoder.WaveFormat.BytesPerSecond));
                    ffmpegDecoder.Position = (long)(newPosition * ffmpegDecoder.WaveFormat.BytesPerSecond);
                }
            }
            return newPosition;
        }

        public void ChangingSetting()
        {
            try
            {
                // 如果当前正在播放，停止播放并重新初始化音频资源
                if (AppSettings.isPlaying)
                {
                    isSettingsChangeStop = true;
                    if (progressTimer != null)
                    {
                        progressTimer.Stop();
                    }
                    if (waveOut != null)
                    {
                        waveOut.Stop();
                        waveOut.Dispose();
                        waveOut = null;
                    }
                    if (wasapiOut != null)
                    {
                        wasapiOut.Stop();
                        wasapiOut.Dispose();
                        wasapiOut = null;
                    }
                    if (selectedDevice != null)
                    {
                        selectedDevice.Dispose();
                        selectedDevice = null;
                    }
                    if (csCoreMMdevice != null)
                    {
                        csCoreMMdevice.Dispose();
                        csCoreMMdevice = null;
                    }
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    OutputDeviceChange();
                    if (multiTypeAudioReader != null)
                    {
                        ResumeMusic();
                    }
                    CScoreOutputDevice();
                    if (ffmpegDecoder != null)
                    {                        
                        ResumeMusic();
                    }
                }
                else
                {
                    isSettingsChangeStop = true;
                    if (selectedDevice != null)
                    {
                        selectedDevice.Dispose();
                        selectedDevice = null;
                    }
                    if (csCoreMMdevice != null)
                    {
                        csCoreMMdevice.Dispose();
                        csCoreMMdevice = null;
                    }
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    OutputDeviceChange();                    
                    CScoreOutputDevice();
                    
                }
            }
            catch (Exception ex)
            {
                showMessage?.Invoke(this, $"播放失败{ex.Message}");
                System.Diagnostics.Debug.WriteLine($"错误: {ex.Message}");
            }

        }

        public void ResumeMusic()
        {
            if (multiTypeAudioReader != null)
            {
                SelectOutputDevice();
                waveOut.Init(multiTypeAudioReader);
                waveOut.PlaybackStopped += WaveOut_PlaybackStopped;
                waveOut.Play();
                AppSettings.isDsd = false;
            }
            else if (ffmpegDecoder != null)
            {
                try
                {
                    SelectCSCoreOutputDevice();
                    wasapiOut.Device = csCoreMMdevice;
                    wasapiOut.Initialize(ffmpegDecoder);
                    wasapiOut.Volume = volume;
                    wasapiOut.Stopped += wasapiOut_Stopped;
                    wasapiOut.Play();
                }
                catch (Exception e)
                {
                    //wasapiOut.Dispose();
                    //wasapiOut = null;
                    //wasapiOut = new CSCore.SoundOut.WasapiOut(false, CSCore.CoreAudioAPI.AudioClientShareMode.Shared, AppSettings.Latency);
                    //wasapiOut.Device = csCoreMMdevice;
                    //wasapiOut.Initialize(ffmpegDecoder);
                    //wasapiOut.Volume = volume;
                    //wasapiOut.Stopped += wasapiOut_Stopped;
                    //wasapiOut.Play();
                    notificationService.SendNotification("DSD独占播放失败", "请尝试切换至共享模式");
                }
                AppSettings.isDsd = true;
            }
            AppSettings.isPlaying = true;
            progressTimer.Start();
        }

        public void OutputDeviceChange()
        {
            using (var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator())
            {
                var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                if (AppSettings.DeviceName != null)
                {
                    foreach (var device in devices)
                    {
                        if (device.FriendlyName == AppSettings.DeviceName)
                        {
                            selectedDevice = device;
                            break;
                        }
                    }
                }
                else
                {
                    selectedDevice = devices[0];
                }
            }
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        public void CScoreOutputDevice()
        {
            using (var csCoreEnumerator = new CSCore.CoreAudioAPI.MMDeviceEnumerator())
            {
                using (var devices = csCoreEnumerator.EnumAudioEndpoints(CSCore.CoreAudioAPI.DataFlow.Render, CSCore.CoreAudioAPI.DeviceState.Active))
                {
                    if (AppSettings.DeviceName != null)
                    {
                        foreach (var device in devices)
                        {
                            if (device.FriendlyName == AppSettings.DeviceName)
                            {
                                csCoreMMdevice = device;
                                break;
                            }
                        }
                    }
                    else
                    {
                        csCoreMMdevice = devices[0];
                    }
                }
            }
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        public void SelectOutputDevice()
        {
            switch (AppSettings.OutputMode)
            {
                case "WaveOut":
                    waveOut = new WaveOutEvent();
                    break;
                case "WasapiShared":
                    waveOut = new NAudio.Wave.WasapiOut(selectedDevice, AudioClientShareMode.Shared, false, AppSettings.Latency);
                    break;
                case "WasapiExclusive":
                    waveOut = new NAudio.Wave.WasapiOut(selectedDevice, AudioClientShareMode.Exclusive, true, AppSettings.Latency);
                    break;
                case "DirectSound":
                    waveOut = new NAudio.Wave.DirectSoundOut(AppSettings.Latency);
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
        }

        public void SelectCSCoreOutputDevice()
        {
            switch (AppSettings.OutputMode)
            {
                case "WasapiShared":
                    wasapiOut = new CSCore.SoundOut.WasapiOut(false, CSCore.CoreAudioAPI.AudioClientShareMode.Shared, AppSettings.Latency);
                    break;
                case "WasapiExclusive":
                    wasapiOut = new CSCore.SoundOut.WasapiOut(false, CSCore.CoreAudioAPI.AudioClientShareMode.Exclusive, AppSettings.Latency);
                    break;
                default:
                    wasapiOut = new CSCore.SoundOut.WasapiOut();
                    break;
            }
        }

        public async void WaveOut_PlaybackStopped(object sender, NAudio.Wave.StoppedEventArgs e)
        {
            bool isNaturalEnd = false;
            if (multiTypeAudioReader != null && !isPausing && !isManualSelect && !isSettingsChangeStop)
            {
                double currentPositionSeconds = multiTypeAudioReader.CurrentTime.TotalSeconds;
                double totalDurationSeconds = multiTypeAudioReader.TotalTime.TotalSeconds;
                isNaturalEnd = (totalDurationSeconds - currentPositionSeconds) < 0.5;
            }

            if (isPausing)
            {
                return;
            }

            if (isManualSelect)
            {
                isManualSelect = false;
                return;
            }

            if (isSettingsChangeStop)
            {
                isSettingsChangeStop = false;
                return;
            }

            if (isNaturalEnd)
            {
                await AutoPlayNextTrack();
            }
        }

        public async Task AutoPlayNextTrack()
        {
            if (progressTimer != null)
            {
                progressTimer.Stop();
            }
            switch (currentPlayMode)
            {
                case PlayMode.SingleLoop:
                    playingMusic?.Invoke(this, currentPlayingMusic);
                    break;
                case PlayMode.ListLoop:
                    int currentIndex = currentPlayingList.FindIndex(m => m.Id == currentPlayingMusic.Id);
                    int nextIndex = (currentIndex + 1) % currentPlayingList.Count;
                    playingMusic?.Invoke(this, currentPlayingList[nextIndex]);
                    break;
                case PlayMode.RandomLoop:
                    Random random = new Random();
                    int randomIndex = random.Next(currentPlayingList.Count);
                    playingMusic?.Invoke(this, currentPlayingList[randomIndex]);
                    break;
            }
        }

        public async Task<bool> InitializeAudioResources(Music music, TimeSpan currentPos = new TimeSpan())
        {
            try
            {
                if (!File.Exists(music.Path)) {
                    notificationService.SendNotification("文件不存在", music.Path);
                    return false;
                }
                Reset();
                if (music.Extension.ToLower() != "dsf" && music.Extension.ToLower() != "dff")
                {
                    try
                    {
                        SelectOutputDevice();
                        multiTypeAudioReader = new MultiTypeAudioReader(music.Path);
                        multiTypeAudioReader.CurrentTime = currentPos;
                        multiTypeAudioReader.Volume = volume;
                        waveOut.Init(multiTypeAudioReader);
                    }
                    catch (Exception e)
                    {
                        Debug.WriteLine(e.Message);
                    }
                }
                else
                {
                    try
                    {
                        SelectCSCoreOutputDevice();
                        ffmpegDecoder = new FfmpegDecoder(music.Path);                        
                        wasapiOut.Device = csCoreMMdevice;
                        wasapiOut.Initialize(ffmpegDecoder);
                    }
                    catch (Exception e)
                    {
                        //wasapiOut.Dispose();
                        //wasapiOut = null;
                        //wasapiOut = new CSCore.SoundOut.WasapiOut(false, CSCore.CoreAudioAPI.AudioClientShareMode.Shared, AppSettings.Latency);
                        //wasapiOut.Device = csCoreMMdevice;
                        //wasapiOut.Initialize(ffmpegDecoder);
                        //wasapiOut.Volume = volume;
                        notificationService.SendNotification("DSD独占播放失败", "请尝试切换至共享模式");
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                showMessage?.Invoke(this, $"播放失败{ex.Message}");
                System.Diagnostics.Debug.WriteLine($"错误: {ex.Message}");
                Reset();
                updateProgressSliders?.Invoke(this, 0);
                return false;
            }
        }

        private async void wasapiOut_Stopped(object? sender, CSCore.SoundOut.PlaybackStoppedEventArgs e)
        {
            bool isNaturalEnd = false;
            if (ffmpegDecoder != null && !isPausing && !isManualSelect && !isSettingsChangeStop)
            {
                double currentPositionSeconds = (double)ffmpegDecoder.Position / ffmpegDecoder.WaveFormat.BytesPerSecond;
                double totalDurationSeconds = (double)ffmpegDecoder.Length / ffmpegDecoder.WaveFormat.BytesPerSecond;
                isNaturalEnd = (totalDurationSeconds - currentPositionSeconds) < 0.5;
            }

            if (isPausing)
            {
                return;
            }

            if (isManualSelect)
            {
                isManualSelect = false;
                return;
            }

            if (isSettingsChangeStop)
            {
                isSettingsChangeStop = false;
                return;
            }

            if (isNaturalEnd)
            {
                await AutoPlayNextTrack();
            }
        }

        public async Task PlayMusic(Music music, TimeSpan currentPos = new TimeSpan(), bool isSettingChanged = false)
        {
            if (await InitializeAudioResources(music, currentPos))
            {
                try
                {
                    updatePlayPauseButton?.Invoke(this, "\uE769");
                    // 根据文件类型获取总时长
                    double totalSeconds = 0;
                    if (multiTypeAudioReader != null)
                    {
                        totalSeconds = multiTypeAudioReader.TotalTime.TotalSeconds;
                    }
                    if (ffmpegDecoder != null)
                    {
                        totalSeconds = (double)ffmpegDecoder.Length / ffmpegDecoder.WaveFormat.BytesPerSecond;
                    }
                    updateProgressMax?.Invoke(this, totalSeconds);
                    if (isSettingChanged)
                    {
                        updateProgressSliders?.Invoke(this, currentPos.TotalSeconds);
                    }
                    else
                    {
                        updateProgressSliders?.Invoke(this, 0);
                    }
                    if (music.Extension.ToLower() != "dsf" && music.Extension.ToLower() != "dff")
                    {
                        waveOut.Play();
                        waveOut.PlaybackStopped += WaveOut_PlaybackStopped;
                        AppSettings.isDsd = false;
                    }
                    else
                    {
                        wasapiOut.Volume = volume;
                        wasapiOut.Play();
                        wasapiOut.Stopped += wasapiOut_Stopped;
                        AppSettings.isDsd = true;
                        AppSettings.isPlaying = true;
                        //_ = Task.Run(() => SavePlayState());
                    }
                    progressTimer.Start();
                    AppSettings.isPlaying = true;
                    //_ = Task.Run(() => SavePlayState());
                }
                catch (Exception ex)
                {
                    showMessage?.Invoke(this, $"播放失败{ex.Message}");
                    System.Diagnostics.Debug.WriteLine($"错误: {ex.Message}");
                    Reset();
                }
            }
        }

        public void Reset()
        {
            if (waveOut != null)
            {
                waveOut.Stop();
                waveOut.Dispose();
                waveOut = null;
            }

            if (wasapiOut != null)
            {
                wasapiOut.Stop();
                wasapiOut.Dispose();
                wasapiOut = null;
            }

            if (multiTypeAudioReader != null)
            {
                multiTypeAudioReader.Dispose();
                multiTypeAudioReader = null;
            }

            if (ffmpegDecoder != null)
            {
                ffmpegDecoder.Dispose();
                ffmpegDecoder = null;
            }
            if (progressTimer != null)
            {
                progressTimer.Stop();
            }
        }        

        public async Task DisposeAudio()
        {            
            if (progressTimer != null)
            {
                progressTimer.Stop();
                progressTimer.Elapsed -= ProgressTimer_Elapsed;
                progressTimer.Dispose();
                progressTimer = null;
            }
            if (waveOut != null)
            {
                waveOut.Stop();
                waveOut.Dispose();
                waveOut = null;
            }

            if (wasapiOut != null)
            {
                wasapiOut.Stop();
                wasapiOut.Dispose();
                wasapiOut = null;
            }

            if (ffmpegDecoder != null) {
                ffmpegDecoder.Dispose();
                ffmpegDecoder = null;
            }

            if (multiTypeAudioReader != null)
            {
                multiTypeAudioReader.Dispose();
                multiTypeAudioReader = null;
            }
            await MusicDatabaseService.SavePlayList(currentPlayingList);
            await SavePlayState();
        }

        public void SwitchPlayMode()
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
        }

        public void StopPlaying()
        {
            if (waveOut != null)
            {
                waveOut.Stop();
                AppSettings.isPlaying = false;
                progressTimer.Stop();
            }

            if (wasapiOut != null)
            {
                wasapiOut.Stop();
                AppSettings.isPlaying = false;
                progressTimer.Stop();
            }
        }

        public void PlayButton()
        {
            if (AppSettings.isPlaying)
            {
                if (waveOut != null)
                {
                    if (AppSettings.OutputMode == "WasapiExclusive")
                    {
                        isPausing = true;
                        waveOut.Stop();
                        AppSettings.isPlaying = false;
                        progressTimer.Stop();
                    }
                    else
                    {
                        isPausing = true;
                        waveOut.Pause();
                        AppSettings.isPlaying = false;
                        progressTimer.Stop();
                    }
                }
                if (wasapiOut != null)
                {
                    isPausing = true;
                    wasapiOut.Pause();
                    AppSettings.isPlaying = false;
                    progressTimer.Stop();
                }
            }
            else
            {
                if (waveOut != null)
                {
                    isPausing = false;
                    if (AppSettings.OutputMode == "WasapiExclusive")
                    {
                        waveOut.Play();
                        AppSettings.isPlaying = true;
                        progressTimer.Start();
                    }
                    else
                    {
                        waveOut.Play();
                        AppSettings.isPlaying = true;
                        progressTimer.Start();
                    }

                }
                else if (wasapiOut != null)
                {
                    isPausing = false;
                    wasapiOut.Play();
                    AppSettings.isPlaying = true;
                    progressTimer.Start();

                }
                else
                {
                    if (currentPlayingMusic != null)
                    {
                        playingMusic?.Invoke(this, currentPlayingMusic);
                    }
                    else if (musicList != null && musicList.Count > 0)
                    {
                        playingMusic?.Invoke(this, musicList[0]);
                    }
                    else
                    {
                        showMessage?.Invoke(this, "没有可播放的音乐");
                        return;
                    }
                }
            }
        }

        public async Task SavePlayState()
        {
            if (!isInitializing)
            {
                await MusicDatabaseService.SavePlayState(
                    currentPlayMode,
                    currentPlayingMusic?.Id,
                    volume);
            }
        }
    }
}
