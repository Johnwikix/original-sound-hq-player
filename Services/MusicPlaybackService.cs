using NAudio.CoreAudioApi;
using NAudio.Flac;
using NAudio.Vorbis;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        //public AudioFileReader audioFileReader;
        public MultiTypeAudioReader multiTypeAudioReader;
        //public FFmpegAudioReader fFmpegAudioReader;
        //public SampleChannel sampleChannel;
        //public VorbisWaveReader vorbisWaveReader;
        //public WaveChannel32 waveChannel;
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

                    if (multiTypeAudioReader != null)
                    {                       
                        currentTimeSeconds = multiTypeAudioReader.CurrentTime.TotalSeconds;
                        totalSeconds = multiTypeAudioReader.TotalTime.TotalSeconds;
                    }
                    //else if (fFmpegAudioReader != null)
                    //{
                    //    currentTimeSeconds = fFmpegAudioReader.CurrentTime.TotalSeconds;
                    //    totalSeconds = fFmpegAudioReader.TotalTime.TotalSeconds;
                    //}

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
                //else if (fFmpegAudioReader != null)
                //{
                //    // 对于其他格式，使用audioFileReader
                //    newPosition = fFmpegAudioReader.CurrentTime.TotalSeconds + seconds;
                //    newPosition = Math.Max(0, Math.Min(newPosition, fFmpegAudioReader.TotalTime.TotalSeconds));
                //    fFmpegAudioReader.CurrentTime = TimeSpan.FromSeconds(newPosition);
                //}
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
                    //if (fFmpegAudioReader != null)
                    //{
                    //    ResumeMusic();
                    //}
                    if (multiTypeAudioReader != null)
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
            if (multiTypeAudioReader != null)
            {
                waveOut.Init(multiTypeAudioReader);
            }
            //else if (fFmpegAudioReader != null)
            //{
            //    sampleChannel = new SampleChannel(fFmpegAudioReader, false);
            //    sampleChannel.Volume = volume;
            //    waveOut.Init(sampleChannel);
            //}
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
                    waveOut = new WasapiOut(selectedDevice, AudioClientShareMode.Shared, false, AppSettings.Latency);
                    break;
                case "WasapiExclusive":
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
            if (multiTypeAudioReader != null && !isPausing && !isManualSelect && !isSettingsChangeStop)
            {
                double currentPositionSeconds = multiTypeAudioReader.CurrentTime.TotalSeconds;
                double totalDurationSeconds = multiTypeAudioReader.TotalTime.TotalSeconds;
                isNaturalEnd = (totalDurationSeconds - currentPositionSeconds) < 0.5;
            }

            //if (fFmpegAudioReader != null && !isPausing && !isManualSelect && !isSettingsChangeStop)
            //{
            //    double currentPositionSeconds = fFmpegAudioReader.CurrentTime.TotalSeconds;
            //    double totalDurationSeconds = fFmpegAudioReader.TotalTime.TotalSeconds;
            //    isNaturalEnd = (totalDurationSeconds - currentPositionSeconds) < 0.5;
            //}

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
                if (waveOut != null)
                {
                    waveOut.Stop();
                    waveOut.Dispose();
                    waveOut = null;
                }

                if (multiTypeAudioReader != null)
                {
                    multiTypeAudioReader.Dispose();
                    multiTypeAudioReader = null;
                }

                //if (audioFileReader != null)
                //{
                //    audioFileReader.Dispose();
                //    audioFileReader = null;
                //}                
                SelectOutputDevice();
                try
                {
                    multiTypeAudioReader = new MultiTypeAudioReader(music.Path);
                    multiTypeAudioReader.CurrentTime = currentPos;
                    multiTypeAudioReader.Volume = volume;
                    waveOut.Init(multiTypeAudioReader);
                }
                catch (Exception e)
                {
                    Debug.WriteLine(e.Message);
                    //FlacReader flacReader = new FlacReader(music.Path);
                    //WaveStream pcmStream = WaveFormatConversionStream.CreatePcmStream(flacReader);
                    //waveChannel = new WaveChannel32(pcmStream);
                    //waveChannel.Volume = volume;
                    //waveChannel.CurrentTime = currentPos;
                    //waveChannel.PadWithZeroes = false;
                    //waveOut.Init(waveChannel);
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
                    if (multiTypeAudioReader != null)
                    {
                        totalSeconds = multiTypeAudioReader.TotalTime.TotalSeconds;
                    }
                    //else if (fFmpegAudioReader != null)
                    //{
                    //    totalSeconds = fFmpegAudioReader.TotalTime.TotalSeconds;
                    //}
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

            //if (sampleChannel != null)
            //{
            //    sampleChannel.Volume = 0;
            //    sampleChannel = null;
            //}

            //if (fFmpegAudioReader != null)
            //{
            //    fFmpegAudioReader.Dispose();
            //    fFmpegAudioReader = null;
            //}
            
            if (multiTypeAudioReader != null)
            {
                multiTypeAudioReader.Dispose();
                multiTypeAudioReader = null;
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
            if (waveOut != null)
            {
                waveOut.Stop();
                waveOut.Dispose();
                waveOut = null;
            }

            //if (sampleChannel != null)
            //{
            //    sampleChannel.Volume = 0;
            //    sampleChannel = null;
            //}

            //if (fFmpegAudioReader != null)
            //{
            //    fFmpegAudioReader.Dispose();
            //    fFmpegAudioReader = null;
            //}

            if (multiTypeAudioReader != null)
            {
                multiTypeAudioReader.Dispose();
                multiTypeAudioReader = null;
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
