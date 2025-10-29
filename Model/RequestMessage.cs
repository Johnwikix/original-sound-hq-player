using System.Text.Json.Serialization;

namespace WinUIMusicPlayer.Model
{
    public class RequestMessage
    {
        [JsonPropertyName("command")]
        public string Command { get; set; } // 例如: "Play", "Pause", "Stop"

        [JsonPropertyName("data")]
        public string Data { get; set; } // 相关的命令数据，例如文件路径
    }
}
