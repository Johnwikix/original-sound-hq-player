using System.Text.Json.Serialization;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Helper
{
    [JsonSourceGenerationOptions(WriteIndented = false)]
    [JsonSerializable(typeof(RequestMessage))] // 客户端请求消息
    [JsonSerializable(typeof(ResponseMessage))]  // 服务器响应消息
    public partial class PlayerJsonContext : JsonSerializerContext
    {
    }
}
