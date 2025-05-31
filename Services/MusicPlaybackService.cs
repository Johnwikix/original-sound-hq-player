using CSCore;
using CSCore.Ffmpeg;
using CSCore.Streams.Effects;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Audio;
using WinUIMusicPlayer.Adapter;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Provider;
using WinUIMusicPlayer.Reader;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.WebService;
using static WinUIMusicPlayer.Utils.ToolUtils;

namespace WinUIMusicPlayer.Services
{
    public class MusicPlaybackService
    {
        public Music currentPlayingMusic;
        private System.Timers.Timer progressTimer;
        public List<Music> currentPlayingList;
        //public MultiTypeAudioReader multiTypeAudioReader;
        public IWavePlayer waveOut;
        //public WaveStream adapter;
        //public CSCore.SoundOut.WasapiOut wasapiOut;
        public WaveChannel32 waveChannel;
        private MMDevice selectedDevice = null;
        //private CSCore.CoreAudioAPI.MMDevice csCoreMMdevice = null;
        //public IWaveSource ffmpegDecoder;
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
        //public List<Music> musicList;
        public bool isUserDraggingProgressSlider = false;
        public bool isInitializing = true;
        private NotificationService notificationService;
        public event EventHandler<int> updateCurrentLyricIndex;
        public List<LyricLine> _lyrics = new List<LyricLine>();
        private LrcService lrcService = new LrcService();
        private CancellationTokenSource _lyricsCancellationTokenSource;
        private CustomEqualizer equalizer;
        private CustomEqualizerBand[] equalizerBands = new CustomEqualizerBand[]
        {
            new CustomEqualizerBand {Frequency = 32, Gain =  (float)AppSettings.equalizer["32Hz"], Bandwidth = 1.0f},
            new CustomEqualizerBand {Frequency = 64, Gain = (float)AppSettings.equalizer["64Hz"], Bandwidth = 1.0f},
            new CustomEqualizerBand {Frequency = 125, Gain = (float)AppSettings.equalizer["125Hz"], Bandwidth = 1.0f},
            new CustomEqualizerBand {Frequency = 250, Gain = (float)AppSettings.equalizer["250Hz"], Bandwidth = 1.0f},
            new CustomEqualizerBand {Frequency = 500, Gain = (float)AppSettings.equalizer["500Hz"], Bandwidth = 1.0f},
            new CustomEqualizerBand {Frequency = 1000, Gain = (float)AppSettings.equalizer["1kHz"], Bandwidth = 1.0f},
            new CustomEqualizerBand {Frequency = 2000, Gain = (float)AppSettings.equalizer["2kHz"], Bandwidth = 1.0f},
            new CustomEqualizerBand {Frequency = 4000, Gain = (float)AppSettings.equalizer["4kHz"], Bandwidth = 1.0f},
            new CustomEqualizerBand {Frequency = 8000, Gain = (float)AppSettings.equalizer["8kHz"], Bandwidth = 1.0f},
            new CustomEqualizerBand {Frequency = 16000, Gain = (float)AppSettings.equalizer["16kHz"], Bandwidth = 1.0f}
        };

        public MusicPlaybackService()
        {
            notificationService = new NotificationService();
            progressTimer = new System.Timers.Timer(200);
            progressTimer.Elapsed += ProgressTimer_Elapsed;
            InitializingData();
        }

        private async void InitializingData()
        {
            currentPlayingList = await MusicDatabaseService.LoadPlayList();
        }

        public async Task SetLyrics()
        {
            CancelPreviousLyricsTask();
            _lyrics.Clear();
            string? lrcContent = AppData.allSongs.FirstOrDefault(m => m.Id == currentPlayingMusic?.Id)?.Lyrics;
            var lyricsContent = await ParseLrcLyrics(lrcContent);
            if (lyricsContent != null)
            {
                _lyrics = lyricsContent;
            }
        }

