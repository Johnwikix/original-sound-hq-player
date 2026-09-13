using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.Advance;
using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinUIMusicPlayer.Model;

// Read the experimental name written by earlier builds; always save the new name.
public sealed class LyricsEasingTypeJsonConverter : JsonConverter<EasingType>
{
    public override EasingType Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            string? value = reader.GetString();
            if (string.Equals(value, "AppleMusic", StringComparison.OrdinalIgnoreCase)) return EasingType.FlowWave;
            if (Enum.TryParse<EasingType>(value, true, out var result)) return result;
        }
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int number)) return (EasingType)number;
        throw new JsonException("Invalid lyrics easing curve.");
    }
    public override void Write(Utf8JsonWriter writer, EasingType value, JsonSerializerOptions options)
    {
        if (Enum.IsDefined(value)) writer.WriteStringValue(value.ToString());
        else writer.WriteNumberValue((int)value);
    }
}

public sealed class LyricsEaseModeJsonConverter : JsonConverter<EaseMode>
{
    public override EaseMode Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            string? value = reader.GetString();
            if (string.Equals(value, "AppleMusic", StringComparison.OrdinalIgnoreCase)) return EaseMode.FlowWave;
            if (Enum.TryParse<EaseMode>(value, true, out var result)) return result;
        }
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int number)) return (EaseMode)number;
        throw new JsonException("Invalid lyrics easing direction.");
    }
    public override void Write(Utf8JsonWriter writer, EaseMode value, JsonSerializerOptions options)
    {
        if (Enum.IsDefined(value)) writer.WriteStringValue(value.ToString());
        else writer.WriteNumberValue((int)value);
    }
}
