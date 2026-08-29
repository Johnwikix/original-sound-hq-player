using System.Text.Json.Serialization;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Helper
{
    [JsonSerializable(typeof(SaveDesktopLyricsState))]
    [JsonSourceGenerationOptions(WriteIndented = true)]
    public partial class DesktopLyricsStateJsonContext : JsonSerializerContext
    {
    }
}
