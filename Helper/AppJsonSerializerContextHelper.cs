using System.Collections.Generic;
using System.Text.Json.Serialization;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Helper
{
    [JsonSerializable(typeof(Dictionary<string, double>))]
    [JsonSerializable(typeof(EqPreset))]
    public partial class AppJsonSerializerContextHelper : JsonSerializerContext
    {
    }
}
