using System.Text.Json.Serialization;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Helper
{
    [JsonSerializable(typeof(SaveSettings))]
    [JsonSourceGenerationOptions(WriteIndented = true)]
    public partial class SettingsJsonContext : JsonSerializerContext
    {
    }
}
