using CommunityToolkit.Mvvm.Messaging.Messages;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.Media.Protection.PlayReady;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Utils;
using static CommunityToolkit.Mvvm.ComponentModel.__Internals.__TaskExtensions.TaskAwaitableWithoutEndValidation;

namespace WinUIMusicPlayer.Services
{
    public class IpcService
    {
        private const string PipeName = "BassPlayerPipe";
        private NamedPipeClientStream _client;
        private StreamReader _reader;
        private StreamWriter _writer;
        public IpcService()
        {
            try
            {
                //Process.Start(new ProcessStartInfo
                //{
                //    FileName = "BassPlayerSharp.exe",
                //    CreateNoWindow = true,
                //    UseShellExecute = false,
                //});
                _client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
                _client.Connect(); // 连接到服务进程
                _reader = new StreamReader(_client);
                _writer = new StreamWriter(_client)
                {
                    AutoFlush = true // 🚨 确保数据立即发送到管道
                };
            }
            catch (Exception ex)
            {
                // 错误处理，例如子进程文件未找到
                System.Diagnostics.Debug.WriteLine($"Error starting service: {ex.Message}");
            }
        }



        public async Task<ResponseMessage> SendCommandAsync(string command, string data)
        {
            try
            {
                var request = new RequestMessage { Command = command, Data = data };
                string requestJson = JsonSerializer.Serialize(request, PlayerJsonContext.Default.RequestMessage);

                // 2. 发送请求
                await _writer.WriteLineAsync(requestJson);
                Debug.WriteLine($"Sent request: {requestJson}");

                // 3. 读取响应
                string responseJson = await _reader.ReadLineAsync();
                Debug.WriteLine($"Received response: {responseJson}");

                if (responseJson == null)
                {
                    throw new IOException("Server closed the pipe unexpectedly.");
                }
                // 4. 反序列化响应
                return JsonSerializer.Deserialize(responseJson, PlayerJsonContext.Default.ResponseMessage);
            }
            catch (TimeoutException)
            {
                return new ResponseMessage { Type = 0, Message = "Connection timeout." };
            }
            catch (Exception ex)
            {
                return new ResponseMessage { Type = 0, Message = $"Communication error: {ex.Message}" };
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

        public void UpdateSettings()
        {
            var settings = new IpcSetting
            {
                PlayMode = ToolUtils.PlayModeToString(AppData.PlayMode),
                OutputMode = AppSettings.OutputMode,
                BassOutputDeviceId = AppSettings.BassOutputDeviceId,
                BassASIODeviceId = AppSettings.BassASIODeviceId,
                Latency = AppSettings.Latency,
                IsDopEnabled = AppSettings.IsDopEnabled,
                dsdGain = AppSettings.dsdGain,
                dsdPcmFreq = AppSettings.dsdPcmFreq,
                IsEqualizerEnabled = AppSettings.IsEqualizerEnabled
            };
            _ = SendCommandAsync("UpdateSettings", JsonSerializer.Serialize(settings));
        }

        public void SetMusicUrl(string musicUrl)
        {
            _ = SendCommandAsync("SetMusicUrl", musicUrl);
        }

        public async Task<double> GetCurrentPostion()
        {
            return double.Parse((await SendCommandAsync("GetProgress", "")).Result);
        }

        public async Task<double> GetDuration()
        {
            return double.Parse((await SendCommandAsync("GetDuration", "")).Result);
        }

        public void SetPosition(double position)
        {
            _ = SendCommandAsync("ChangePosition", position.ToString());
        }
    }
}
