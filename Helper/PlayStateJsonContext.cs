using System.Text.Json.Serialization;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Helper
{
    [JsonSerializable(typeof(SavePlayState))]
    [JsonSourceGenerationOptions(WriteIndented = true)]
    public partial class PlayStateJsonContext : JsonSerializerContext
    {
    }
}
