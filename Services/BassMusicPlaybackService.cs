using ManagedBass;
using ManagedBass.Dsd;
using ManagedBass.Fx;
using ManagedBass.Wasapi;
using Microsoft.Extensions.DependencyInjection;
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
using System.Timers;
using WinUIMusicPlayer.Extensions;
using WinUIMusicPlayer.Manager;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.ViewModel;
using WinUIMusicPlayer.WebService;
using ZLinq;
using static WinUIMusicPlayer.Utils.ToolUtils;

namespace WinUIMusicPlayer.Services
{
    public class BassMusicPlaybackService
    {
        private System.Timers.Timer progressTimer;
        public int _currentStream;
        private SyncProcedure? _syncEndCallback;
        private SyncProcedure? _syncFailCallback;
        private WasapiProcedure _myWasapiProcedure;
        public int? lastPlayedMusicId;
        public bool isManualSelect = false;
        public bool isPausing = false;
        public bool isSettingsChangeStop = false;
        public float volume = 0.5f;
        public bool isInitializing = true;
        private NotificationService notificationService;
        public List<LyricLine> _lyrics = new List<LyricLine>();
        private CancellationTokenSource _lyricsCancellationTokenSource;
        private readonly StringBuilder _timeStringBuilder = new StringBuilder(16);
        private TimeSpan _cachedLastCurrentTime = TimeSpan.Zero;
        private TimeSpan _cachedCurrentTime;
        private TimeSpan _cachedTotalTime;
        //private bool isEnableEq = false;
        public MusicBrowseViewModel MusicBrowseViewModel { get; }
        private readonly object _streamLock = new();
        private readonly object _waveChannelLock = new();
        //private CancellationTokenSource _currentOperationCts;
        private volatile bool _isDisposing = false;
        private readonly SystemMediaControlsService _systemMediaControlsService = App.Services.GetRequiredService<SystemMediaControlsService>();
        private double _totalSeconds;
        private double _currentSeconds;
        private readonly int[] _bandIndices = new int[10];
        private readonly float[] _eqFrequencies = { 32, 64, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 }; // 10频段
        private readonly float[] _eqGains = [
                                                (float)AppSettings.equalizer["32Hz"],
                                                (float)AppSettings.equalizer["64Hz"],
                                                (float)AppSettings.equalizer["125Hz"],
                                                (float)AppSettings.equalizer["250Hz"],
                                                (float)AppSettings.equalizer["500Hz"],
                                                (float)AppSettings.equalizer["1kHz"],
                                                (float)AppSettings.equalizer["2kHz"],
                                                (float)AppSettings.equalizer["4kHz"],
                                                (float)AppSettings.equalizer["8kHz"],
                                                (float)AppSettings.equalizer["16kHz"]
                                            ];
        private PeakEQ _peakEQ;

