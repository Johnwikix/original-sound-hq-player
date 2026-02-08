using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Helper
{
    [JsonSerializable(typeof(IpcSetting))]
    public partial class IpcJsonContext : JsonSerializerContext
    {
    }
}
