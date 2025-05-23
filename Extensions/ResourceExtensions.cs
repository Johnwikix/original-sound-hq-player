using Microsoft.Windows.ApplicationModel.Resources;

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
