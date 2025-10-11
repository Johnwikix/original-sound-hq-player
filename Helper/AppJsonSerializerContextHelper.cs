using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace WinUIMusicPlayer.Helper
{
    [JsonSerializable(typeof(Dictionary<string, double>))]
    public partial class AppJsonSerializerContextHelper : JsonSerializerContext
    {
    }
}
