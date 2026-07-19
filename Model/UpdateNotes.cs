using System.Text.Json.Serialization;

namespace WinUIMusicPlayer.Model;

public class UpdateNotes
{
    [JsonPropertyName("zh-CN")]
    public string ZhCN { get; set; } = string.Empty;

    [JsonPropertyName("en")]
    public string En { get; set; } = string.Empty;
}
