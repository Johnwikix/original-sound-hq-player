using ManagedBass;
using ManagedBass.Fx;
using System;
using System.Diagnostics;
using System.IO;

namespace WinUIMusicPlayer.Manager
{
    public static class BassManager
    {
        private static bool _isInitialized = false;

        public static void Initialize()
        {
            if (_isInitialized) return;

            if (!Bass.Init())
            {
                Debug.WriteLine($"Bass初始化失败: {Bass.LastError}");
                return;
            }
            _isInitialized = true;
            LoadBassPlugins();
        }

        private static void LoadBassPlugins()
        {
            var appPath = AppContext.BaseDirectory;
            var pluginPaths = new[]
            {
                "bassape.dll",
                "basscd.dll",
                "bassdsd.dll",
                "bassflac.dll",
                "basshls.dll",
                "bassmidi.dll",
                "bassopus.dll",
                "basswebm.dll",
                "basswv.dll",
                "bassalac.dll"
            };
            var version = BassFx.Version;
            Debug.WriteLine($"BassFx: {version}");
            foreach (var pluginPath in pluginPaths)
            {
                var fullPath = Path.Combine(appPath, pluginPath);
                if (!File.Exists(fullPath))
                {
                    Debug.WriteLine($"插件文件不存在: {fullPath}");
                    continue;
                }

                var pluginHandle = Bass.PluginLoad(fullPath);
                if (pluginHandle != 0)
                {
                    Debug.WriteLine($"成功加载插件: {pluginPath}，句柄: {pluginHandle}");
                }
                else
                {
                    Debug.WriteLine($"加载插件失败: {pluginPath}，错误: {Bass.LastError}");
                }
                var plugins = Bass.PluginGetInfo(pluginHandle);

                foreach (var plugin in plugins.Formats)
                {
                    Debug.WriteLine($"  支持格式: {plugin.Name} ({plugin.FileExtensions})");
                }
            }

        }

        public static void Free()
        {
            if (!_isInitialized) return;
            Bass.Free();
            _isInitialized = false;
        }
    }
}
