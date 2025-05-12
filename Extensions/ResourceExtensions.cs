using Microsoft.Windows.ApplicationModel.Resources;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WinUIMusicPlayer.Extensions
{
    public static class ResourceExtensions
    {
        private static ResourceLoader resourceLoader = new ResourceLoader();

        public static string GetLocalized(string resourceKey)
        {
            return resourceLoader.GetString(resourceKey);
        }
    }
}
