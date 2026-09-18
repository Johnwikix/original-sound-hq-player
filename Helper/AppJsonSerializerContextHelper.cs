using System.Collections.Generic;
using System.Text.Json.Serialization;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;

namespace WinUIMusicPlayer.Helper
{
    [JsonSerializable(typeof(Dictionary<string, double>))]
    [JsonSerializable(typeof(EqPreset))]
    [JsonSerializable(typeof(OneShotLyricsCacheEntry))]
    public partial class AppJsonSerializerContextHelper : JsonSerializerContext
    {
    }
}
