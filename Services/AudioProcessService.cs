using System;
using System.Diagnostics;
using System.IO;

namespace WinUIMusicPlayer.Services;

public sealed class AudioProcessService : IDisposable
{
    private Process? _process;
    public void Start()
    {
        if (_process is not null) return;
        string path = Path.Combine(AppContext.BaseDirectory, "AudioPlayer.exe");
        _process = Process.Start(new ProcessStartInfo(path)
        {
            WorkingDirectory = AppContext.BaseDirectory,
            CreateNoWindow = true,
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException("Unable to start audio player");
    }
    public void Dispose()
    {
        var process = _process;
        _process = null;
        if (process is null) return;
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        finally { process.Dispose(); }
    }
}
