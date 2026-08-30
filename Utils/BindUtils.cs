using Microsoft.Extensions.DependencyInjection;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.ViewModel;
using ZLinq;
using static WinUIMusicPlayer.Utils.ToolUtils;

namespace WinUIMusicPlayer.Utils
{
    public static class BindUtils
    {
        public static Visibility GetHrMusicVisibility(int sampleRate, int bitDepth)
            => (sampleRate >= 48000 && bitDepth >= 24) || (sampleRate >= 2822400 && bitDepth == 1)
                ? Visibility.Visible
                : Visibility.Collapsed;

        public static double GetHrMusicFontSize(double baseFontSize)
            => baseFontSize * 0.7;

        public static bool PlayModeCheckerConverter(PlayMode currentPlayMode, string targetPlayMode)
        {
            if (currentPlayMode.ToString() is null || targetPlayMode is null)
                return false;
            return currentPlayMode.ToString().Equals(targetPlayMode, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>播放模式 → 本地化标题文本（随 CurrentPlayMode 函数绑定，替代曾经的 PlayModeFlyoutText 属性）。</summary>
        public static string PlayModeToTextConverter(PlayMode currentPlayMode)
            => GetPlayModeText(currentPlayMode);
        public static bool RadioButtonTagToBoolConverter(string themeType, string type)
        {
            if (themeType is null || type is null)
                return false;
            return themeType.Equals(type, StringComparison.OrdinalIgnoreCase);
        }

        public static string AlbumSongsConverter(string album)
        {
            if (string.IsNullOrEmpty(album)) return "0";
            return App.Services.GetRequiredService<AppViewModel>().GetAlbumSongCount(album).ToString();
        }

        public static Music? PlayListCoverMusicConverter(int playListId)
        {
            var items = App.Services.GetRequiredService<MusicDatabaseService>().GetMusicByPlayListIdFromMem(playListId);
            foreach (var item in items)
            {
                if (item.Music is not null) return item.Music;
            }
            return null;
        }
        public static double BoolToOpacityRe08Converter(bool isInPlayingDetailMode)
        {
            return isInPlayingDetailMode ? 0 : 0.8;
        }

        /// <summary>桌面歌词锁定状态 → 锁定按钮字形，反映当前状态（Segoe Fluent Icons：e72e=Lock e785=Unlock）。</summary>
        public static string LockGlyphConverter(bool locked)
            => locked ? "\uE785" : "\uE72E";

        /// <summary>桌面歌词开关状态 → 托盘菜单字形，随状态切换（Segoe Fluent Icons：e890=View ed1a=Hide）。</summary>
        public static string DesktopLyricsGlyphConverter(bool enabled)
            => enabled ? "\uED1A" : "\uE890";

        /// <summary>桌面歌词开关状态 → 播放条按钮透明度（启用 1.0 / 停用 0.4）。</summary>
        public static double DesktopLyricsButtonOpacity(bool enabled)
            => enabled ? 1.0 : 0.4;

        public static double BoolToOpacityReConverter(bool isInPlayingDetailMode)
        {
            return isInPlayingDetailMode ? 0 : 1;
        }

        public static double VisibilityToOpacityConverter(Visibility visibility)
        {
            return visibility is Visibility.Visible ? 1.0 : 0;
        }

        public static bool OpacityToBoolConverter(double opacity)
        {
            return opacity > 0;
        }

        public static double VisibilityToOpacityReConverter(Visibility visibility)
        {
            return visibility is Visibility.Visible ? 0 : 1.0;
        }

        public static bool IsCurrentMusicExist(Music current) {
            if(current is null) return false;
            return true;
        }

        public static bool IsCurrentPlayListExist(IEnumerable<Music> playList)
        {
            if (playList is null || !playList.Any()) return false;
            return true;
        }

        public static Visibility VisiblilityToVisibilityConverter(Visibility visibility)
        {
            return visibility is Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        }

        public static double BoolToOpacityConverter(bool value)
            => value ? 1.0 : 0.0;

        public static Visibility BoolAndVisibilityToVisibilityConverter(bool isTrue, Visibility visibility)
            => isTrue && visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;

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

        public static bool IsLyricsControlEnabled(bool isFluidBackgroundEnabled, bool isAdvancedLyricsEnabled)
        {
            return !isFluidBackgroundEnabled || !isAdvancedLyricsEnabled;
        }

        public static bool IsFluidShaderSelected(int backgroundShaderIndex)
        {
            return backgroundShaderIndex == 0;
        }

        public static bool IsLightWaveEnabled(bool isFluidBackgroundEnabled, int backgroundShaderIndex)
        {
            return isFluidBackgroundEnabled && backgroundShaderIndex == 0;
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
            var album = music.Album;
            var author = music.Author;
            var newLine = Environment.NewLine;
            return string.Create(album.Length + newLine.Length + author.Length, (album, author, newLine), static (span, state) =>
            {
                state.album.CopyTo(span);
                state.newLine.CopyTo(span.Slice(state.album.Length));
                state.author.CopyTo(span.Slice(state.album.Length + state.newLine.Length));
            });
        }

        public static bool BothTrue(bool a, bool b)
        {
            return a && b;
        }

        public static double PercentToDouble(double percent) => percent / 100.0;

        public static string FormatF1(double value)
        {
            Span<char> buf = stackalloc char[32];
            value.TryFormat(buf, out var written, "F1", CultureInfo.InvariantCulture);
            return new string(buf[..written]);
        }
        public static string FormatF1(float value)
        {
            Span<char> buf = stackalloc char[32];
            value.TryFormat(buf, out var written, "F1", CultureInfo.InvariantCulture);
            return new string(buf[..written]);
        }
        public static string FormatF0(double value)
        {
            Span<char> buf = stackalloc char[32];
            value.TryFormat(buf, out var written, "F0", CultureInfo.InvariantCulture);
            return new string(buf[..written]);
        }
        public static string FormatMs(double value)
        {
            Span<char> buf = stackalloc char[32];
            value.TryFormat(buf, out var written, "F0", CultureInfo.InvariantCulture);
            " ms".CopyTo(buf.Slice(written));
            return new string(buf[..(written + 3)]);
        }
        public static string FormatPercent(double value)
        {
            Span<char> buf = stackalloc char[32];
            value.TryFormat(buf, out var written, "F0", CultureInfo.InvariantCulture);
            buf[written] = '%';
            return new string(buf[..(written + 1)]);
        }

        public static string FormatThumbTipTime(double totalSeconds)
        {
            var timeSpan = TimeSpan.FromSeconds(totalSeconds);
            return timeSpan.TotalHours >= 1
                ? string.Create(8, timeSpan, static (span, ts) => WriteThumbWithHours(span, ts))
                : string.Create(5, timeSpan, static (span, ts) => WriteThumbNoHours(span, ts));
        }

        private static void WriteThumbWithHours(Span<char> span, TimeSpan ts)
        {
            ((int)ts.TotalHours).TryFormat(span.Slice(0, 2), out _, "D2", CultureInfo.InvariantCulture);
            span[2] = ':';
            ts.Minutes.TryFormat(span.Slice(3, 2), out _, "D2", CultureInfo.InvariantCulture);
            span[5] = ':';
            ts.Seconds.TryFormat(span.Slice(6, 2), out _, "D2", CultureInfo.InvariantCulture);
        }

        private static void WriteThumbNoHours(Span<char> span, TimeSpan ts)
        {
            ts.Minutes.TryFormat(span.Slice(0, 2), out _, "D2", CultureInfo.InvariantCulture);
            span[2] = ':';
            ts.Seconds.TryFormat(span.Slice(3, 2), out _, "D2", CultureInfo.InvariantCulture);
        }
    }
}
