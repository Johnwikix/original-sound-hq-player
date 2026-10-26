using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

/// <summary>Drives the isolated WinUI input window and verifies delivery without an external UI controller.</summary>
internal static partial class NativePointerDriver
{
    internal static async Task<string> RunAsync(Window window, Func<int> moves, Func<int> wheels, bool interactive)
    {
        nint root = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var targets = new List<nint>();
        EnumChildWindows(root, (child, _) => { targets.Add(child); return true; }, 0);
        targets.Add(root);
        foreach (nint target in targets)
        {
            int initial = moves();
            PostMessage(target, 0x0200, 0, Pack(100, 100));
            await Task.Delay(30);
            PostMessage(target, 0x0200, 0, Pack(120, 110));
            await Task.Delay(30);
            if (moves() == initial) continue;
            for (int i = 0; i < 80; i++)
            {
                PostMessage(target, 0x0200, 0, Pack(140 + i % 40 * 3, 120 + i % 30 * 2));
                await Task.Delay(4);
            }
            var position = new NativePoint { X = 200, Y = 200 };
            if (!ClientToScreen(target, ref position)) throw new InvalidOperationException("ClientToScreen failed.");
            for (int i = 0; i < 20; i++)
            {
                PostMessage(target, 0x020A, (nuint)(120 << 16), Pack(position.X, position.Y));
                await Task.Delay(10);
            }
            if (moves() > 5 && wheels() > 5) return "Win32 queued messages to the owned offscreen input window";
        }
        if (!interactive)
            throw new InvalidOperationException($"Offscreen input routing unavailable ({moves()} moves/{wheels()} wheels). Run with --interactive-input in a dedicated desktop session for SendInput.");
        return await SendInputAsync(root, moves, wheels);
    }

    private static async Task<string> SendInputAsync(nint root, Func<int> moves, Func<int> wheels)
    {
        if (!GetCursorPos(out var original)) throw new InvalidOperationException("GetCursorPos failed.");
        NativePoint last = original;
        try
        {
            ShowWindow(root, 5);
            SetForegroundWindow(root);
            await Task.Delay(100);
            GetClientRect(root, out var client);
            var origin = new NativePoint { X = client.Right / 3, Y = client.Bottom / 3 };
            ClientToScreen(root, ref origin);
            for (int i = 0; i < 80; i++)
            {
                if (GetAsyncKeyState(0x1B) < 0) throw new OperationCanceledException("Input probe cancelled with Escape.");
                if (!OwnsForeground()) throw new OperationCanceledException($"Input probe lost foreground ownership: target={root}, foreground={GetForegroundWindow()}.");
                last = new() { X = origin.X + i % 20 * 3, Y = origin.Y + i % 15 * 2 };
                Move(last);
                await Task.Delay(6);
            }
            for (int i = 0; i < 20; i++)
            {
                var input = new NativeInput { Mouse = new() { MouseData = i % 2 == 0 ? unchecked((uint)-120) : 120, Flags = 0x0800 } };
                Send(input);
                await Task.Delay(12);
            }
            if (moves() <= 5 || wheels() <= 5) throw new InvalidOperationException("SendInput did not deliver enough routed events.");
            return "Win32 SendInput in the owned foreground test window";
        }
        finally
        {
            if (GetCursorPos(out var current) && current.X == last.X && current.Y == last.Y) Move(original);
        }
    }

    private static bool OwnsForeground()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out uint processId);
        return processId == (uint)Environment.ProcessId;
    }

    private static void Move(NativePoint point)
    {
        int x = GetSystemMetrics(76), y = GetSystemMetrics(77);
        int width = GetSystemMetrics(78), height = GetSystemMetrics(79);
        Send(new() { Mouse = new() { Dx = (point.X - x) * 65535 / Math.Max(1, width - 1),
            Dy = (point.Y - y) * 65535 / Math.Max(1, height - 1), Flags = 0xE001 } });
    }
    private static unsafe void Send(NativeInput input)
    {
        if (SendInput(1, &input, sizeof(NativeInput)) != 1) throw new InvalidOperationException($"SendInput failed: {Marshal.GetLastPInvokeError()}");
    }
    private static nint Pack(int x, int y) => (nint)(uint)((x & 0xffff) | (y & 0xffff) << 16);

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { internal int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { internal int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeMouse { internal int Dx, Dy; internal uint MouseData, Flags, Time; internal nuint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeInput { internal uint Type; internal NativeMouse Mouse; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate bool EnumWindow(nint window, nint state);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumChildWindows(nint parent, EnumWindow callback, nint state);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessage(nint window, uint message, nuint wparam, nint lparam);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ClientToScreen(nint window, ref NativePoint point);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetClientRect(nint window, out NativeRect rectangle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", SetLastError = true)] private static extern unsafe uint SendInput(uint count, NativeInput* inputs, int size);
}