        public async Task<List<LyricLine>> ParseLrcLyrics(string? lrcContent)
        {
            _lyricsCancellationTokenSource = new CancellationTokenSource();
            var cancellationToken = _lyricsCancellationTokenSource.Token;
            List<LyricLine> lyrics = new List<LyricLine>();
            if (string.IsNullOrEmpty(lrcContent))
            {
                if (AppSettings.isAutoLyricsEnabled)
                {
                    try
                    {
                        var autoLyrics = await lrcService.GetLyricsAsync(
                            currentPlayingMusic.Title,
                            currentPlayingMusic.Album,
                            currentPlayingMusic.Author,
                            cancellationToken);

                        if (autoLyrics != null)
                        {
                            lrcContent = autoLyrics;
                            currentPlayingMusic.Lyrics = lrcContent;
                            //AppData.allSongs.FirstOrDefault(m => m.Id == currentPlayingMusic.Id).Lyrics = lrcContent;
                            cancellationToken.ThrowIfCancellationRequested();
                            await MusicDatabaseService.UpdateMusicInfo(currentPlayingMusic);
                            AppData.allSongs.FirstOrDefault(m => m.Id == currentPlayingMusic?.Id).Lyrics = lrcContent;
                            return SpliteContent(lrcContent, lyrics);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        Debug.WriteLine("歌词获取任务已被取消");
                    }

                    // 如果获取失败或被取消，返回默认歌词
                    //lyrics.Add(new LyricLine
                    //{
                    //    Text = ToolUtils.GetString("LyricsGetFailed"),
                    //    Time = TimeSpan.Zero,
                    //    IsCurrent = true
                    //});
                    return null;
                }
                else
                {
                    lyrics.Add(new LyricLine
                    {
                        Text = ToolUtils.GetString("LyricsGetFailed"),
                        Time = TimeSpan.Zero,
                        IsCurrent = true
                    });
                    return lyrics;
                }
            }
            return SpliteContent(lrcContent, lyrics);
        }

        private void CancelPreviousLyricsTask()
        {
            if (_lyricsCancellationTokenSource != null)
            {
                try
                {
                    if (!_lyricsCancellationTokenSource.IsCancellationRequested)
                    {
                        _lyricsCancellationTokenSource.Cancel();
                    }
                }
                catch (ObjectDisposedException)
                {
                    // 忽略已释放对象的异常
                }
                finally
                {
                    _lyricsCancellationTokenSource.Dispose();
                    _lyricsCancellationTokenSource = null;
                }
            }
        }

        private List<LyricLine> SpliteContent(string lrcContent, List<LyricLine> lyrics)
        {
            string[] lines = lrcContent.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string line in lines)
            {
                string trimmedLine = line.Trim();
                if (string.IsNullOrEmpty(trimmedLine) || !trimmedLine.StartsWith("["))
                    continue;

                // 匹配时间标签 [mm:ss.xx]
                Match timeMatch = Regex.Match(trimmedLine, @"\[(\d{2}):(\d{2})\.(\d{2,3})\]");
                if (timeMatch.Success)
                {
                    int minutes = int.Parse(timeMatch.Groups[1].Value);
                    int seconds = int.Parse(timeMatch.Groups[2].Value);
                    string millisecondStr = timeMatch.Groups[3].Value;
                    int milliseconds;

                    // 根据毫秒部分的长度处理不同格式
                    if (millisecondStr.Length == 2)
                    {
                        milliseconds = int.Parse(millisecondStr) * 10;
                    }
                    else
                    {
                        milliseconds = int.Parse(millisecondStr);
                    }

                    TimeSpan time = new TimeSpan(0, 0, minutes, seconds, milliseconds);

                    // 提取歌词文本（时间标签后的所有内容）
                    string text = trimmedLine.Substring(timeMatch.Length).Trim();

                    // 如果是空行或元信息行（如作词作曲），跳过或添加为特殊行
                    if (string.IsNullOrEmpty(text))
                        continue;

                    lyrics.Add(new LyricLine
                    {
                        Time = time,
                        Text = text,
                        IsCurrent = false
                    });
                }
            }
            if (lyrics.Count == 0)
            {
                lyrics.Add(new LyricLine
                {
                    Text = ToolUtils.GetString("NoRecognizableLyrics"),
                    Time = TimeSpan.Zero,
                    IsCurrent = true
                });
                return lyrics;
            }
            return lyrics.OrderBy(l => l.Time).ToList();
        }

