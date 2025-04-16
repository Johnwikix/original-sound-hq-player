using NAudio.CoreAudioApi;
using NAudio.Flac;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using static WinUIMusicPlayer.Utils.ToolUtils;

namespace WinUIMusicPlayer.Services
{
    public class MusicPlaybackService
    {
        public Music currentPlayingMusic;
        private System.Timers.Timer progressTimer;
        public List<Music> currentPlayingList;
        public AudioFileReader audioFileReader;
        public WaveChannel32 waveChannel;
        public IWavePlayer waveOut;
        private MMDevice selectedDevice = null;
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
        //public DispatcherQueue DispatcherQueue { get; set; }

        public MusicPlaybackService()
        {
            progressTimer = new System.Timers.Timer(1000);
            progressTimer.Elapsed += ProgressTimer_Elapsed;
        }

        private void ProgressTimer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            try
            {
                if (AppSettings.isPlaying && !isUserDraggingProgressSlider)
                {
                    double currentTimeSeconds = 0;
                    double totalSeconds = 0;

                    if (currentPlayingMusic.Extension.ToLower() == "flac" && waveChannel != null)
                    {
                        // 对于FLAC文件，从waveChannel计算当前位置和总时长                        
                        currentTimeSeconds = (double)waveChannel.Position / waveChannel.WaveFormat.AverageBytesPerSecond;
                        totalSeconds = (double)waveChannel.Length / waveChannel.WaveFormat.AverageBytesPerSecond;
                    }
                    else if (audioFileReader != null)
                    {
                        // 对于其他格式，直接使用audioFileReader
                        currentTimeSeconds = audioFileReader.CurrentTime.TotalSeconds;
                        totalSeconds = audioFileReader.TotalTime.TotalSeconds;
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
                if (currentPlayingMusic.Extension.ToLower() == "flac" && waveChannel != null)
                {
                    // 对于FLAC文件，计算新位置
                    double currentTimeSeconds = (double)waveChannel.Position / waveChannel.WaveFormat.AverageBytesPerSecond;
                    double totalSeconds = (double)waveChannel.Length / waveChannel.WaveFormat.AverageBytesPerSecond;

                    newPosition = currentTimeSeconds + seconds;
                    newPosition = Math.Max(0, Math.Min(newPosition, totalSeconds));

                    // 设置新位置
                    waveChannel.Position = (long)(newPosition * waveChannel.WaveFormat.AverageBytesPerSecond);
                }
                else if (audioFileReader != null)
                {
                    // 对于其他格式，使用audioFileReader
                    newPosition = audioFileReader.CurrentTime.TotalSeconds + seconds;
                    newPosition = Math.Max(0, Math.Min(newPosition, audioFileReader.TotalTime.TotalSeconds));
                    audioFileReader.CurrentTime = TimeSpan.FromSeconds(newPosition);
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
                    OutputDeviceChange();
                    if (audioFileReader != null)
                    {
                        ResumeMusic();
                    }
                    if (waveChannel != null)
                    {
                        ResumeMusic();
                    }
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
            SelectOutputDevice();
            if (waveChannel != null)
            {
                waveOut.Init(waveChannel);
            }
            else if (audioFileReader != null)
            {
                waveOut.Init(audioFileReader);
            }
            waveOut.PlaybackStopped += WaveOut_PlaybackStopped;
            waveOut.Play();
            AppSettings.isPlaying = true;
            progressTimer.Start();
        }

        public void OutputDeviceChange()
        {
            MMDeviceEnumerator enumerator = new MMDeviceEnumerator();
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

        public void SelectOutputDevice()
        {
            switch (AppSettings.OutputMode)
            {
                case "WaveOut":
                    waveOut = new WaveOutEvent();
                    break;
                case "WasapiShared":
                    //OutputDeviceChange();
                    waveOut = new WasapiOut(selectedDevice, AudioClientShareMode.Shared, false, AppSettings.Latency);
                    break;
                case "WasapiExclusive":
                    //OutputDeviceChange();
                    waveOut = new WasapiOut(selectedDevice, AudioClientShareMode.Exclusive, true, AppSettings.Latency);
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
        }

        public async void WaveOut_PlaybackStopped(object sender, StoppedEventArgs e)
        {
            bool isNaturalEnd = false;
            if (waveChannel != null && !isPausing && !isManualSelect && !isSettingsChangeStop)
            {
                double currentPositionSeconds = waveChannel.CurrentTime.TotalSeconds;
                double totalDurationSeconds = waveChannel.TotalTime.TotalSeconds;
                isNaturalEnd = (totalDurationSeconds - currentPositionSeconds) < 0.5;
            }

            if (audioFileReader != null && !isPausing && !isManualSelect && !isSettingsChangeStop)
            {
                double currentPositionSeconds = audioFileReader.CurrentTime.TotalSeconds;
                double totalDurationSeconds = audioFileReader.TotalTime.TotalSeconds;
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
                //await Task.Delay(100);
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

                if (waveChannel != null)
                {
                    waveChannel.Dispose();
                    waveChannel = null;
                }
                SelectOutputDevice();
                // 加载新音频
                if (music.Extension.ToLower() == "flac")
                {
                    // 使用FlacReader读取FLAC文件
                    FlacReader flacReader = new FlacReader(music.Path);

                    // 转换为PCM流以便兼容其他代码
                    WaveStream pcmStream = WaveFormatConversionStream.CreatePcmStream(flacReader);

                    // 使用WaveChannel32封装WaveStream，以便控制音量
                    waveChannel = new WaveChannel32(pcmStream);
                    waveChannel.Volume = volume;
                    waveChannel.CurrentTime = currentPos;
                    waveChannel.PadWithZeroes = false;
                    waveOut.Init(waveChannel);
                }
                else
                {
                    // 非FLAC文件继续使用AudioFileReader
                    audioFileReader = new AudioFileReader(music.Path);
                    audioFileReader.Volume = volume;
                    audioFileReader.CurrentTime = currentPos;
                    // 初始化播放器
                    waveOut.Init(audioFileReader);
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

        public async Task PlayMusic(Music music, TimeSpan currentPos = new TimeSpan(), bool isSettingChanged = false)
        {
            if (await InitializeAudioResources(music, currentPos))
            {
                try
                {
                    updatePlayPauseButton?.Invoke(this, "\uE769");
                    // 根据文件类型获取总时长
                    double totalSeconds = 0;
                    if (music.Extension.ToLower() == "flac" && waveChannel != null)
                    {
                        // 对于FLAC文件，从waveChannel获取总时长
                        totalSeconds = (double)waveChannel.Length / waveChannel.WaveFormat.AverageBytesPerSecond;
                    }
                    else if (audioFileReader != null)
                    {
                        // 对于其他格式，从audioFileReader获取总时长
                        totalSeconds = audioFileReader.TotalTime.TotalSeconds;
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
                    waveOut.Play();
                    progressTimer.Start();
                    waveOut.PlaybackStopped += WaveOut_PlaybackStopped;
                    AppSettings.isPlaying = true;
                    _ = Task.Run(() => SavePlayState());
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

            if (audioFileReader != null)
            {
                audioFileReader.Dispose();
                audioFileReader = null;
            }
            if (waveChannel != null)
            {
                waveChannel.Dispose();
                waveChannel = null;
            }
            if (progressTimer != null)
            {
                progressTimer.Stop();
            }
        }

        public void DisposeAudio()
        {
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

            if (waveChannel != null)
            {
                waveChannel.Dispose();
                waveChannel = null;
            }
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
        }

        public void PlayButton()
        {
            if (AppSettings.isPlaying)
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
            else
            {
                if (waveOut == null)
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
                else
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
