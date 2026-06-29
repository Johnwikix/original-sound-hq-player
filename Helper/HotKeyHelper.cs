using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Windows.System;
using WinRT.Interop;

namespace WinUIMusicPlayer.Helper
{
    public enum ShortcutId
    {
        PlayOrPauseSong,
        NextSong,
        PreviousSong,
        VolumeUp,
        VolumeDown,
        TogglePlayingDetail,
        Back,
        ShowWindow,
        ToggleFullScreen,
    }

    public static class GlobalHotKeyHook
    {
        private static readonly Dictionary<int, Action> _actions = [];
        private static readonly Dictionary<int, List<string>> _keys = [];

        private static void RegisterHotKey(Window window, ShortcutId id, List<string> keys, Action action)
        {
            if (keys.Count == 0) return;

            IntPtr hwnd = WindowNative.GetWindowHandle(window);
            HotKeyModifiers modifiers = HotKeyModifiers.MOD_NONE;
            VirtualKey key = VirtualKey.None;
            foreach (var item in keys)
            {
                switch (item)
                {
                    case "Ctrl":
                        modifiers |= HotKeyModifiers.MOD_CONTROL;
                        break;
                    case "Shift":
                        modifiers |= HotKeyModifiers.MOD_SHIFT;
                        break;
                    case "Alt":
                        modifiers |= HotKeyModifiers.MOD_ALT;
                        break;
                    case "Win":
                        modifiers |= HotKeyModifiers.MOD_WIN;
                        break;
                    default:
                        key = (VirtualKey)Enum.Parse(typeof(VirtualKey), item, true);
                        break;
                }
            }
            bool success = NativeMethods.RegisterHotKey(hwnd, (int)id, (uint)modifiers, (uint)key);
            if (success)
            {
                _actions[(int)id] = action;
                _keys[(int)id] = keys;
            }
        }

        private static void UnregisterHotKey(Window window, ShortcutId id)
        {
            IntPtr hwnd = WindowNative.GetWindowHandle(window);
            NativeMethods.UnregisterHotKey(hwnd, (int)id);
            _actions.Remove((int)id);
            _keys.Remove((int)id);
        }

        public static void UpdateHotKey(Window window, ShortcutId id, List<string> keys, Action action)
        {
            UnregisterHotKey(window, id);
            RegisterHotKey(window, id, keys, action);
        }

        public static bool IsHotKeyRegistered(ShortcutId id)
        {
            return _actions.ContainsKey((int)id);
        }

        public static bool IsHotKeyRegistered(List<string> keys)
        {
            return _keys.ContainsValue(keys);
        }

        public static bool TryInvokeAction(int id)
        {
            if (_actions.TryGetValue(id, out var action))
            {
                action?.Invoke();
                return true;
            }
            return false;
        }
    }

    [Flags]
    internal enum HotKeyModifiers : uint
    {
        MOD_NONE = 0x0000,
        MOD_ALT = 0x0001,
        MOD_CONTROL = 0x0002,
        MOD_SHIFT = 0x0004,
        MOD_WIN = 0x0008,
    }

    internal static partial class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    }
}
