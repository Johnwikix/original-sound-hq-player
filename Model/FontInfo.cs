using Microsoft.UI.Xaml.Media;

namespace WinUIMusicPlayer.Model
{
    public class FontInfo
    {
        public static FontInfo CreateDefault() => new()
        {
            Name = "Segoe UI",
            DisplayName = "Segoe UI",
            FontFamily = new FontFamily("Segoe UI")
        };

        public string Name { get; set; } = null!;
        public string DisplayName { get; set; } = null!;
        public FontFamily FontFamily { get; set; } = null!;
    }
}
