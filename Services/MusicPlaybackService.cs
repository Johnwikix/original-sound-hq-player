using NAudio.CoreAudioApi;
using NAudio.Wave;
using SQLite;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static WinUIMusicPlayer.Utils.ToolUtils;
using WinUIMusicPlayer.Model;
using Windows.Devices.Geolocation;
using static SQLite.TableMapping;
using Microsoft.UI.Xaml.Controls;
using System.Timers;

namespace WinUIMusicPlayer.Services
{
    public class MusicPlaybackService
    {
        public Music currentPlayingMusic;
        private System.Timers.Timer progressTimer;
        public List<Music> currentPlayingList;
        public AudioFileReader audioFileReader;
        public IWavePlayer waveOut;
        public bool isPlaying;
        public MMDevice selectedDevice = null;
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

        public MusicPlaybackService()
        {
            progressTimer = new System.Timers.Timer(1000);
            progressTimer.Elapsed += ProgressTimer_Elapsed;
        }

        private void ProgressTimer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            try
            {
                if (audioFileReader != null && isPlaying && !isUserDraggingProgressSlider)
                {
                    updateProgressSliders?.Invoke(this, audioFileReader.CurrentTime.TotalSeconds);
                    string currentTime = audioFileReader.CurrentTime.ToString(@"mm\:ss");
                    string totalTime = audioFileReader.TotalTime.ToString(@"mm\:ss");
                    updatePlayTimeText?.Invoke(this, $"{currentTime}/{totalTime}");
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
            if (audioFileReader != null && isPlaying)
            {
                newPosition = audioFileReader.CurrentTime.TotalSeconds + seconds;
                newPosition = Math.Max(0, Math.Min(newPosition, audioFileReader.TotalTime.TotalSeconds));
                audioFileReader.CurrentTime = TimeSpan.FromSeconds(newPosition);
            }
            return newPosition;
        }

        public void ChangingSetting()
        {
            try
            {
                // 如果当前正在播放，停止播放并重新初始化音频资源
                if (isPlaying)
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
                    if (audioFileReader != null)
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
            OutputDeviceChange();
            SelectOutputDevice();
            waveOut.Init(audioFileReader);
            waveOut.PlaybackStopped += WaveOut_PlaybackStopped;
            waveOut.Play();
            isPlaying = true;
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
                await Task.Delay(100);
                await AutoPlayNextTrack();
            }
        }

        public async Task AutoPlayNextTrack()
        {
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
                // 加载新音频
                audioFileReader = new AudioFileReader(music.Path);
                audioFileReader.Volume = volume;
                audioFileReader.CurrentTime = currentPos;
                SelectOutputDevice();
                waveOut.Init(audioFileReader);
                return true;
            }
            catch (Exception ex)
            {
                showMessage?.Invoke(this, $"播放失败{ex.Message}");
                System.Diagnostics.Debug.WriteLine($"错误: {ex.Message}");
                Reset();
                updateProgressSliders?.Invoke(this,0);
                return false;
            }           
        }

        public async Task PlayMusic(Music music, TimeSpan currentPos = new TimeSpan(), bool isSettingChanged = false)
        {
            if (progressTimer != null)
            {
                progressTimer.Stop();
            }
            if (await InitializeAudioResources(music, currentPos))
            {
                try
                {
                    waveOut.Play();
                    waveOut.PlaybackStopped += WaveOut_PlaybackStopped;
                    updatePlayPauseButton?.Invoke(this, "\uE769");
                    updateProgressMax?.Invoke(this, audioFileReader.TotalTime.TotalSeconds);                  
                    if (isSettingChanged)
                    {
                        updateProgressSliders?.Invoke(this, currentPos.TotalSeconds);
                    }
                    else
                    {
                        updateProgressSliders?.Invoke(this, 0);
                    }                    
                    isPlaying = true;
                    progressTimer.Start();
                    await SavePlayState();
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
            if (progressTimer != null)
            {
                progressTimer.Stop();
            }
        }

        public void DisposeAudio() {
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
        }

        public void SwitchPlayMode() {
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

        public void StopPlaying() {
            if (waveOut != null)
            {
                waveOut.Stop();
                isPlaying = false;
                progressTimer.Stop();
            }
        }

        public void PlayButton() {
            if (isPlaying)
            {
                if (AppSettings.OutputMode == "WasapiExclusive")
                {
                    isPausing = true;
                    waveOut.Stop();
                    isPlaying = false;
                    progressTimer.Stop();
                }
                else
                {
                    isPausing = true;
                    waveOut.Pause();
                    isPlaying = false;
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
                        isPlaying = true;
                        progressTimer.Start();
                    }
                    else
                    {
                        waveOut.Play();
                        isPlaying = true;
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
