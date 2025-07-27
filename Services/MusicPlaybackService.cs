using Microsoft.Extensions.DependencyInjection;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.OnlineAPIs.CloudMusicAPI;
using WinUIMusicPlayer.Provider;
using WinUIMusicPlayer.Reader;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.ViewModel;
using WinUIMusicPlayer.WebService;
using static WinUIMusicPlayer.Utils.ToolUtils;

namespace WinUIMusicPlayer.Services
{
    public class MusicPlaybackService
    {
        private System.Timers.Timer progressTimer;
        public IWavePlayer waveOut;
        public MultiTypeAudioReader multiTypeAudioReader;
        //public WaveChannel32 waveChannel;
        private MMDevice selectedDevice = null;
        public int? lastPlayedMusicId;
        public bool isManualSelect = false;
        //public bool isManualPlayingNext = false;
        public bool isPausing = false;
        public bool isSettingsChangeStop = false;
        public float volume = 0.5f;
        public event EventHandler<float[]> updateSpectrumData;
        public bool isInitializing = true;
        private NotificationService notificationService;
        public List<LyricLine> _lyrics = new List<LyricLine>();
        private CancellationTokenSource _lyricsCancellationTokenSource;       
        private CustomEqualizer equalizer;
        private readonly StringBuilder _timeStringBuilder = new StringBuilder(16);
        private TimeSpan _cachedCurrentTime;
        private TimeSpan _cachedTotalTime;
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
        private bool isEnableEq = false;
        public MusicBrowseViewModel MusicBrowseViewModel { get;}
        private readonly object _waveOutLock = new();
        private readonly object _waveChannelLock = new();
        private readonly object _equalizerLock = new();
        private CancellationTokenSource _currentOperationCts;
        private readonly object _initializeLock = new object();
        private volatile bool _isDisposing = false;
        private readonly SystemMediaControlsService _systemMediaControlsService = App.Services.GetRequiredService<SystemMediaControlsService>();

        public MusicPlaybackService(NotificationService notificationService)
        {
            this.notificationService = notificationService;
            MusicBrowseViewModel = App.Services.GetRequiredService<MusicBrowseViewModel>();
            progressTimer = new System.Timers.Timer(1000);
            progressTimer.Elapsed += ProgressTimer_Elapsed;
            InitializingData();
            
        }

        private async void InitializingData()
        {
            MusicBrowseViewModel.CurrentPlayingList = new ObservableCollection<Music>(await MusicDatabaseService.LoadPlayList());
        }

        public async Task SetLyrics()
        {
            CancelPreviousLyricsTask();
            _lyrics.Clear();
            string? lrcContent = AppData.allSongs.FirstOrDefault(m => m.Id == MusicBrowseViewModel.CurrentPlayingMusic?.Id)?.Lyrics;
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
                        var autoLyrics = string.Empty;
                        if (string.IsNullOrEmpty(AppSettings.LrcAPISource) || AppSettings.LrcAPISource == "https://api.lrc.cx")
                        {
                            autoLyrics = await CloudMusicSearchHelper.GetSongLyrics(
                            MusicBrowseViewModel.CurrentPlayingMusic.Title,
                            MusicBrowseViewModel.CurrentPlayingMusic.Album,
                            MusicBrowseViewModel.CurrentPlayingMusic.Author,
                            cancellationToken);
                        }
                        else
                        {
                            autoLyrics = await LrcService.GetLyricsAsync(
                            MusicBrowseViewModel.CurrentPlayingMusic.Title,
                            MusicBrowseViewModel.CurrentPlayingMusic.Album,
                            MusicBrowseViewModel.CurrentPlayingMusic.Author,
                            cancellationToken);
                        }

                        if (autoLyrics != null)
                        {
                            lrcContent = autoLyrics;
                            MusicBrowseViewModel.CurrentPlayingMusic.Lyrics = lrcContent;
                            cancellationToken.ThrowIfCancellationRequested();
                            await MusicDatabaseService.UpdateMusicInfo(MusicBrowseViewModel.CurrentPlayingMusic);
                            AppData.allSongs.FirstOrDefault(m => m.Id == MusicBrowseViewModel.CurrentPlayingMusic?.Id).Lyrics = lrcContent;
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

                    bool found = false;
                    foreach (var lyric in lyrics)
                    {
                        if (Math.Abs((lyric.Time - time).TotalMilliseconds) <= 100)
                        {
                            lyric.Text += "\n" + text;
                            found = true;
                            break;
                        }
                    }
                    if (!found)
                    {
                        lyrics.Add(new LyricLine
                        {
                            Time = time,
                            Text = text,
                            IsCurrent = false
                        });
                    }
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
                if (AppSettings.isPlaying && !MusicBrowseViewModel.IsUserDraggingProgressSlider)
                {

                    if (multiTypeAudioReader != null)
                    {                        
                        if (multiTypeAudioReader.CurrentTime.TotalSeconds >= (int)multiTypeAudioReader.TotalTime.TotalSeconds)
                        {
                            AutoPlayNextTrack();
                        }
                        // 格式化显示时间
                        UpdateProgressTimerUI();
                    } 
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"进度条更新失败: {ex.Message}");
            }
        }