        public BassMusicPlaybackService(NotificationService notificationService)
        {
            this.notificationService = notificationService;
            MusicBrowseViewModel = App.Services.GetRequiredService<MusicBrowseViewModel>();
            progressTimer = new System.Timers.Timer(1000);
            progressTimer.Elapsed += ProgressTimer_Elapsed;
            InitializingData();
            InitializeBass();
            InitializeBassWasapi();

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

        private void InitializeBass()
        {
            BassManager.Initialize();
            _syncEndCallback = OnPlayBackEnded;
            _syncFailCallback = OnPlaybackFailed;
        }

        private void OnPlaybackFailed(int Handle, int Channel, int Data, nint User)
        {
            AppSettings.isPlaying = false;
        }

        private void OnPlayBackEnded(int Handle, int Channel, int Data, nint User)
        {
            AppSettings.isPlaying = false;
            AutoPlayNextTrack();
        }

        private void ProgressTimer_Elapsed(object? sender, ElapsedEventArgs e)
        {
            try
            {
                if (AppSettings.isPlaying && !MusicBrowseViewModel.IsUserDraggingProgressSlider)
                {
                    if (_currentStream != 0)
                    {
                        var positionBytes = Bass.ChannelGetPosition(_currentStream);
                        var lengthBytes = Bass.ChannelGetLength(_currentStream);
                        _totalSeconds = Bass.ChannelBytes2Seconds(_currentStream, lengthBytes);
                        _currentSeconds = Bass.ChannelBytes2Seconds(_currentStream, positionBytes);
                        if (_currentSeconds < (int)_totalSeconds)
                        {
                            _cachedLastCurrentTime = _cachedCurrentTime;
                        }
                        UpdateProgressTimerUI();
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        private void UpdateProgressTimerUI()
        {
            if (_currentStream != 0)
            {
                _cachedCurrentTime = TimeSpan.FromSeconds(_currentSeconds);
                _cachedTotalTime = TimeSpan.FromSeconds(_totalSeconds);
                _timeStringBuilder.Clear();
                App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                {
                    if (!isManualSelect)
                    {
                        try
                        {
                            MusicBrowseViewModel.ProgressSlider = _currentSeconds;
                            if (_cachedTotalTime.TotalHours >= 1)
                            {
                                MusicBrowseViewModel.PlayTimeText = _timeStringBuilder
                                    .AppendFormat("{0:hh\\:mm\\:ss}/{1:hh\\:mm\\:ss}", _cachedCurrentTime, _cachedTotalTime)
                                    .ToString();
                            }
                            else
                            {
                                MusicBrowseViewModel.PlayTimeText = _timeStringBuilder
                                    .AppendFormat("{0:mm\\:ss}/{1:mm\\:ss}", _cachedCurrentTime, _cachedTotalTime)
                                    .ToString();
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine(ex.Message);
                        }
                    }
                });
                UpdateLyrics(_cachedCurrentTime);
                _systemMediaControlsService.UpdateTimelineProperties(_cachedCurrentTime, _cachedTotalTime);
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

        public async Task SetLyrics()
        {
            CancelPreviousLyricsTask();
            _lyrics.Clear();
            string? lrcContent = AppData.allSongs.AsValueEnumerable().FirstOrDefault(m => m.Id == MusicBrowseViewModel.CurrentPlayingMusic?.Id)?.Lyrics;
            var lyricsContent = await ParseLrcLyrics(lrcContent);
            if (lyricsContent is not null)
            {
                _lyrics = lyricsContent;
            }
        }

        private string GetLyricsContentFromLrc(string path)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                string lyricFilePath = Path.ChangeExtension(path, ".lrc");
                if (File.Exists(lyricFilePath))
                {
                    try
                    {
                        return File.ReadAllText(lyricFilePath);
                    }
                    catch (Exception)
                    {
                        return null;
                    }
                }
            }
            return null;
        }

        public async Task<List<LyricLine>> ParseLrcLyrics(string? lrcContent)
        {
            _lyricsCancellationTokenSource = new CancellationTokenSource();
            var cancellationToken = _lyricsCancellationTokenSource.Token;
            List<LyricLine> lyrics = new List<LyricLine>();
            if (!string.IsNullOrWhiteSpace(lrcContent))
            {
                return SpliteContent(lrcContent, lyrics);
            }
            lrcContent = GetLyricsContentFromLrc(AppData.allSongs.AsValueEnumerable().FirstOrDefault(m => m.Id == MusicBrowseViewModel.CurrentPlayingMusic?.Id)?.Path);
            if (string.IsNullOrWhiteSpace(lrcContent))
            {
                if (AppSettings.isAutoLyricsEnabled)
                {
                    try
                    {
                        var autoLyrics = string.Empty;
                        autoLyrics = await LrcService.GetLyricsAsync(
                            MusicBrowseViewModel.CurrentPlayingMusic.Title,
                            MusicBrowseViewModel.CurrentPlayingMusic.Album,
                            MusicBrowseViewModel.CurrentPlayingMusic.Author,
                            cancellationToken);

                        if (autoLyrics is not null)
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
            else
            {
                return SpliteContent(lrcContent, lyrics);
            }
        }

        private void CancelPreviousLyricsTask()
        {
            if (_lyricsCancellationTokenSource is not null)
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

        public void MusicEnd()
        {
            progressTimer.Stop();
            if (_currentStream != 0)
            {
                if (AppSettings.OutputMode.Contains("Wasapi"))
                {
                    BassWasapi.Stop();
                }
                else
                {
                    Bass.ChannelStop(_currentStream);
                }
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
            int currentIndex = MusicBrowseViewModel.CurrentPlayingList.ToList().FindIndex(m => m.Id == MusicBrowseViewModel.CurrentPlayingMusic.Id);
            int nextIndex = (currentIndex + 1) % MusicBrowseViewModel.CurrentPlayingList.Count;
            MusicBrowseViewModel.PlayMusic(MusicBrowseViewModel.CurrentPlayingList[nextIndex]);
        }

        private void InitializeBassWasapi()
        {
            try
            {
                if (!BassWasapi.Init(-1))
                {
                    return;
                }

                // 设置WASAPI回调
                _myWasapiProcedure = OnWasapiProc;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"初始化wasapi异常:{ex.Message}");
            }
        }

        private int OnWasapiProc(IntPtr buffer, int length, IntPtr user)
        {
            if (_currentStream != 0)
            {
                return Bass.ChannelGetData(_currentStream, buffer, length);
            }
            return 0;
        }

        public void ToggleEqualizer()
        {
            if (AppSettings.IsEqualizerEnabled)
            {
                try
                {
                    if (_currentStream != 0)
                    {
                        _peakEQ = new PeakEQ(_currentStream, Q: 0, Bandwith: 2.5);
                        // 为每个频段添加Band
                        for (int i = 0; i < _eqFrequencies.Length; i++)
                        {
                            _bandIndices[i] = _peakEQ.AddBand(_eqFrequencies[i]);
                            Debug.WriteLine($"均衡器频段 {_eqFrequencies[i]}Hz 创建成功，Band索引: {_bandIndices[i]}");
                        }
                        Debug.WriteLine("均衡器初始化完成");
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"初始化均衡器时出错: {ex.Message}");
                    _peakEQ = null;
                }
            }
        }

        public void SetEqualizerGain(int bandIndex, float gain)
        {
            if (bandIndex < 0 || bandIndex >= _eqFrequencies.Length)
            {
                Debug.WriteLine($"无效的频段索引: {bandIndex}");
                return;
            }
            if (_peakEQ == null)
            {
                Debug.WriteLine("均衡器未初始化");
                return;
            }

            try
            {
                // 使用UpdateBand方法更新指定频段的增益
                _peakEQ.UpdateBand(_bandIndices[bandIndex], gain);
                Debug.WriteLine($"频段 {_eqFrequencies[bandIndex]}Hz (Band {_bandIndices[bandIndex]}) 增益设置为 {gain}dB");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"设置均衡器参数失败: {ex.Message}");
            }
        }

        public void SetEqualizer()
        {
            if (_peakEQ is null) return;
            for (int i = 0; i < 10; i++)
            {
                _peakEQ.UpdateBand(_bandIndices[i], (float)AppSettings.equalizer[FloatToString[_eqFrequencies[i]]]);
            }
        }

        public void ClearEqualizer()
        {
            DisposeEq();
        }

        private bool SwitchDevice()
        {
            bool result = false;
            // 获取当前流的格式信息
            ChannelInfo channelInfo = Bass.ChannelGetInfo(_currentStream);           
            switch (AppSettings.OutputMode)
            {
                case "WasapiShared":
                    result = BassWasapi.Init(AppSettings.BassOutputDeviceId,
                            channelInfo.Frequency,
                            channelInfo.Channels,
                            WasapiInitFlags.Shared,
                            AppSettings.Latency / 1000.0f, 0, _myWasapiProcedure, IntPtr.Zero);
                    break;
                case "WasapiExclusivePush":
                    result = BassWasapi.Init(AppSettings.BassOutputDeviceId,
                            channelInfo.Frequency,
                            channelInfo.Channels,
                            WasapiInitFlags.Exclusive,
                            AppSettings.Latency / 1000.0f, 0, _myWasapiProcedure, IntPtr.Zero);
                    break;
                case "WasapiExclusiveEvent":
                    result = BassWasapi.Init(AppSettings.BassOutputDeviceId,
                            channelInfo.Frequency,
                            channelInfo.Channels,
                            WasapiInitFlags.Exclusive | WasapiInitFlags.EventDriven,
                            AppSettings.Latency / 1000.0f, 0, _myWasapiProcedure, IntPtr.Zero);
                    break;
            }
            return result;
        }

        private bool InitializePlayback()
        {
            try
            {
                // 停止当前WASAPI流
                StopWasapiPlayback();
                // 获取默认音频设备
                if (AppSettings.BassOutputDeviceId == -1)
                {
                    Debug.WriteLine($"无法获取默认WASAPI设备");
                    return false;
                }
                var info = BassWasapi.GetDeviceInfo(AppSettings.BassOutputDeviceId);
                Debug.WriteLine($"使用WASAPI设备: {info.Name}");
                // 初始化播放模式                
                var result = SwitchDevice();
                if (!result)
                {
                    var error = Bass.LastError;
                    if (Bass.LastError == Errors.Busy)
                    {
                        StopWasapiPlayback();
                    }
                    result = SwitchDevice();
                    if (!result)
                    {
                        Debug.WriteLine($"WASAPI独占模式初始化失败: {error}");
                        return false;
                    }
                }
                WasapiInfo wasapiInfo;
                BassWasapi.GetInfo(out wasapiInfo);
                Debug.WriteLine($"实际WASAPI格式 - 采样率: {wasapiInfo.Frequency}, 声道: {wasapiInfo.Channels}, 格式: {wasapiInfo.Format}");
                // 设置音量
                if (AppSettings.OutputMode.Contains("WasapiShared"))
                {
                    BassWasapi.SetVolume(WasapiVolumeTypes.Session, (float)volume);
                }
                else if (AppSettings.OutputMode.Contains("WasapiExclusive"))
                {
                    BassWasapi.SetVolume(WasapiVolumeTypes.WindowsHybridCurve, (float)volume);
                }
                Debug.WriteLine($"WASAPI模式启动成功");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex, $"启动WASAPI独占模式时出错");
                return false;
            }
        }

        private void SetSource(Music music)
        {
            try
            {
                DisposeStream();
                if (AppSettings.OutputMode.Contains("Wasapi"))
                {
                    if (music.Extension.Equals("dsf", StringComparison.OrdinalIgnoreCase) || music.Extension.Equals("dff", StringComparison.OrdinalIgnoreCase))
                    {
                        BassDsd.DefaultGain = AppSettings.dsdGain;
                        BassDsd.DefaultFrequency = 176400;                        
                        _currentStream = BassDsd.CreateStream(music.Path, 0, 0, BassFlags.DSDOverPCM | BassFlags.Float | BassFlags.Decode);                        
                    }
                    else
                    {
                        _currentStream = Bass.CreateStream(music.Path, 0, 0, BassFlags.Unicode | BassFlags.Float | BassFlags.AsyncFile | BassFlags.Decode);
                    }
                    _currentStream = Bass.CreateStream(music.Path, 0, 0, BassFlags.Unicode | BassFlags.Float | BassFlags.AsyncFile | BassFlags.Decode);
                }
                else
                {
                    _currentStream = Bass.CreateStream(music.Path);
                }
                if (_currentStream == 0)
                {
                    notificationService.SendNotification(ToolUtils.GetString("Error"), $"创建流失败: {Bass.LastError}");
                    return;
                }
                //InitializeEqualizer();
                Bass.ChannelSetSync(_currentStream, SyncFlags.End, 0, _syncEndCallback); // 设置播放结束回调
                Bass.ChannelSetSync(_currentStream, SyncFlags.Stalled, 0, _syncFailCallback); // 设置播放失败回调
                ToggleEqualizer();
                // 根据模式设置音量
                if (!AppSettings.OutputMode.Contains("Wasapi"))
                {
                    // 在独占模式下，音量由WASAPI控制
                    Bass.ChannelSetAttribute(
                        _currentStream,
                        ChannelAttribute.Volume,
                        volume
                    );
                }
                var lengthBytes = Bass.ChannelGetLength(_currentStream);
                _totalSeconds = Bass.ChannelBytes2Seconds(_currentStream, lengthBytes);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SetSource异常: {ex.Message}");
            }
        }

        public void PlayMusic(Music music, TimeSpan currentPos = new TimeSpan(), bool isSettingChanged = false)
        {
            lock (_streamLock)
            {
                Stop();
                SetSource(music);
                Play(isSettingChanged);
            }
        }

        public void Stop()
        {
            if (_currentStream != 0)
            {
                if (AppSettings.OutputMode.Contains("Wasapi"))
                {
                    StopWasapiPlayback();
                }
                Bass.ChannelStop(_currentStream);
                progressTimer.Stop();
                AppSettings.isPlaying = false;
                App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                {
                    MusicBrowseViewModel.IsPlaying = false;
                });
            }
        }

        public void Reset()
        {
            _isDisposing = true;
            try
            {
                DisposeStream();
                App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                {
                    MusicBrowseViewModel.IsPlaying = false;
                });
                AppSettings.isPlaying = false;
                SetSource(MusicBrowseViewModel.CurrentPlayingMusic);
            }
            finally
            {
                _isDisposing = false;
            }
        }

        public void PlayButton()
        {
            if (AppSettings.isPlaying)
            {
                if (AppSettings.OutputMode.Contains("Wasapi"))
                {
                    BassWasapi.Stop();
                }
                else
                {
                    Bass.ChannelStop(_currentStream);
                }
                isPausing = true;
                AppSettings.isPlaying = false;
                App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                {
                    MusicBrowseViewModel.IsPlaying = false;
                });
                progressTimer.Stop();
            }
            else
            {
                if (_currentStream != 0)
                {
                    if (AppSettings.OutputMode.Contains("Wasapi"))
                    {
                        BassWasapi.Start();
                    }
                    else
                    {
                        Bass.ChannelPlay(_currentStream, false);
                    }
                }
                else
                {
                    if (MusicBrowseViewModel.CurrentPlayingMusic is not null)
                    {
                        MusicBrowseViewModel.PlayMusic(MusicBrowseViewModel.CurrentPlayingMusic);
                    }
                    else if (MusicBrowseViewModel.CurrentPlayingList is not null && MusicBrowseViewModel.CurrentPlayingList.Count > 0)
                    {
                        MusicBrowseViewModel.PlayMusic(MusicBrowseViewModel.CurrentPlayingList[0]);
                    }
                    else
                    {
                        notificationService.SendNotification(ToolUtils.GetString("Error"), "没有可播放的音乐");
                        return;
                    }
                }
                isPausing = false;
                AppSettings.isPlaying = true;
                App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                {
                    MusicBrowseViewModel.IsPlaying = true;
                });
                progressTimer.Start();
            }
        }

        public void Play(bool isSettingChanged = false)
        {
            if (_currentStream != 0)
            {
                if (AppSettings.OutputMode.Contains("Wasapi"))
                {
                    // 独占模式下使用WASAPI播放
                    if (InitializePlayback())
                    {
                        BassWasapi.Start();
                    }
                    else
                    {
                        // 如果独占模式启动失败，回退到共享模式
                        Bass.ChannelPlay(_currentStream, false);
                    }
                }
                else
                {
                    // 共享模式下直接播放
                    Bass.ChannelPlay(_currentStream, false);
                }
                if (AppSettings.IsEqualizerEnabled)
                {
                    SetEqualizer();
                }
                progressTimer.Start();
                UpdateProgressTimerUI();
                App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        MusicBrowseViewModel.ProgressSliderMax = Bass.ChannelBytes2Seconds(_currentStream, Bass.ChannelGetLength(_currentStream));
                        if (isSettingChanged)
                        {
                            MusicBrowseViewModel.ProgressSlider = Bass.ChannelBytes2Seconds(_currentStream, Bass.ChannelGetPosition(_currentStream));
                        }
                        else
                        {
                            MusicBrowseViewModel.ProgressSlider = 0;
                        }
                        AppSettings.isPlaying = true;
                        MusicBrowseViewModel.IsPlaying = true;
                        MusicBrowseViewModel.UpdatePlayPauseButtonIcon();
                        _ = MusicDatabaseService.SavePlayState([.. MusicBrowseViewModel.SequentialPlayingList], AppData.PlayMode, MusicBrowseViewModel.CurrentPlayingMusic?.Id, volume, AppData.sortOrder);
                    }
                    catch (Exception)
                    {
                    }
                });
            }
        }

