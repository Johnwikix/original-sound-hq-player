using System.Text.Json.Serialization;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Helper;

[JsonSerializable(typeof(VersionRecord))]
[JsonSourceGenerationOptions(WriteIndented = true)]
public partial class VersionJsonContext : JsonSerializerContext
{
}
