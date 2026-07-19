using System.Text.Json.Serialization;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Helper;

[JsonSerializable(typeof(UpdateNotes))]
[JsonSourceGenerationOptions(WriteIndented = true)]
public partial class UpdateNotesJsonContext : JsonSerializerContext
{
}
