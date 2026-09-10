using System.Text.Json.Serialization;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Helper;

// Standalone tests use the real preset helper with a minimal generated JSON context.
[JsonSerializable(typeof(EqPreset))]
internal partial class AppJsonSerializerContextHelper : JsonSerializerContext { }
