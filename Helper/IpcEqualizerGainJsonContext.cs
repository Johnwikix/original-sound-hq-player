using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Helper
{
    [JsonSerializable(typeof(IpcEqualizerGain))]
    public partial class IpcEqualizerGainJsonContext : JsonSerializerContext
    {
    }
}
