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
using WinUIMusicPlayer.Model;
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
                Process.Start(new ProcessStartInfo
                {
                    FileName = "BassPlayerSharp.exe",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                });
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
                string requestJson = JsonSerializer.Serialize(request);

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
                return JsonSerializer.Deserialize<ResponseMessage>(responseJson);
            }
            catch (TimeoutException)
            {
                return new ResponseMessage { Success = false, Message = "Connection timeout." };
            }
            catch (Exception ex)
            {
                return new ResponseMessage { Success = false, Message = $"Communication error: {ex.Message}" };
            }
        }

        /// <summary>
        /// 将命令字符串发送给子进程的 PlaybackService
        /// </summary>
        /// <param name="command">要发送的格式化命令字符串（例如: CMD:PLAY）</param>
        public void SendCommand(string command)
        {
            if (_writer != null && _client.IsConnected)
            {
                try
                {
                    _writer.WriteLine(command);
                    // 因为 AutoFlush=true，所以 WriteLine 就会发送数据
                }
                catch (IOException ioEx)
                {
                    // 处理管道断开连接的错误（例如子进程崩溃或关闭）
                    System.Diagnostics.Debug.WriteLine($"Error writing to command pipe: {ioEx.Message}");
                }
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("Cannot send command: Pipe is not connected.");
            }
        }

        public void Play()
        {
            _ = SendCommandAsync("PLAY", "PLAYURL");
        }
    }
}
