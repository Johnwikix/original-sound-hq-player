using System;
using System.Collections.Generic;
using System.Reflection.Metadata;
using System.Text;
using WinUIMusicPlayer.Model;
using ZLinq;

namespace WinUIMusicPlayer.Utils
{
    public static class BindUtils
    {
        public static bool RadioButtonTagToBoolConverter(string themeType,string type) {
            if (themeType is null || type is null)
                return false;
            return themeType.Equals(type, StringComparison.OrdinalIgnoreCase);
        }

        public static string AlbumSongsConverter(string album) {
            if (string.IsNullOrEmpty(album)) {
                return "0";
            }
            return AppData.allSongs.AsValueEnumerable().Where(m => m.Album == album).Count().ToString();
        }
    }
}