        private void ProgressTimer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            try
            {
                if (AppSettings.isPlaying && !isUserDraggingProgressSlider)
                {
                    double currentTimeSeconds = 0;
                    double totalSeconds = 0;

                    if (waveChannel != null)
                    {
                        currentTimeSeconds = waveChannel.CurrentTime.TotalSeconds;
                        totalSeconds = waveChannel.TotalTime.TotalSeconds;
                        if (currentTimeSeconds > totalSeconds) {
                            currentTimeSeconds = 0;
                            totalSeconds = 0;
                            AutoPlayNextTrack();
                        }
                    }
                    //else if (ffmpegDecoder != null)
                    //{
                    //    currentTimeSeconds = (double)ffmpegDecoder.Position / ffmpegDecoder.WaveFormat.BytesPerSecond;
                    //    totalSeconds = (double)ffmpegDecoder.Length / ffmpegDecoder.WaveFormat.BytesPerSecond;
                    //}
                    updateProgressSliders?.Invoke(this, currentTimeSeconds);

                    // 格式化显示时间
                    TimeSpan currentTime = TimeSpan.FromSeconds(currentTimeSeconds);
                    TimeSpan totalTime = TimeSpan.FromSeconds(totalSeconds);
                    string currentTimeText = currentTime.ToString(@"mm\:ss");
                    string totalTimeText = totalTime.ToString(@"mm\:ss");
                    updatePlayTimeText?.Invoke(this, $"{currentTimeText}/{totalTimeText}");
                    UpdateLyrics(currentTime);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"进度条更新失败: {ex.Message}");
            }
        }

        private void UpdateLyrics(TimeSpan currentPosition)
        {
            if (_lyrics.Count == 0)
                return;
            var ly = _lyrics;
            // 查找当前应显示的歌词
            int currentIndex = -1;
            for (int i = 0; i < _lyrics.Count; i++)
            {
                // 找到时间戳小于等于当前播放位置的最后一条歌词
                if (_lyrics[i].Time <= currentPosition)
                {
                    currentIndex = i;
                }
                else
                {
                    break;
                }
            }

            // 触发事件通知UI更新
            if (currentIndex >= 0)
            {
                updateCurrentLyricIndex?.Invoke(this, currentIndex);
            }
        }

