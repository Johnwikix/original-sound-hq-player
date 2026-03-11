using ATL;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.Services
{
    public class IpcService
    {
        private const string MmfName = "BassPlayerSharp_SharedMemory";
        private const string RequestSemaphoreName = "BassPlayerSharp_RequestReady";
        private const string ResponseSemaphoreName = "BassPlayerSharp_ResponseReady";
        private const string NotificationSemaphoreName = "BassPlayerSharp_NotificationReady";
        private const int MaxMessageSize = 4096;
        private const int MaxResponseSize = 1024;
        private static readonly long MmfSize = MaxMessageSize + MaxResponseSize * 2;

        private const long RequestBufferOffset = 0;
        private static readonly long ResponseBufferOffset = MaxMessageSize;
        private static readonly long NotificationBufferOffset = MaxMessageSize + MaxResponseSize;
        // 共享内存和同步对象
        private MemoryMappedFile _mmf;
        private MemoryMappedViewAccessor _accessor;
        private Semaphore _requestReadySemaphore;
        private Semaphore _responseReadySemaphore;
        private Semaphore _notificationReadySemaphore;

        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
        private bool _isConnected = false;

        private CancellationTokenSource _notificationCts;
        private Task _notificationListenerTask;

        // 新增：通知事件，外部可订阅
        public event Action<ResponseMessage> NotificationReceived;

        public void Initializing()
        {
            try
            {
                _mmf = MemoryMappedFile.OpenExisting(MmfName);
                _accessor = _mmf.CreateViewAccessor(0, MmfSize);
                _requestReadySemaphore = Semaphore.OpenExisting(RequestSemaphoreName);
                _responseReadySemaphore = Semaphore.OpenExisting(ResponseSemaphoreName);
                _notificationReadySemaphore = Semaphore.OpenExisting(NotificationSemaphoreName);
                _isConnected = true;
                StartNotificationListener();
            }
            catch (FileNotFoundException)
            {
                _isConnected = false;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                _isConnected = false;
            }
            catch (Exception)
            {
                _isConnected = false;
            }
        }

        public async Task InitializeMusic(Music music)
        {
            if (music is not null) {
                await SetMusicUrl(music.Path);
            }            
            await UpdateEq();
            UpdateSettings();
        }

        private void StartNotificationListener()
        {
            _notificationCts = new CancellationTokenSource();
            _notificationListenerTask = Task.Run(() => ListenForNotificationsAsync(_notificationCts.Token));
        }

        private async Task ListenForNotificationsAsync(CancellationToken cancellationToken)
        {
            Debug.WriteLine("Notification listener started...");
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    bool hasNotification = await Task.Run(() =>
                        _notificationReadySemaphore.WaitOne(1000), cancellationToken);
                    if (cancellationToken.IsCancellationRequested) break;

                    if (hasNotification)
                    {
                        string notificationJson = ReadFromSharedMemory(NotificationBufferOffset);
                        if (!string.IsNullOrEmpty(notificationJson))
                        {
                            var notification = JsonSerializer.Deserialize(
                                notificationJson,
                                PlayerJsonContext.Default.ResponseMessage);
                            NotificationReceived?.Invoke(notification);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception)
                {
                    await Task.Delay(500, cancellationToken); // 出错后短暂等待
                }
            }
        }

        /// <summary>
        /// 断开连接并释放资源
        /// </summary>
        public async Task Dispose()
        {
            _accessor?.Dispose();
            _mmf?.Dispose();
            // 客户端打开的命名信号量只需要关闭句柄，不需要释放
            _requestReadySemaphore?.Dispose();
            _responseReadySemaphore?.Dispose();
            _sendLock?.Dispose();
            _isConnected = false;
            Debug.WriteLine("Shared Memory Client Disposed.");
        }


        public async Task<ResponseMessage> SendCommandAsync(string command, string data)
        {
            if (!_isConnected)
            {
                return new ResponseMessage { Type = 0, Message = "Connection failed. Server not ready." };
            }

            // 使用本地锁确保同一时间只有一个请求在 MMF 上进行，防止数据竞争
            await _sendLock.WaitAsync();
            try
            {
                var request = new RequestMessage { Command = command, Data = data };
                string requestJson = JsonSerializer.Serialize(request, PlayerJsonContext.Default.RequestMessage);
                WriteToSharedMemory(RequestBufferOffset, requestJson);
                try
                {
                    _requestReadySemaphore.Release();
                }
                catch (SemaphoreFullException)
                {
                    Debug.WriteLine("Warning: Request semaphore was already signaled.");
                }

                // 3. 等待 Response 信号量
                bool responded = await Task.Run(() => _responseReadySemaphore.WaitOne(1000));
                if (!responded)
                {
                    return new ResponseMessage { Type = MessageType.Failed, Message = "Server response timeout (1s)." };
                }
                string responseJson = ReadFromSharedMemory(ResponseBufferOffset);
                if (string.IsNullOrEmpty(responseJson))
                {
                    return new ResponseMessage { Type = MessageType.Failed, Message = "Received empty response from server." };
                }
                return JsonSerializer.Deserialize(responseJson, PlayerJsonContext.Default.ResponseMessage);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Shared Memory Error: {ex.Message}");
                return new ResponseMessage { Type = 0, Message = $"Communication error: {ex.Message}" };
            }
            finally
            {
                _sendLock.Release();
            }
        }

        // 将字符串写入 MMF
        private void WriteToSharedMemory(long offset, string json)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            int length = bytes.Length;

            if (length > MaxMessageSize - sizeof(int))
            {
                // 截断
                length = MaxMessageSize - sizeof(int);
                bytes = Encoding.UTF8.GetBytes(json[..((MaxMessageSize - sizeof(int)) / 3)]);
                length = bytes.Length;
                Debug.WriteLine("Warning: Client message truncated due to size limit.");
            }

            _accessor.Write(offset, length);
            _accessor.WriteArray(offset + sizeof(int), bytes, 0, length);
        }

        //从 MMF 读取字符串 
        private string ReadFromSharedMemory(long offset)
        {
            try
            {
                int length = _accessor.ReadInt32(offset);
                if (length <= 0 || length > MaxMessageSize - sizeof(int))
                {
                    return string.Empty; // 无效长度
                }
                byte[] buffer = new byte[length];
                _accessor.ReadArray(offset + sizeof(int), buffer, 0, length);
                _accessor.Write(offset, 0);
                return Encoding.UTF8.GetString(buffer);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error reading from MMF: {ex.Message}");
                return string.Empty;
            }
        }

        public void Play(string musicUrl)
        {
            _ = SendCommandAsync("Play", musicUrl);
        }

        public void PlayButton()
        {
            _ = SendCommandAsync("PlayButton", "");
        }

        public void UpdateSettings(bool IsSettingChanged = false)
        {
            var settings = new IpcSetting
            {
                OutputMode = AppSettings.OutputMode,
                BassOutputDeviceId = AppSettings.BassOutputDeviceId,
                BassASIODeviceId = AppSettings.BassASIODeviceId,
                Latency = AppSettings.Latency,
                IsDopEnabled = AppSettings.IsDopEnabled,
                dsdGain = AppSettings.DsdGain,
                dsdPcmFreq = AppSettings.DsdPcmFreq,
                IsEqualizerEnabled = AppSettings.IsEqualizerEnabled,
                Volume = App.Services.GetRequiredService<AppViewModel>().Volume / 100,
                IsSettingChanged = IsSettingChanged,
                IsFadeEnabled = AppSettings.IsFadeEnabled,
            };
            _ = SendCommandAsync("UpdateSettings", JsonSerializer.Serialize(settings, IpcJsonContext.Default.IpcSetting));
        }

        public async Task UpdateEq()
        {
            await SendCommandAsync("UpdateEq", AppSettings.EqualizerStr);
        }

        public async Task SetMusicUrl(string musicUrl)
        {
            await SendCommandAsync("SetMusicUrl", musicUrl);
        }

        public async Task<double> GetCurrentPostion()
        {
            var res = await SendCommandAsync("GetProgress", "");
            if (res.Type == 20)
            {
                return double.Parse(res.Result);
            }
            return 0;
        }

        public async Task<double> GetDuration()
        {
            var res = await SendCommandAsync("GetDuration", "");
            if (res.Type == 21)
            {
                return double.Parse(res.Result);
            }
            return 0;
        }

        public void SetPosition(double position)
        {
            _ = SendCommandAsync("ChangePosition", position.ToString());
        }

        public void ChangeVolume(double volume)
        {
            _ = SendCommandAsync("ChangeVolume", volume.ToString());
        }

        public async Task<double> AdjustPlaybackPosition(int seconds)
        {
            var res = await SendCommandAsync("AdjustPlaybackPosition", seconds.ToString());
            if (res.Type == 22)
            {
                return double.Parse(res.Result);
            }
            return 0;
        }

        public void MusicEnd()
        {
            _ = SendCommandAsync("MusicEnd", string.Empty);
        }
        public void ToggleEqualizer()
        {
            _ = SendCommandAsync("ToggleEqualizer", string.Empty);
        }

        public void SetEqualizerGain(int bandIndex, float gain)
        {
            var ipcEqGain = new IpcEqualizerGain
            {
                bandIndex = bandIndex,
                gain = gain
            };
            _ = SendCommandAsync("SetEqualizerGain", JsonSerializer.Serialize(ipcEqGain, IpcEqualizerGainJsonContext.Default.IpcEqualizerGain));
        }


        public void SetEqualizer()
        {
            _ = SendCommandAsync("SetEqualizer", string.Empty);
        }

        public void ClearEqualizer()
        {
            _ = SendCommandAsync("ClearEqualizer", string.Empty);
        }

        public void FadeOut()
        {
            _ = SendCommandAsync("FadeOut", string.Empty);
        }

    }
}