        public void ChangeWaveChannelTime(TimeSpan timeSpan)
        {
            lock (_waveChannelLock)
            {
                if (_currentStream != 0)
                {
                    var targetBytes = Bass.ChannelSeconds2Bytes(_currentStream, timeSpan.TotalSeconds);
                    Bass.ChannelSetPosition(_currentStream, targetBytes);
                }
            }
        }

        public void SetVolume(double volume)
        {
            if (_currentStream != 0)
            {
                if (AppSettings.OutputMode.Contains("WasapiExclusive"))
                {
                    BassWasapi.SetVolume(WasapiVolumeTypes.WindowsHybridCurve, (float)volume);
                }
                else if (AppSettings.OutputMode.Contains("WasapiShared"))
                {
                    BassWasapi.SetVolume(WasapiVolumeTypes.Session, (float)volume);
                }
                else
                {
                    Bass.ChannelSetAttribute(_currentStream, ChannelAttribute.Volume, volume);
                }
            }
        }

        public double GetCurrentPosition()
        {
            if (_currentStream != 0)
            {
                var positionBytes = Bass.ChannelGetPosition(_currentStream);
                return Bass.ChannelBytes2Seconds(_currentStream, positionBytes);
            }
            return 0;
        }

        public double GetTotalPosition()
        {
            if (_currentStream != 0)
            {
                var totalBytes = Bass.ChannelGetLength(_currentStream);
                return Bass.ChannelBytes2Seconds(_currentStream, totalBytes);
            }
            return 0;
        }