        public double AdjustPlaybackPosition(int seconds)
        {
            double newPosition = 0;
            if (AppSettings.isPlaying)
            {
                if (waveChannel != null)
                {
                    // 对于FLAC文件，计算新位置
                    double currentTimeSeconds = waveChannel.CurrentTime.TotalSeconds;
                    double totalSeconds = waveChannel.TotalTime.TotalSeconds;

                    newPosition = currentTimeSeconds + seconds;
                    newPosition = Math.Max(0, Math.Min(newPosition, totalSeconds));

                    // 设置新位置
                    waveChannel.CurrentTime = TimeSpan.FromSeconds(newPosition);
                }
                //else if (ffmpegDecoder != null)
                //{
                //    // 对于其他格式，使用audioFileReader

                //    newPosition = (double)ffmpegDecoder.Position / ffmpegDecoder.WaveFormat.BytesPerSecond + seconds;
                //    newPosition = Math.Max(0, Math.Min(newPosition, (double)ffmpegDecoder.Length / ffmpegDecoder.WaveFormat.BytesPerSecond));
                //    ffmpegDecoder.Position = (long)(newPosition * ffmpegDecoder.WaveFormat.BytesPerSecond);
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
                    if (selectedDevice != null)
                    {
                        selectedDevice.Dispose();
                        selectedDevice = null;
                    }
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    OutputDeviceChange();
                    if (waveChannel != null)
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
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    OutputDeviceChange();

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
            if (waveChannel != null && equalizer != null)
            {
                SelectOutputDevice();
                waveOut.Init(equalizer);               
                waveOut.Play();
                
            } else if (waveChannel != null)
            {
                SelectOutputDevice();
                waveOut.Init(waveChannel);             
                waveOut.Play();
            }
            AppSettings.isPlaying = true;
            progressTimer.Start();
        }

        public void OutputDeviceChange()
        {
            selectedDevice = null;
            using (var enumerator = new MMDeviceEnumerator())
            {
                try
                {
                    if (AppSettings.DeviceName != null)
                    {
                        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                        foreach (var device in devices)
                        {
                            if (device.FriendlyName == AppSettings.DeviceName)
                            {
                                selectedDevice = device;
                                break;
                            }
                        }
                    }

                    // 如果未找到指定名称的设备，则使用系统默认设备
                    if (selectedDevice == null)
                    {
                        selectedDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                        AppSettings.DeviceName = selectedDevice.FriendlyName;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"获取音频设备时出错: {ex.Message}");
                    selectedDevice = null;
                }
            }
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        public void SelectOutputDevice()
        {
            var device = selectedDevice;
            switch (AppSettings.OutputMode)
            {
                case "WaveOut":
                    waveOut = new WaveOutEvent();
                    break;
                case "WasapiShared":
                    waveOut = new NAudio.Wave.WasapiOut(selectedDevice, AudioClientShareMode.Shared, false, AppSettings.Latency);
                    break;
                case "WasapiExclusivePush":
                    waveOut = new NAudio.Wave.WasapiOut(selectedDevice, AudioClientShareMode.Exclusive, false, AppSettings.Latency);
                    break;
                case "WasapiExclusiveEvent":
                    waveOut = new NAudio.Wave.WasapiOut(selectedDevice, AudioClientShareMode.Exclusive, true, AppSettings.Latency);
                    break;
                case "DirectSound":
                    waveOut = new NAudio.Wave.DirectSoundOut(AppSettings.Latency);
                    break;
                case "ASIO":
                    waveOut = new NAudio.Wave.AsioOut();
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

        //public async void WaveOut_PlaybackStopped(object sender, NAudio.Wave.StoppedEventArgs e)
        //{
        //    bool isNaturalEnd = false;
        //    if (waveChannel != null && !isPausing && !isManualSelect && !isSettingsChangeStop)
        //    {
        //        double currentPositionSeconds = waveChannel.CurrentTime.TotalSeconds;
        //        double totalDurationSeconds = waveChannel.TotalTime.TotalSeconds;
        //        isNaturalEnd = (totalDurationSeconds - currentPositionSeconds) < 0.5;
        //    }

        //    if (isPausing)
        //    {
        //        return;
        //    }

        //    if (isManualSelect)
        //    {
        //        isManualSelect = false;
        //        return;
        //    }

        //    if (isSettingsChangeStop)
        //    {
        //        isSettingsChangeStop = false;
        //        return;
        //    }

        //    if (isNaturalEnd)
        //    {
        //        AutoPlayNextTrack();
        //    }
        //}


        public void AutoPlayNextTrack()
        {
            if (progressTimer != null)
            {
                progressTimer.Stop();
            }
            switch (AppData.PlayMode)
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
                case PlayMode.RepeatOff:
                    MusicEnd();                    
                    break;
            }
        }

        private void MusicEnd()
        {
            progressTimer.Stop();
            if (waveChannel != null)
            {
                waveOut.Stop();
                waveChannel.CurrentTime = TimeSpan.Zero;                
            }
            //else if (ffmpegDecoder != null)
            //{
            //    ffmpegDecoder.Position = 0;
            //}
            updateProgressSliders?.Invoke(this, 0);            
            AppSettings.isPlaying = false;
            updatePlayPauseButton?.Invoke(this, "\uE768");
        }

        public void PlayNextTrack()
        {
            int currentIndex = currentPlayingList.FindIndex(m => m.Id == currentPlayingMusic.Id);
            int nextIndex = (currentIndex + 1) % currentPlayingList.Count;
            playingMusic?.Invoke(this, currentPlayingList[nextIndex]);
        }

        public void UpdateEqualizerSettings()
        {
            equalizerBands = new CustomEqualizerBand[]
            {
                    new CustomEqualizerBand {Frequency = 32, Gain =  (float)AppSettings.equalizer["32Hz"], Bandwidth = 1.0f},
                    new CustomEqualizerBand {Frequency = 64, Gain = (float)AppSettings.equalizer["64Hz"], Bandwidth = 1.0f},
                    new CustomEqualizerBand {Frequency = 125, Gain = (float)AppSettings.equalizer["125Hz"], Bandwidth = 1.0f},
                    new CustomEqualizerBand {Frequency = 250, Gain = (float)AppSettings.equalizer["250Hz"], Bandwidth = 1.0f},
                    new CustomEqualizerBand {Frequency = 500, Gain = (float)AppSettings.equalizer["500Hz"], Bandwidth = 1.0f},
                    new CustomEqualizerBand {Frequency = 1000, Gain = (float)AppSettings.equalizer["1kHz"], Bandwidth = 1.0f},
                    new CustomEqualizerBand {Frequency = 2000, Gain = (float)AppSettings.equalizer["2kHz"], Bandwidth = 1.0f},
                    new CustomEqualizerBand {Frequency = 4000, Gain = (float)AppSettings.equalizer["4kHz"], Bandwidth = 1.0f},
                    new CustomEqualizerBand {Frequency = 8000, Gain = (float)AppSettings.equalizer["8kHz"], Bandwidth = 1.0f},
                    new CustomEqualizerBand {Frequency = 16000, Gain = (float)AppSettings.equalizer["16kHz"], Bandwidth = 1.0f}
            };
        }

        public void SetEqualizerGain(float frequency, float gainDb)
        {
            if (equalizer == null) return;
            // 找到最接近的频段
            var band = equalizerBands.OrderBy(b => Math.Abs(b.Frequency - frequency)).First();
            band.Gain = gainDb;
            equalizer.Update();
        }

        public void ClearEqualizer() {
            if (equalizer == null) return;
            foreach (var band in equalizerBands)
            {
                band.Gain = 0f; // 重置增益                
            }
            equalizer.Update();
        }

        public void SetEqualizer()
        {
            if (equalizer == null) return;
            foreach (var band in equalizerBands)
            {
                band.Gain = (float)AppSettings.equalizer[FloatToString[band.Frequency]];
            }
            equalizer.Update();
        }

        public async Task<bool> InitializeAudioResources(Music music, TimeSpan currentPos = new TimeSpan())
        {
            try
            {
                if (!File.Exists(music.Path))
                {
                    notificationService.SendNotification(ToolUtils.GetString("FileDoNotExist"), music.Path);
                    return false;
                }
                Reset();
                SelectOutputDevice();
                if (music.Extension.ToLower() != "dsf" && music.Extension.ToLower() != "dff")
                {
                    try
                    {
                        AppSettings.isDsd = false;                        
                        var multiTypeAudioReader = new MultiTypeAudioReader(music.Path);
                        multiTypeAudioReader.CurrentTime = currentPos;
                        waveChannel = new WaveChannel32(multiTypeAudioReader);
                        waveChannel.Volume = volume;
                        //waveOut.Init(waveChannel);                        
                        //waveOut.Volume = volume;
                    }
                    catch (Exception e)
                    {
                        Debug.WriteLine(e.Message);
                        Reset();
                        OutputDeviceChange();
                        return false;
                    }
                }
                else
                {
                    try
                    {
                        AppSettings.isDsd = true;
                        var ffmpegDecoder = new FfmpegDecoder(music.Path);
                        var adapter = new CSCoreToWaveStreamAdapter(ffmpegDecoder);
                        waveChannel = new WaveChannel32(adapter);
                        waveChannel.Volume = volume * (float)Math.Pow(10, AppSettings.dsdGain / 20.0);
                        //waveOut.Init(waveChannel);
                    }
                    catch (Exception e)
                    {
                        notificationService.SendNotification(ToolUtils.GetString("DSDPlaybackFailed"), ToolUtils.GetString("SwitchingToSharedMode"));
                        Reset();
                        OutputDeviceChange();
                        return false;
                    }
                }

                if (AppSettings.IsEqualizerEnabled)
                {
                    var sampleProvider = waveChannel.ToSampleProvider();
                    equalizer = new CustomEqualizer(sampleProvider, equalizerBands);
                    waveOut.Init(equalizer);
                }
                else
                {
                    waveOut.Init(waveChannel);
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
            UpdateEqualizerSettings();
            if (await InitializeAudioResources(music, currentPos))
            {
                try
                {
                    // 根据文件类型获取总时长
                    double totalSeconds = 0;
                    if (waveChannel != null)
                    {
                        totalSeconds = waveChannel.TotalTime.TotalSeconds;
                    }
                    //if (ffmpegDecoder != null)
                    //{
                    //    totalSeconds = (double)ffmpegDecoder.Length / ffmpegDecoder.WaveFormat.BytesPerSecond;
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
                    AppSettings.isPlaying = true;
                    updatePlayPauseButton?.Invoke(this, "\uE769");
                }
                catch (Exception ex)
                {
                    showMessage?.Invoke(this, $"播放失败{ex.Message}");
                    System.Diagnostics.Debug.WriteLine($"错误: {ex.Message}");
                    Reset();
                }
            }
            else
            {
                Reset();
                OutputDeviceChange();
            }
        }

        public void Reset()
        {
            isManualSelect = false;
            isPausing = false;
            isSettingsChangeStop = false;
            if (waveOut != null)
            {
                waveOut.Stop();
                waveOut.Dispose();
                waveOut = null;
            }

            if (waveChannel != null)
            {
                waveChannel.Dispose();
                waveChannel = null;
            }

            if (equalizer != null) {                
                equalizer = null;
            }

            //if (multiTypeAudioReader != null)
            //{
            //    multiTypeAudioReader.Dispose();
            //    multiTypeAudioReader = null;
            //}

            //if (ffmpegDecoder != null)
            //{
            //    ffmpegDecoder.Dispose();
            //    ffmpegDecoder = null;
            //}
            if (progressTimer != null)
            {
                progressTimer.Stop();
            }
        }

        public async Task DisposeAudio()
        {
            CancelPreviousLyricsTask();
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

            if (waveChannel != null)
            {
                waveChannel.Dispose();
                waveChannel = null;
            }

            //if (ffmpegDecoder != null)
            //{
            //    ffmpegDecoder.Dispose();
            //    ffmpegDecoder = null;
            //}

            //if (multiTypeAudioReader != null)
            //{
            //    multiTypeAudioReader.Dispose();
            //    multiTypeAudioReader = null;
            //}
            await MusicDatabaseService.SavePlayState(currentPlayingList, AppData.PlayMode, currentPlayingMusic?.Id, volume);
        }

        public void SwitchPlayMode()
        {
            switch (AppData.PlayMode)
            {
                case PlayMode.SingleLoop:
                    AppData.PlayMode = PlayMode.ListLoop;
                    break;
                case PlayMode.ListLoop:
                    AppData.PlayMode = PlayMode.RandomLoop;
                    break;
                case PlayMode.RandomLoop:
                    AppData.PlayMode = PlayMode.RepeatOff;
                    break;
                case PlayMode.RepeatOff:
                    AppData.PlayMode = PlayMode.SingleLoop;
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
                if (waveOut != null)
                {
                    if (AppSettings.OutputMode.Contains("WasapiExclusive"))
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
            }
            else
            {
                if (waveOut != null)
                {
                    isPausing = false;
                    if (AppSettings.OutputMode.Contains("WasapiExclusive"))
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
                else
                {
                    if (currentPlayingMusic != null)
                    {
                        playingMusic?.Invoke(this, currentPlayingMusic);
                    }
                    else if (currentPlayingList != null && currentPlayingList.Count > 0)
                    {
                        playingMusic?.Invoke(this, currentPlayingList[0]);
                    }
                    else
                    {
                        showMessage?.Invoke(this, "没有可播放的音乐");
                        return;
                    }
                }
            }
        }
    }
}
