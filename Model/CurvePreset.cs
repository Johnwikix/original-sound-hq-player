using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace WinUIMusicPlayer.Model;

// The legacy gain fields remain readable; a curve preset now describes only its response.
public sealed record CurvePreset(string Name, string Points, bool AutoHeadroom = true, double TrimDb = 0);
[JsonSerializable(typeof(List<CurvePreset>))]
internal partial class CurvePresetJsonContext : JsonSerializerContext { }
