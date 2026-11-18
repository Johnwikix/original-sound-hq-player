using WinUIMusicPlayer.State;

namespace PlaybackHdrProbe
{
    internal static class BrightnessChecks
    {
        internal static void Run()
        {
            var preferences = new AppearancePreferencesState();
            void Check(double white, double peak)
            {
                if (preferences.PlaybackHdrReferenceWhiteNits != white || preferences.PlaybackHdrPeakNits != peak)
                    throw new Exception($"Unexpected reference white/peak {preferences.PlaybackHdrReferenceWhiteNits}/{preferences.PlaybackHdrPeakNits}");
            }
            preferences.PropertyChanged += (_, _) =>
            {
                if (preferences.PlaybackHdrReferenceWhiteNits > preferences.PlaybackHdrPeakNits)
                    throw new Exception("Brightness observers saw reference white above peak");
            };
            Check(300, 600);
            preferences.PlaybackHdrReferenceWhiteNits = 800;
            Check(800, 800);
            preferences.PlaybackHdrPeakNits = 120;
            Check(120, 120);
            preferences.PlaybackHdrPeakNits = 4400;
            Check(120, 4000);
            preferences.PlaybackHdrReferenceWhiteNits = 1200;
            Check(1000, 4000);
            preferences.PlaybackHdrPeakNits = double.NaN;
            Check(600, 600);
            preferences.PlaybackHdrReferenceWhiteNits = double.NaN;
            Check(300, 600);
            Console.WriteLine("PASS production HDR preference defaults, independent values, crossing, clamp and atomic notifications");
        }
    }
}

namespace WinUIMusicPlayer.Utils
{
    // The unrelated animated-text selection reads its localized label during state construction.
    internal static partial class ToolUtils
    {
        public static string GetString(string key) => key == "TextDefaultEffect" ? "Default" : throw new ArgumentException(key);
    }
}
