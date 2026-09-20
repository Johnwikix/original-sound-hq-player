using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Lyricify.Lyrics.Serialization;

/// <summary>Shared, source-generated JSON contracts for the lyrics providers.</summary>
public static class LyricsJson
{
    private static readonly LyricsJsonContext Compact = CreateContext(false);
    private static readonly LyricsJsonContext Indented = CreateContext(true);

    private static LyricsJsonContext CreateContext(bool indented)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            IncludeFields = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            NumberHandling = JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.AllowNamedFloatingPointLiterals,
            Encoder = LyricsJsonEncoder.Instance,
            WriteIndented = indented,
            NewLine = Environment.NewLine,
        };
        options.Converters.Add(new ScalarStringConverter());
        options.Converters.Add(new BooleanConverter());
        options.Converters.Add(new DoubleConverter());
        options.Converters.Add(new SingleConverter());
        options.Converters.Add(new UntypedValueConverter());
        return new LyricsJsonContext(options);
    }

    public static T? Deserialize<T>(string json) => Deserialize(json, GetTypeInfo<T>(Compact));

    // Callers with their own DTOs supply generated metadata instead of requiring assembly roots.
    public static T? Deserialize<T>(string json, JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (string.IsNullOrWhiteSpace(json))
        {
            if (default(T) is not null) throw new JsonException("Empty JSON cannot be converted to a non-nullable value.");
            return default;
        }
        return JsonSerializer.Deserialize(json, typeInfo);
    }

    public static string Serialize<T>(T value, bool writeIndented = false)
    {
        var context = writeIndented ? Indented : Compact;
        var type = value?.GetType() ?? typeof(T);
        return JsonSerializer.Serialize(value, context.GetTypeInfo(type)
            ?? throw new NotSupportedException($"No generated JSON contract for {type}. Supply JsonTypeInfo<T> for custom types."));
    }

    public static string Serialize<T>(T value, JsonTypeInfo<T> typeInfo) => JsonSerializer.Serialize(value, typeInfo);

    private static JsonTypeInfo<T> GetTypeInfo<T>(LyricsJsonContext context) =>
        (JsonTypeInfo<T>)(context.GetTypeInfo(typeof(T))
            ?? throw new NotSupportedException($"No generated JSON contract for {typeof(T)}. Supply JsonTypeInfo<T> for custom types."));

    private sealed class ScalarStringConverter : JsonConverter<string>
    {
        public override string? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => GetNumberText(ref reader),
            JsonTokenType.True => bool.TrueString,
            JsonTokenType.False => bool.FalseString,
            _ => throw new JsonException("Expected a string or scalar value."),
        };

        private static string GetNumberText(ref Utf8JsonReader reader)
        {
            // Deserialize(string) supplies contiguous UTF-8 input. Handle sequences for custom callers too.
            if (!reader.HasValueSequence)
                return System.Text.Encoding.UTF8.GetString(reader.ValueSpan);
            using var document = JsonDocument.ParseValue(ref reader);
            return document.RootElement.GetRawText();
        }

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);

        public override string ReadAsPropertyName(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => reader.GetString()!;

        public override void WriteAsPropertyName(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WritePropertyName(value);
    }

    private sealed class BooleanConverter : JsonConverter<bool>
    {
        public override bool Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => reader.TokenType switch
        {
            JsonTokenType.True => true,
            JsonTokenType.False => false,
            JsonTokenType.String when bool.TryParse(reader.GetString(), out var value) => value,
            JsonTokenType.Number when reader.TryGetInt64(out var value) => value != 0,
            _ => throw new JsonException("Expected a boolean value."),
        };

        public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options) => writer.WriteBooleanValue(value);
    }

    private sealed class UntypedValueConverter : JsonConverter<object>
    {
        public override object? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => reader.TokenType switch
        {
            JsonTokenType.String => reader.TryGetDateTime(out var date) ? date : reader.GetString(),
            JsonTokenType.True => true,
            JsonTokenType.False => false,
            JsonTokenType.Number when reader.TryGetInt64(out var integer) => integer,
            JsonTokenType.Number => reader.GetDouble(),
            // JsonElement is the replacement for Json.NET's untyped object/array DOM.
            // Deserialize returns an independently owned element; it never outlives a disposed document.
            _ => JsonElement.ParseValue(ref reader),
        };

        public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options)
        {
            if (value.GetType() == typeof(object))
            {
                writer.WriteStartObject();
                writer.WriteEndObject();
                return;
            }
            JsonSerializer.Serialize(writer, value, options.GetTypeInfo(value.GetType()));
        }
    }

    private sealed class DoubleConverter : JsonConverter<double>
    {
        public override double Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.String ? double.Parse(reader.GetString()!, CultureInfo.InvariantCulture) : reader.GetDouble();

        public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
        {
            if (!double.IsFinite(value))
            {
                writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
                return;
            }
            Span<char> buffer = stackalloc char[32];
            value.TryFormat(buffer, out var count, "R", CultureInfo.InvariantCulture);
            WriteFloatingPoint(writer, buffer, count);
        }
    }

    private sealed class SingleConverter : JsonConverter<float>
    {
        public override float Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.String ? float.Parse(reader.GetString()!, CultureInfo.InvariantCulture) : reader.GetSingle();

        public override void Write(Utf8JsonWriter writer, float value, JsonSerializerOptions options)
        {
            if (!float.IsFinite(value))
            {
                writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
                return;
            }
            Span<char> buffer = stackalloc char[32];
            value.TryFormat(buffer, out var count, "R", CultureInfo.InvariantCulture);
            WriteFloatingPoint(writer, buffer, count);
        }
    }

    private static void WriteFloatingPoint(Utf8JsonWriter writer, Span<char> buffer, int count)
    {
        // Json.NET retains the fractional marker for integral floating-point values.
        if (!buffer[..count].ContainsAny('.', 'E', 'e'))
        {
            buffer[count++] = '.';
            buffer[count++] = '0';
        }
        writer.WriteRawValue(buffer[..count], skipInputValidation: true);
    }
}