        public double AdjustPlaybackPosition(int seconds)
        {
            double newPosition = 0;
            if (AppSettings.isPlaying)
            {
                if (_currentStream != 0)
                {
                    newPosition = GetCurrentPosition() + seconds;
                    newPosition = Math.Max(0, Math.Min(newPosition, GetTotalPosition()));
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
                lock (_streamLock)
                {
                    var currentTime = GetCurrentPosition();
                    if (AppSettings.isPlaying)
                    {
                        Stop();
                        DisposeStream();
                        SetSource(MusicBrowseViewModel.CurrentPlayingMusic);
                        Play(true);
                    }
                    else
                    {
                        DisposeStream();
                        SetSource(MusicBrowseViewModel.CurrentPlayingMusic);
                    }
                    ChangeWaveChannelTime(TimeSpan.FromSeconds(currentTime));
                }
            }
            catch (Exception ex)
            {
                notificationService.SendNotification(ToolUtils.GetString("Error"), ex.Message);
            }

        }

        private void DisposeStream()
        {
            if (_currentStream != 0)
            {
                Bass.StreamFree(_currentStream);
                _currentStream = 0;
            }
            StopWasapiPlayback();
            DisposeEq();
        }

        private void StopWasapiPlayback()
        {
            try
            {
                if (BassWasapi.IsStarted)
                {
                    BassWasapi.Stop(true);
                }
                BassWasapi.Free();
                AppSettings.isPlaying = false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex, $"停止WASAPI播放时出错");
            }
        }
        public void DisposeEq()
        {
            _peakEQ?.Dispose();
            _peakEQ = null;
        }

        public void Dispose()
        {
            DisposeEq();
            CancelPreviousLyricsTask();
            DisposeStream();
            if (progressTimer is not null)
            {
                progressTimer.Stop();
                progressTimer.Elapsed -= ProgressTimer_Elapsed;
                progressTimer.Dispose();
                progressTimer = null;
            }
            if (BassWasapi.IsStarted)
            {
                BassWasapi.Stop(true);
            }
            _syncEndCallback -= OnPlayBackEnded;
            _syncFailCallback -= OnPlaybackFailed;
            BassWasapi.Free();
            BassManager.Free();
        }
    }
}