        private void UpdateProgressTimerUI()
        {
            if (multiTypeAudioReader != null)
            {
                _cachedCurrentTime = TimeSpan.FromSeconds(multiTypeAudioReader.CurrentTime.TotalSeconds);
                _cachedTotalTime = TimeSpan.FromSeconds(multiTypeAudioReader.TotalTime.TotalSeconds);
                _timeStringBuilder.Clear();
                App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                {
                    if (!isManualSelect)
                    {
                        try
                        {
                            MusicBrowseViewModel.ProgressSlider = multiTypeAudioReader.CurrentTime.TotalSeconds;
                            MusicBrowseViewModel.PlayTimeText = _timeStringBuilder.AppendFormat("{0:mm\\:ss}/{1:mm\\:ss}", _cachedCurrentTime, _cachedTotalTime).ToString();
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine(ex.Message);
                        }
                    }
                });
                UpdateLyrics(_cachedCurrentTime);
                _systemMediaControlsService.UpdateTimelineProperties(multiTypeAudioReader.CurrentTime, multiTypeAudioReader.TotalTime);
            }
        }


        private void UpdateLyrics(TimeSpan currentPosition)
        {
            if (_lyrics.Count == 0)
                return;
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
                MusicBrowseViewModel.UpdateLyricsToUI(currentIndex);
            }
        }

        public double AdjustPlaybackPosition(int seconds)
        {
            double newPosition = 0;
            if (AppSettings.isPlaying)
            {
                if (multiTypeAudioReader != null)
                {
                    double currentTimeSeconds = multiTypeAudioReader.CurrentTime.TotalSeconds;
                    double totalSeconds = multiTypeAudioReader.TotalTime.TotalSeconds;
                    newPosition = currentTimeSeconds + seconds;
                    newPosition = Math.Max(0, Math.Min(newPosition, totalSeconds));
                    ChangeWaveChannelTime(TimeSpan.FromSeconds(newPosition));
                }
            }
            return newPosition;
        }

        public void ChangingSetting()
        {
            try
            {
                //isManualPlayingNext = true;
                lock (_waveOutLock)
                {
                    // 如果当前正在播放，停止播放并重新初始化音频资源
                    if (AppSettings.isPlaying)
                    {
                        isSettingsChangeStop = true;
                        progressTimer?.Stop();
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
                    }
                    else
                    {
                        if (waveOut != null)
                        {
                            waveOut.Stop();
                            waveOut.Dispose();
                            waveOut = null;
                        }
                        isSettingsChangeStop = true;
                        if (selectedDevice != null)
                        {
                            selectedDevice.Dispose();
                            selectedDevice = null;
                        }
                    }
                    OutputDeviceChange();
                    if (multiTypeAudioReader != null)
                    {
                        ResumeMusic();
                    }
                }                
            }
            catch (Exception ex)
            {
                notificationService.SendNotification(ToolUtils.GetString("Error"), ex.Message);
                System.Diagnostics.Debug.WriteLine($"错误: {ex.Message}");
            }

        }

        public void ResumeMusic()
        {
            //出错捕获，待测试
            try
            {
                if (multiTypeAudioReader != null && equalizer != null)
                {
                    isEnableEq = true;
                    SelectOutputDevice();
                    waveOut.Init(equalizer);

                }
                else if (multiTypeAudioReader != null)
                {
                    isEnableEq = false;
                    SelectOutputDevice();
                    waveOut.Init(multiTypeAudioReader);
                }
                if (AppSettings.isPlaying)
                {
                    waveOut.Play();
                    AppSettings.isPlaying = true;
                    MusicBrowseViewModel.IsPlaying = true;
                    progressTimer.Start();
                }               
            }
            catch (Exception ex) {
                notificationService.SendNotification(ToolUtils.GetString("Error"), ex.Message);
            }
        }

        public void OutputDeviceChange()
        {
            selectedDevice?.Dispose(); // 确保释放之前的设备
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
        }

        public void SelectOutputDevice()
        {
            lock (_waveOutLock)
            {
                if (selectedDevice == null)
                {
                    OutputDeviceChange();
                }
                try
                {
                    SwitchDevice();
                }
                catch (Exception ex)
                {
                    OutputDeviceChange();
                    SwitchDevice();
                }
            }
        }

        private void SwitchDevice() {
            switch (AppSettings.OutputMode)
            {
                case "WaveOut":
                    var waveOutEvent = new WaveOutEvent();
                    waveOutEvent.DesiredLatency = AppSettings.Latency;
                    waveOut = waveOutEvent;
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
                    waveOut = new NAudio.Wave.DirectSoundOut();
                    break;
                case "ASIO":
                    waveOut = new NAudio.Wave.AsioOut();
                    break;
                default:
                    var defaultWaveOutEvent = new WaveOutEvent();
                    defaultWaveOutEvent.DesiredLatency = AppSettings.Latency;
                    waveOut = defaultWaveOutEvent;
                    break;
            }
        }

        public void AutoPlayNextTrack()
        {
            progressTimer?.Stop();
            switch (AppData.PlayMode)
            {
                case PlayMode.SingleLoop:
                    MusicBrowseViewModel.PlayMusic(MusicBrowseViewModel.CurrentPlayingMusic);
                    break;
                case PlayMode.ListLoop:
                    int currentIndex = MusicBrowseViewModel.CurrentPlayingList.ToList().FindIndex(m => m.Id == MusicBrowseViewModel.CurrentPlayingMusic.Id);
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

        private void MusicEnd()
        {
            progressTimer.Stop();
            if (multiTypeAudioReader != null)
            {
                waveOut.Stop();
                ChangeWaveChannelTime(TimeSpan.Zero);                
            }
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
            if (AppData.PlayMode != PlayMode.RandomLoop)
            {
                int currentIndex = MusicBrowseViewModel.CurrentPlayingList.ToList().FindIndex(m => m.Id == MusicBrowseViewModel.CurrentPlayingMusic.Id);
                int nextIndex = (currentIndex + 1) % MusicBrowseViewModel.CurrentPlayingList.Count;
                MusicBrowseViewModel.PlayMusic(MusicBrowseViewModel.CurrentPlayingList[nextIndex]);
            }
            else {
                Random random = new Random();
                int randomIndex = random.Next(MusicBrowseViewModel.CurrentPlayingList.Count);
                MusicBrowseViewModel.PlayMusic(MusicBrowseViewModel.CurrentPlayingList[randomIndex]);
            }            
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

        // 切换均衡器开关
        public void ToggleEqualizer()
        {
            // 只有在启用状态变化且音频播放时才需要重新初始化
            if (AppSettings.IsEqualizerEnabled && !isEnableEq)
            {
                //var currentPos = multiTypeAudioReader?.CurrentTime ?? TimeSpan.Zero;
                lock (_waveOutLock) {
                    if (waveOut != null)
                    {
                        //isManualPlayingNext = true;
                        waveOut.Stop();
                        waveOut.Dispose();
                        waveOut = null;
                        SelectOutputDevice();
                        if (AppSettings.IsEqualizerEnabled)
                        {
                            isEnableEq = true;
                            var sampleProvider = multiTypeAudioReader.ToSampleProvider();
                            equalizer = new CustomEqualizer(sampleProvider, equalizerBands);
                            waveOut.Init(equalizer);
                        }
                        //InitializeAudioResources(MusicBrowseViewModel.CurrentPlayingMusic, currentPos);
                    }

                    if (AppSettings.isPlaying)
                    {
                        waveOut.Play();
                        progressTimer.Start();
                    }
                }                
            }
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
            lock (_equalizerLock) {
                if (equalizer == null) return;
                foreach (var band in equalizerBands)
                {
                    band.Gain = 0f; // 重置增益                
                }
                equalizer.Update();
            }           
        }

        public void SetEqualizer()
        {
            lock (_equalizerLock)
            {
                if (equalizer == null) return;
                foreach (var band in equalizerBands)
                {
                    band.Gain = (float)AppSettings.equalizer[FloatToString[band.Frequency]];
                }
                equalizer.Update();
            }            
        }

        public bool InitializeAudioResources(Music music, TimeSpan currentPos = new TimeSpan())
        {
            try
            {
                _currentOperationCts?.Cancel();
                _currentOperationCts = new CancellationTokenSource();
                var token = _currentOperationCts.Token;
                progressTimer.Stop();
                App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                {
                    MusicBrowseViewModel.ProgressSlider = 0;
                });
                if (!File.Exists(music.Path))
                {
                    notificationService.SendNotification(ToolUtils.GetString("FileDoNotExist"), music.Path);
                    return false;
                }
                if (token.IsCancellationRequested) return false;
                InitializeMusic();
                if (token.IsCancellationRequested) return false;
                SelectOutputDevice();
                if (token.IsCancellationRequested) return false;
                try
                {
                    AppSettings.isDsd = false;
                    multiTypeAudioReader = new MultiTypeAudioReader(music.Path);
                    ChangeWaveChannelTime(currentPos);
                }
                catch (Exception e)
                {
                    Debug.WriteLine(e.Message);
                    Reset();
                    OutputDeviceChange();
                    return false;
                }
                if (music.Extension.ToLower() == "dsf" || music.Extension.ToLower() == "dff")
                {
                    AppSettings.isDsd = true;
                    multiTypeAudioReader.Volume = volume * (float)Math.Pow(10, AppSettings.dsdGain / 20.0);
                }
                else {
                    multiTypeAudioReader.Volume = volume;
                }
                if (token.IsCancellationRequested) return false;
                lock (_waveOutLock)
                {
                    if (AppSettings.IsEqualizerEnabled)
                    {
                        isEnableEq = true;
                        var sampleProvider = multiTypeAudioReader.ToSampleProvider();
                        equalizer = new CustomEqualizer(sampleProvider, equalizerBands);
                        waveOut.Init(equalizer);
                    }
                    else
                    {
                        isEnableEq = false;
                        waveOut.Init(multiTypeAudioReader);
                    }
                }                
                if (token.IsCancellationRequested) return false;
                return true;

            }
            catch (Exception ex)
            {
                notificationService.SendNotification(ToolUtils.GetString("Error"), ex.Message);
                Reset();
                MusicBrowseViewModel.ProgressSlider = 0;
                return false;
            }
        }

        public void PlayMusic(Music music, TimeSpan currentPos = new TimeSpan(), bool isSettingChanged = false)
        {
            lock (_initializeLock) {
                if (_isDisposing) return;
                if (InitializeAudioResources(music, currentPos))
                {
                    try
                    {
                        // 根据文件类型获取总时长
                        double totalSeconds = 0;
                        if (multiTypeAudioReader != null)
                        {
                            totalSeconds = multiTypeAudioReader.TotalTime.TotalSeconds;
                        }
                        lock (_waveOutLock)
                        {
                            if (waveOut != null && !_isDisposing)
                            {
                                waveOut.Play();
                            }
                            else
                            {
                                return;
                            }
                        }
                        progressTimer.Start();
                        UpdateProgressTimerUI();
                        App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                        {
                            MusicBrowseViewModel.ProgressSliderMax = totalSeconds;
                            if (isSettingChanged)
                            {
                                MusicBrowseViewModel.ProgressSlider = currentPos.TotalSeconds;
                            }
                            else
                            {
                                MusicBrowseViewModel.ProgressSlider = 0;
                            }
                            AppSettings.isPlaying = true;
                            MusicBrowseViewModel.IsPlaying = true;
                            MusicBrowseViewModel.UpdatePlayPauseButtonIcon();
                        });
                    }
                    catch (Exception ex)
                    {
                        notificationService.SendNotification(ToolUtils.GetString("Error"), ex.Message);
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

        }
        private void InitializeMusic() {
            lock (_waveOutLock)
            {
                isManualSelect = false;
                isPausing = false;                
                isSettingsChangeStop = false;
                isEnableEq = false;
                if (waveOut != null)
                {
                    waveOut.Stop();
                    waveOut.Dispose();
                    waveOut = null;
                }

                if (multiTypeAudioReader != null) {
                    multiTypeAudioReader.Dispose();
                    multiTypeAudioReader = null;
                }

                if (equalizer != null)
                {
                    equalizer = null;
                }
                progressTimer?.Stop();
            }
        }
        public void Reset()
        {
            _isDisposing = true;
            try
            {
                App.MainWindow.DispatcherQueue.TryEnqueue(() => {
                    MusicBrowseViewModel.IsPlaying = false;
                });
                AppSettings.isPlaying = false;
                InitializeMusic();
            }
            finally
            {
                _isDisposing = false;
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
                //waveOut.PlaybackStopped -= WaveOut_Stop;
                waveOut.Stop();                
                waveOut.Dispose();                
                waveOut = null;
            }

            //if (waveChannel != null)
            //{
            //    waveChannel.Dispose();
            //    waveChannel = null;
            //}

            if (multiTypeAudioReader != null)
            {
                multiTypeAudioReader.Dispose();
                multiTypeAudioReader = null;
            }

            if (equalizer != null)
            {                
                equalizer = null;
            }
            App.MainWindow.DispatcherQueue.TryEnqueue(async() =>
            {
                await MusicDatabaseService.SavePlayState(MusicBrowseViewModel.CurrentPlayingList.ToList(), AppData.PlayMode, MusicBrowseViewModel.CurrentPlayingMusic?.Id, volume);
            });           
        }

        public void ChangeWaveChannelTime(TimeSpan timeSpan) {
            lock (_waveChannelLock) {
                multiTypeAudioReader.CurrentTime = timeSpan;
            }            
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
                //isManualPlayingNext = true;
                progressTimer.Stop();
                waveOut.Stop();
                AppSettings.isPlaying = false;
                MusicBrowseViewModel.IsPlaying = false;                
            }

        }

        public void PlayButton()
        {
            if (AppSettings.isPlaying)
            {
                if (waveOut != null)
                {
                    //必须这样写，不然在某些音频设备上会有bug
                    if (AppSettings.OutputMode.Contains("WasapiExclusive"))
                    {
                        isPausing = true;
                        waveOut.Stop();
                        AppSettings.isPlaying = false;
                        MusicBrowseViewModel.IsPlaying = false;
                        progressTimer.Stop();
                    }
                    else
                    {
                        isPausing = true;
                        waveOut.Pause();
                        AppSettings.isPlaying = false;
                        MusicBrowseViewModel.IsPlaying = false;
                        progressTimer.Stop();
                    }
                }
            }
            else
            {
                if (waveOut != null)
                {
                    isPausing = false;
                    waveOut.Play();
                    AppSettings.isPlaying = true;
                    MusicBrowseViewModel.IsPlaying = true;
                    progressTimer.Start();
                    //if (AppSettings.OutputMode.Contains("WasapiExclusive"))
                    //{
                    //    waveOut.Play();
                    //    AppSettings.isPlaying = true;
                    //    MusicBrowseViewModel.IsPlaying = true;
                    //    progressTimer.Start();
                    //}
                    //else
                    //{
                    //    waveOut.Play();
                    //    AppSettings.isPlaying = true;
                    //    MusicBrowseViewModel.IsPlaying = true;
                    //    progressTimer.Start();
                    //}

                }
                else
                {
                    if (MusicBrowseViewModel.CurrentPlayingMusic != null)
                    {
                        MusicBrowseViewModel.PlayMusic(MusicBrowseViewModel.CurrentPlayingMusic);
                    }
                    else if (MusicBrowseViewModel.CurrentPlayingList != null && MusicBrowseViewModel.CurrentPlayingList.Count > 0)
                    {
                        MusicBrowseViewModel.PlayMusic(MusicBrowseViewModel.CurrentPlayingList[0]);
                    }
                    else
                    {
                        notificationService.SendNotification(ToolUtils.GetString("Error"),"没有可播放的音乐");
                        return;
                    }
                }
            }
        }
    }
}
