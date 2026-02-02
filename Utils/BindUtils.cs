using System;
using System.Collections.Generic;
using System.Reflection.Metadata;
using System.Text;

namespace WinUIMusicPlayer.Utils
{
    public static class BindUtils
    {
        public static bool RadioButtonTagToBoolConverter(string themeType,string type) {
            if (themeType is null || type is null)
                return false;
            return themeType.Equals(type, StringComparison.OrdinalIgnoreCase);
        }
    }
}
