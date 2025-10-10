using ManagedBass;
using ManagedBass.Asio;
using ManagedBass.Dsd;
using ManagedBass.Fx;
using ManagedBass.Tags;
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
        public MusicBrowseViewModel MusicBrowseViewModel { get; }
        private readonly Lock _streamLock = new();
        private readonly Lock _waveChannelLock = new();
        //private CancellationTokenSource _currentOperationCts;
        private volatile bool _isDisposing = false;
        private readonly SystemMediaControlsService _systemMediaControlsService = App.Services.GetRequiredService<SystemMediaControlsService>();
        private double _totalSeconds;
        private double _currentSeconds;
        private readonly int[] _bandIndices = new int[10];
        private readonly float[] _eqFrequencies = { 32, 64, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 }; // 10频段
        private double MinDb = -60;
        private double MaxDb = 0;
        private double MiddleDb = -30;
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
            string? lrcContent = GetLyricsContentFromLrc(AppData.allSongs.AsValueEnumerable().FirstOrDefault(m => m.Id == MusicBrowseViewModel.CurrentPlayingMusic?.Id)?.Path);
            var lyricsContent = await ParseLrcLyrics(lrcContent);
            if (lyricsContent is not null)
            {
                _lyrics = lyricsContent;
            }
        }

        private string GetLyricsContentFromLrc(string? path)
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
            lrcContent = AppData.allSongs.AsValueEnumerable().FirstOrDefault(m => m.Id == MusicBrowseViewModel.CurrentPlayingMusic?.Id)?.Lyrics;
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
            if (AppSettings.IsEqualizerEnabled 
                && !(AppSettings.IsDopEnabled 
                && (AppSettings.OutputMode.Contains("WasapiExclusive") ||AppSettings.OutputMode == "ASIO")
                && (MusicBrowseViewModel.CurrentPlayingMusic.Extension.Equals("dsf", StringComparison.OrdinalIgnoreCase) || MusicBrowseViewModel.CurrentPlayingMusic.Extension.Equals("dff", StringComparison.OrdinalIgnoreCase)))                    
               )
            {
                try
                {
                    if (_currentStream != 0)
                    {
                        _peakEQ = new PeakEQ(_currentStream, Q: 0, Bandwith: 1.0);
                        // 为每个频段添加Band
                        for (int i = 0; i < _eqFrequencies.Length; i++)
                        {
                            _bandIndices[i] = _peakEQ.AddBand(_eqFrequencies[i]);
                            //Debug.WriteLine($"均衡器频段 {_eqFrequencies[i]}Hz 创建成功，Band索引: {_bandIndices[i]}");
                        }
                        //Debug.WriteLine("均衡器初始化完成");
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
                //Debug.WriteLine($"无效的频段索引: {bandIndex}");
                return;
            }
            if (_peakEQ == null)
            {
                //Debug.WriteLine("均衡器未初始化");
                return;
            }

            try
            {
                // 使用UpdateBand方法更新指定频段的增益
                _peakEQ.UpdateBand(_bandIndices[bandIndex], gain);
                //Debug.WriteLine($"频段 {_eqFrequencies[bandIndex]}Hz (Band {_bandIndices[bandIndex]}) 增益设置为 {gain}dB");
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

        private bool SwitchDevice(ChannelInfo channelInfo)
        {
            bool result = false;                 
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
                case "ASIO":
                    result = BassAsio.Init(AppSettings.BassASIODeviceId,AsioInitFlags.Thread);
                    break;
            }
            if (AppSettings.OutputMode.Contains("Wasapi")) {
                BassWasapi.GetInfo(out var info);
                MaxDb = info.MaxVolume;
                MinDb = info.MinVolume;
                MiddleDb = (MinDb + MaxDb) / 2;
            }            
            return result;
        }

        private bool InitializePlayback()
        {
            try
            {
                // 初始化播放模式
                Bass.ChannelGetInfo(_currentStream,out var channelInfo);      
                var result = SwitchDevice(channelInfo);
                if (!result)
                {
                    StopWasapiPlayback();
                    StopAsioPlayback();
                    result = SwitchDevice(channelInfo);
                    if (!result)
                    {
                        return false;
                    }
                }
                // 设置音量
                if (AppSettings.OutputMode.Contains("WasapiShared"))
                {
                    BassWasapi.SetVolume(WasapiVolumeTypes.Session, (float)volume);
                }
                else if (AppSettings.OutputMode.Contains("WasapiExclusive"))
                {
                    if (volume > 0.7)
                    {
                        volume = (float)DbToLinear(MiddleDb);
                        BassWasapi.SetVolume(WasapiVolumeTypes.LogaritmicCurve, (float)MiddleDb);
                        App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                        {
                            MusicBrowseViewModel.Volume = volume * 100;
                        });
                    }
                    else
                    {                        
                        BassWasapi.SetVolume(WasapiVolumeTypes.LogaritmicCurve, (float)LinearToDb(volume));
                    }
                }
                else if (AppSettings.OutputMode == "ASIO") {
                    BassAsio.ChannelEnableBass(false, 0, _currentStream, true);
                    BassAsio.ChannelSetFormat(false, 0, AsioSampleFormat.Float);
                    BassAsio.Rate = channelInfo.Frequency;
                    BassAsio.ChannelSetVolume(false, -1, volume);
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
                BassDsd.DefaultGain = AppSettings.dsdGain;
                BassDsd.DefaultFrequency = AppSettings.dsdPcmFreq;
                if (AppSettings.OutputMode.Contains("WasapiExclusive"))
                {                    
                    if (AppSettings.IsDopEnabled && (music.Extension.Equals("dsf", StringComparison.OrdinalIgnoreCase) || music.Extension.Equals("dff", StringComparison.OrdinalIgnoreCase)))
                    {
                        _currentStream = BassDsd.CreateStream(music.Path, 0, 0, BassFlags.DSDOverPCM | BassFlags.Float | BassFlags.Decode | BassFlags.AsyncFile);
                    }
                    else
                    {
                        _currentStream = Bass.CreateStream(music.Path, 0, 0, BassFlags.Unicode | BassFlags.Float | BassFlags.AsyncFile | BassFlags.Decode);
                    }
                }
                else if (AppSettings.OutputMode.Contains("WasapiShared"))
                {
                    _currentStream = Bass.CreateStream(music.Path, 0, 0, BassFlags.Unicode | BassFlags.Float | BassFlags.AsyncFile | BassFlags.Decode);
                }
                else if (AppSettings.OutputMode == "ASIO") {
                    _currentStream = Bass.CreateStream(music.Path, 0, 0, BassFlags.Float | BassFlags.AsyncFile | BassFlags.Decode);
                }
                else
                {
                    _currentStream = Bass.CreateStream(music.Path, 0, 0, BassFlags.Default | BassFlags.AsyncFile);
                }
                if (_currentStream == 0)
                {
                    notificationService.SendNotification(ToolUtils.GetString("Error"), $"创建流失败: {Bass.LastError}");
                    return;
                }
                Bass.ChannelSetSync(_currentStream, SyncFlags.End, 0, _syncEndCallback); // 设置播放结束回调
                Bass.ChannelSetSync(_currentStream, SyncFlags.Stalled, 0, _syncFailCallback); // 设置播放失败回调
                ToggleEqualizer();
                // 根据模式设置音量
                if (!AppSettings.OutputMode.Contains("Wasapi") && AppSettings.OutputMode != "ASIO")
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
                }else if (AppSettings.OutputMode == "ASIO") {
                    BassAsio.Stop();
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
                    } else if (AppSettings.OutputMode == "ASIO") {
                        BassAsio.Start();
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
                } else if (AppSettings.OutputMode == "ASIO") {
                    if (InitializePlayback())
                    {
                        BassAsio.Start();
                    }
                    else
                    {
                        // 如果ASIO模式启动失败，回退到共享模式
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
                    BassWasapi.SetVolume(WasapiVolumeTypes.LogaritmicCurve, (float)LinearToDb(volume));
                }
                else if (AppSettings.OutputMode.Contains("WasapiShared"))
                {
                    BassWasapi.SetVolume(WasapiVolumeTypes.Session, (float)volume);
                } else if (AppSettings.OutputMode == "ASIO") {
                    BassAsio.ChannelSetVolume(false, -1, volume);
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
                        SetSource(MusicBrowseViewModel.CurrentPlayingMusic);
                        Play(true);
                    }
                    else
                    {
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

        public double LinearToDb(double linearValue)
        {
            if (linearValue <= 0)
                return MinDb;
            if (linearValue >= 1) {
                return MaxDb;
            }
            // 映射到0到-65.25dB的范围
            double dbValue = MaxDb + (MinDb - MaxDb) * (1 - Math.Log10(9 * linearValue + 1) / Math.Log10(10));
            return dbValue;
        }

        public double DbToLinear(double dbValue)
        {
            dbValue = Math.Clamp(dbValue, MinDb, MaxDb);
            if (dbValue <= MinDb)
                return 0;
            double dbPosition = (dbValue - MaxDb) / (MinDb - MaxDb);
            return (Math.Pow(10, (1 - dbPosition) * Math.Log10(10)) - 1) / 9;
        }

        private void DisposeStream()
        {
            if (_currentStream != 0)
            {
                Bass.StreamFree(_currentStream);
                _currentStream = 0;
            }
            StopWasapiPlayback();
            StopAsioPlayback();
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
        private void StopAsioPlayback()
        {
            try
            {
                if (BassAsio.IsStarted)
                {
                    BassAsio.Stop();
                }
                var asioFree = BassAsio.Free();
                if (!asioFree)
                {
                    Debug.WriteLine($"释放ASIO失败: {Bass.LastError}");
                }
                else {
                    Debug.WriteLine($"释放ASIO成功");
                }
                AppSettings.isPlaying = false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex, $"停止ASIO播放时出错");
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
            _syncEndCallback -= OnPlayBackEnded;
            _syncFailCallback -= OnPlaybackFailed;
            BassManager.Free();
        }
    }
}
