using System.Text.Json;

namespace Lyricify.Lyrics.Serialization;

internal static class JsonElementExtensions
{
    internal static JsonElement Property(this JsonElement element, string name) => element.ValueKind switch
    {
        JsonValueKind.Undefined => default,
        JsonValueKind.Object => element.TryGetProperty(name, out var value) ? value : default,
        _ => throw new InvalidOperationException("Cannot access a property on a scalar JSON value."),
    };

    internal static JsonElement AsObject(this JsonElement element) => element.ValueKind == JsonValueKind.Object ? element : default;

    internal static string? ScalarText(this JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Undefined or JsonValueKind.Null => null,
        JsonValueKind.String => element.GetString(),
        JsonValueKind.True => bool.TrueString,
        JsonValueKind.False => bool.FalseString,
        JsonValueKind.Number => element.GetRawText(),
        _ => throw new InvalidCastException("Expected a scalar JSON value."),
    };
}
