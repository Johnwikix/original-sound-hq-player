using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;
using System.Reflection.Metadata;
using System.Text;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.ViewModel;
using ZLinq;
using static WinUIMusicPlayer.Utils.ToolUtils;

namespace WinUIMusicPlayer.Utils
{
    public static class BindUtils
    {
        public static bool PlayModeCheckerConverter(PlayMode currentPlayMode, string targetPlayMode) {
            if (currentPlayMode.ToString() is null || targetPlayMode is null)
                return false;
            return currentPlayMode.ToString().Equals(targetPlayMode, StringComparison.OrdinalIgnoreCase);
        }
        public static bool RadioButtonTagToBoolConverter(string themeType,string type) {
            if (themeType is null || type is null)
                return false;
            return themeType.Equals(type, StringComparison.OrdinalIgnoreCase);
        }

        public static string AlbumSongsConverter(string album) {
            if (string.IsNullOrEmpty(album)) {
                return "0";
            }
            return App.Services.GetRequiredService<AppViewModel>().AllSongs.AsValueEnumerable().Where(m => m.Album == album).Count().ToString();
        }

        public static double BoolToOpacityReConverter(bool isInPlayingDetailMode) {
            return isInPlayingDetailMode ? 0 : 1;
        }

        public static double BoolToOpacityMultParamsConverter(Visibility visibility,bool isInNaviView)
        {
            if (visibility is not Visibility.Visible) return 1.0;
            return isInNaviView ? 1.0 : 0.0;
        }

        public static double VisibilityToOpacityReConverter(Visibility visibility) {
            return visibility is Visibility.Visible ? 0 : 1.0;
        }
    }
}
