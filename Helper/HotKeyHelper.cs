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
        private static readonly HashSet<ShortcutId> _conflicts = [];

        public static event EventHandler? ConflictsChanged;

        public static IReadOnlyCollection<ShortcutId> Conflicts => _conflicts;

        public static void UpdateHotKey(Window window, ShortcutId id, List<string> keys, Action action)
        {
            if (window is null) return;
            UnregisterHotKey(window, id);
            RegisterHotKey(window, id, keys, action);
        }

        public static void ClearAll(Window window)
        {
            if (window is null) return;
            IntPtr hwnd = WindowNative.GetWindowHandle(window);
            foreach (var id in _actions.Keys)
            {
                NativeMethods.UnregisterHotKey(hwnd, id);
            }
            _actions.Clear();
            _keys.Clear();
            if (_conflicts.Count > 0)
            {
                _conflicts.Clear();
                ConflictsChanged?.Invoke(null, EventArgs.Empty);
            }
        }

        private static void RegisterHotKey(Window window, ShortcutId id, List<string> keys, Action action)
        {
            if (keys.Count == 0) return;

            IntPtr hwnd = WindowNative.GetWindowHandle(window);
            var (modifiers, key) = ParseModifiersAndKey(keys);
            if (key == VirtualKey.None) return;

            if (NativeMethods.RegisterHotKey(hwnd, (int)id, (uint)modifiers, (uint)key))
            {
                _actions[(int)id] = action;
                _keys[(int)id] = keys;
                if (_conflicts.Remove(id))
                {
                    ConflictsChanged?.Invoke(null, EventArgs.Empty);
                }
            }
            else if (_conflicts.Add(id))
            {
                ConflictsChanged?.Invoke(null, EventArgs.Empty);
            }
        }

        private static void UnregisterHotKey(Window window, ShortcutId id)
        {
            if (window is not null)
            {
                IntPtr hwnd = WindowNative.GetWindowHandle(window);
                NativeMethods.UnregisterHotKey(hwnd, (int)id);
            }
            _actions.Remove((int)id);
            _keys.Remove((int)id);
            if (_conflicts.Remove(id))
            {
                ConflictsChanged?.Invoke(null, EventArgs.Empty);
            }
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

        private static (HotKeyModifiers modifiers, VirtualKey key) ParseModifiersAndKey(List<string> keys)
        {
            HotKeyModifiers modifiers = HotKeyModifiers.MOD_NONE;
            VirtualKey key = VirtualKey.None;
            foreach (var item in keys)
            {
                switch (item)
                {
                    case "Ctrl": modifiers |= HotKeyModifiers.MOD_CONTROL; break;
                    case "Shift": modifiers |= HotKeyModifiers.MOD_SHIFT; break;
                    case "Alt": modifiers |= HotKeyModifiers.MOD_ALT; break;
                    case "Win": modifiers |= HotKeyModifiers.MOD_WIN; break;
                    default:
                        if (Enum.TryParse<VirtualKey>(item, true, out var v) && v != VirtualKey.None)
                        {
                            key = v;
                        }
                        break;
                }
            }
            return (modifiers, key);
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
