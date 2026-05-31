using Microsoft.Extensions.DependencyInjection;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.ViewModel;
using ZLinq;
using static WinUIMusicPlayer.Utils.ToolUtils;

namespace WinUIMusicPlayer.Utils
{
    public static class BindUtils
    {
        public static bool PlayModeCheckerConverter(PlayMode currentPlayMode, string targetPlayMode)
        {
            if (currentPlayMode.ToString() is null || targetPlayMode is null)
                return false;
            return currentPlayMode.ToString().Equals(targetPlayMode, StringComparison.OrdinalIgnoreCase);
        }
        public static bool RadioButtonTagToBoolConverter(string themeType, string type)
        {
            if (themeType is null || type is null)
                return false;
            return themeType.Equals(type, StringComparison.OrdinalIgnoreCase);
        }

        public static string AlbumSongsConverter(string album)
        {
            if (string.IsNullOrEmpty(album))
            {
                return "0";
            }
            return App.Services.GetRequiredService<AppViewModel>().SongsSource.AsValueEnumerable().Where(m => m.Album == album).Count().ToString();
        }
        public static double BoolToOpacityRe08Converter(bool isInPlayingDetailMode)
        {
            return isInPlayingDetailMode ? 0 : 0.8;
        }

        public static double BoolToOpacityReConverter(bool isInPlayingDetailMode)
        {
            return isInPlayingDetailMode ? 0 : 1;
        }

        //public static double BoolToOpacityMultParamsConverter(Visibility visibility, bool isInNaviView)
        //{
        //    if (visibility is not Visibility.Visible) return 1.0;
        //    return isInNaviView ? 1.0 : 0.0;
        //}

        public static double VisibilityToOpacityReConverter(Visibility visibility)
        {
            return visibility is Visibility.Visible ? 0 : 1.0;
        }

        public static Visibility VisiblilityToVisibilityConverter(Visibility visibility)
        {
            return visibility is Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        }

        public static Visibility ImageSourceToVisibilityConverter(ImageSource source)
        {
            return source is null ? Visibility.Visible : Visibility.Collapsed;
        }

        public static double LyricsFontSizeConverter(double fontSize, double globalFontSize, bool isGlobalFontSizeEnable)
        {
            if (isGlobalFontSizeEnable)
            {
                return globalFontSize;
            }
            return fontSize;
        }

        public static CanvasHorizontalAlignment ConvertStringToTextAlignment(string alignment)
        {
            return Enum.TryParse(alignment, true, out CanvasHorizontalAlignment result) ? result : CanvasHorizontalAlignment.Left;
        }

        public static Visibility BoolToVisibilityReConverter(bool isVisible)
        {
            return isVisible ? Visibility.Collapsed : Visibility.Visible;
        }

        public static bool BoolToBoolReConverter(bool value)
        {
            return !value;
        }

        public static double HalfConverter(double value)
        {
            return value / 2.0;
        }

        public static double ValueAmplifierConverter(double value, double scale = 1.0)
        {
            return value * scale;
        }

        public static string MusicToInfo(Music music)
        {
            if (music is null) return string.Empty;
            return $"{music.Album}{Environment.NewLine}{music.Author}";
        }

        public static double PercentToDouble(double percent) => percent / 100.0;

        public static string FormatF1(double value) => $"{value:F1}";
        public static string FormatF1(float value) => $"{value:F1}";
        public static string FormatF0(double value) => $"{value:F0}";
        public static string FormatMs(double value) => $"{value:F0} ms";
        public static string FormatPercent(double value) => $"{value:F0}%";
    }
}
