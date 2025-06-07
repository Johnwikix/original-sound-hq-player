using CSCore;
using CSCore.Ffmpeg;
using CSCore.Streams.Effects;
using NAudio.CoreAudioApi;
using NAudio.Dsp;
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
        //public LightweightCSCoreAdapter adapter;
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
        public event EventHandler<float[]> updateSpectrumData;
        //public List<Music> musicList;
        public bool isUserDraggingProgressSlider = false;
        public bool isInitializing = true;
        private NotificationService notificationService;
        public event EventHandler<int> updateCurrentLyricIndex;
        public List<LyricLine> _lyrics = new List<LyricLine>();
        private LrcService lrcService = new LrcService();
        private CancellationTokenSource _lyricsCancellationTokenSource;
        private float[] _fftBuffer;
        private Complex[] _complexBuffer;
        private int _fftPosition;
        private int _fftLength = 64; // FFT点数
        private int _m; // FFT阶数
        private int _barCount = 16; // 柱状图数量
        private float[] _spectrumData;
        private readonly object _spectrumDataLock = new object();
        private volatile bool _hasNewData = false;
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
        private bool isEnableEq = false;

        public MusicPlaybackService()
        {
            notificationService = new NotificationService();
            progressTimer = new System.Timers.Timer(250);
            progressTimer.Elapsed += ProgressTimer_Elapsed;
            InitializingData();
            _m = (int)Math.Log(_fftLength, 2);
            _fftBuffer = new float[_fftLength];
            _complexBuffer = new NAudio.Dsp.Complex[_fftLength];
            _spectrumData = new float[_barCount];
            
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
                    //else if (adapter != null)
                    //{
                    //    currentTimeSeconds = adapter.CurrentTime.TotalSeconds;
                    //    totalSeconds = adapter.TotalTime.TotalSeconds;
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
                //UpdateSpectrumCallback();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"进度条更新失败: {ex.Message}");
            }
        }

        private void UpdateSpectrumCallback()
        {            
            if (!_hasNewData) return;            
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
                    double currentTimeSeconds = waveChannel.CurrentTime.TotalSeconds;
                    double totalSeconds = waveChannel.TotalTime.TotalSeconds;
                    newPosition = currentTimeSeconds + seconds;
                    newPosition = Math.Max(0, Math.Min(newPosition, totalSeconds));
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

        public async void ResumeMusic()
        {
            if (AppSettings.isPlaying)
            {
                if (waveChannel != null && equalizer != null)
                {
                    isEnableEq = true;
                    SelectOutputDevice();
                    waveOut.Init(equalizer);
                    waveOut.Play();

                }
                else if (waveChannel != null)
                {
                    isEnableEq = false;
                    SelectOutputDevice();
                    waveOut.Init(waveChannel);
                    waveOut.Play();
                }
                AppSettings.isPlaying = true;
                progressTimer.Start();
            }
            else {
                var currentPos = waveChannel?.CurrentTime ?? TimeSpan.Zero;
                if (waveOut != null)
                {
                    waveOut.Stop();
                    waveOut.Dispose();
                    waveOut = null;
                    await InitializeAudioResources(currentPlayingMusic, currentPos);
                }                               
            }            
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

        // 切换均衡器开关
        public async void ToggleEqualizer()
        {
            // 只有在启用状态变化且音频播放时才需要重新初始化
            if (AppSettings.IsEqualizerEnabled && !isEnableEq)
            {
                var currentPos = waveChannel?.CurrentTime ?? TimeSpan.Zero;
                if (waveOut != null)
                {
                    waveOut.Stop();
                    waveOut.Dispose();
                    waveOut = null;
                    await InitializeAudioResources(currentPlayingMusic, currentPos);
                }
                
                if (AppSettings.isPlaying)
                {
                    waveOut.Play();
                    progressTimer.Start();
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
                progressTimer.Stop();
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
                        waveChannel = new WaveChannel32(multiTypeAudioReader);
                        waveChannel.CurrentTime = currentPos;
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
                        var adapter = new LightweightCSCoreAdapter(ffmpegDecoder);
                        waveChannel = new WaveChannel32(adapter);
                        waveChannel.CurrentTime = currentPos;
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
                //TO DO 波形可视化
                //waveChannel.Sample += WaveChannel_Sample;
                if (AppSettings.IsEqualizerEnabled)
                {
                    isEnableEq = true;                    
                    var sampleProvider = waveChannel.ToSampleProvider();
                    equalizer = new CustomEqualizer(sampleProvider, equalizerBands);
                    waveOut.Init(equalizer);
                }
                else
                {
                    isEnableEq = false;
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

        private void WaveChannel_Sample(object? sender, SampleEventArgs e)
        {
            float sample = (e.Left + e.Right)/2;
            _fftBuffer[_fftPosition] = sample * (float)FastFourierTransform.HannWindow(_fftPosition, _fftLength);
            _fftPosition++;            
            if (_fftPosition >= _fftLength)
            {                
                _fftPosition = 0;
                //Debug.WriteLine($"sample：{sample}");
                //Debug.WriteLine($"时间：{DateTime.Now:HH:mm:ss.fff}, data: [{string.Join(", ", _spectrumData)}]");
                lock (_spectrumDataLock)
                {                    
                    // 异步通知UI更新
                    Task.Run(() => {
                        CalculateSpectrum(_fftBuffer);
                        updateSpectrumData?.Invoke(this, _spectrumData);
                    });
                }
            }           
        }

        // 计算频谱
        private void CalculateSpectrum(float[] fftBuffer)
        {
            Debug.WriteLine($"时间：{DateTime.Now:HH:mm:ss.fff}, data: [{string.Join(", ", fftBuffer)}]");
            Parallel.For(0, _fftLength, i =>
            {
                _complexBuffer[i].X = fftBuffer[i];
                _complexBuffer[i].Y = 0;
            });

            FastFourierTransform.FFT(true, _m, _complexBuffer);

            int spectrumSize = _barCount;
            int pointsPerBin = _fftLength / 2 / spectrumSize;

            // 并行计算频谱数据
            Parallel.For(0, spectrumSize, i =>
            {
                float sum = 0;
                int startIndex = i * pointsPerBin;
                int endIndex = Math.Min(startIndex + pointsPerBin, _fftLength / 2);

                for (int j = startIndex; j < endIndex; j++)
                {
                    float magnitude = (float)Math.Sqrt(
                        _complexBuffer[j].X * _complexBuffer[j].X +
                        _complexBuffer[j].Y * _complexBuffer[j].Y);
                    sum += magnitude;
                }

                float average = sum / (endIndex - startIndex);
                float dbValue = 20 * (float)Math.Log10(average + 0.0001f);
                float normalizedValue = Math.Max(0, Math.Min(1, (dbValue + 90) / 90));

                // 添加平滑处理，避免频谱跳跃
                _spectrumData[i] = _spectrumData[i] * 0.7f + normalizedValue * 0.3f;
            });
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
            isEnableEq = false;
            if (waveOut != null)
            {
                waveOut.Stop();
                waveOut.Dispose();
                waveOut = null;
            }

            if (waveChannel != null)
            {
                waveChannel.Sample -= WaveChannel_Sample; // 取消事件订阅
                waveChannel.Dispose();
                waveChannel = null;
            }

            if (equalizer != null) {                
                equalizer = null;
            }

            //if (adapter != null) {
            //    adapter.Dispose();
            //    adapter = null;
            //}

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
                waveChannel.Sample -= WaveChannel_Sample;
                waveChannel.Dispose();
                waveChannel = null;
            }

            if(equalizer != null)
            {                
                equalizer = null;
            }

            //if (adapter != null) {
            //    adapter.Dispose();
            //    adapter = null;
            //}
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
                progressTimer.Stop();
                waveOut.Stop();
                AppSettings.isPlaying = false;                
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
