using System.Text.Json;
using System.Text.Json.Serialization;

// UI-only dependencies are substituted; settings models, serialization and stores are production code.
namespace AnimatedWin2dControls.Controls.AnimatedTextBlock.Enums
{
    public enum AnimatedTextEffect { TextDefaultEffect }
}
namespace AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.Advance
{
    public enum EasingType { FlowWave }
    public enum EaseMode { FlowWave }
}
namespace Microsoft.Graphics.Canvas.Text
{
    public enum CanvasHorizontalAlignment { Left }
}
namespace Microsoft.UI.Xaml
{
    public enum TextAlignment { Left }
}
namespace WinUIMusicPlayer.Utils
{
    using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.Advance;
    public static class ToolUtils { public static string GetString(string key) => key; }
    public class LyricsEasingTypeJsonConverter : JsonConverter<EasingType>
    {
        public override EasingType Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => EasingType.FlowWave;
        public override void Write(Utf8JsonWriter writer, EasingType value, JsonSerializerOptions options) => writer.WriteNumberValue(0);
    }
    public class LyricsEaseModeJsonConverter : JsonConverter<EaseMode>
    {
        public override EaseMode Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => EaseMode.FlowWave;
        public override void Write(Utf8JsonWriter writer, EaseMode value, JsonSerializerOptions options) => writer.WriteNumberValue(0);
    }
}
