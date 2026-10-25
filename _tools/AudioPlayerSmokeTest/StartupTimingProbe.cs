using System.Diagnostics;
using BassPlayerIpc.Shared;

internal static class StartupTimingProbe
{
    // A fresh process, real pipes, real decoding and real output for every sample.
    internal static int Run(string[] args)
    {
        if (args.Length < 2) throw new ArgumentException("Expected player and media paths");
        string player = Path.GetFullPath(args[0]);
        string media = Path.GetFullPath(args[1]);
        int Option(string name, int fallback)
        {
            string? option = args.FirstOrDefault(a => a.StartsWith(name, StringComparison.Ordinal));
            return int.TryParse(option?[name.Length..], out int value) ? value : fallback;
        }
        int idleMs = Option("--idle-ms=", 0);
        int limitMs = Option("--max-play-ms=", 1000);
        Environment.SetEnvironmentVariable("ORIGINALSOUND_IPC_SCOPE", "startup-" + Guid.NewGuid().ToString("N"));
        using var alive = new Mutex(true, IpcConstants.ClientAliveMutexName);
        var clock = Stopwatch.StartNew();
        using var server = Process.Start(new ProcessStartInfo(player)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(player)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException("Player did not start");
        void LogOutput(object sender, DataReceivedEventArgs e)
        {
            if (e.Data is not null) Console.WriteLine($"[player +{clock.Elapsed.TotalMilliseconds:F0}ms] {e.Data}");
        }
        server.OutputDataReceived += LogOutput;
        server.ErrorDataReceived += LogOutput;
        server.BeginOutputReadLine();
        server.BeginErrorReadLine();
        PipeCommandClient? commands = null;
        PipeStateClient? states = null;
        try
        {
            using var timeout = new CancellationTokenSource(20000);
            commands = PipeCommandClient.ConnectAsync(IpcConstants.ControlPipeName, timeout.Token).GetAwaiter().GetResult();
            states = PipeStateClient.ConnectAsync(IpcConstants.StatePipeName, timeout.Token).GetAwaiter().GetResult();
            if (commands.InstanceId != states.InstanceId) throw new InvalidOperationException("Player instance mismatch");
            states.Start();
            if (!SpinWait.SpinUntil(() => states.CurrentDspState is not null, 5000)) throw new InvalidOperationException("Initial DSP state missing");
            double connectedMs = clock.Elapsed.TotalMilliseconds;
            byte[] response = new byte[IpcConstants.MaxResponseSize];
            void Send(CommandId command, ReadOnlySpan<byte> payload)
            {
                var result = commands.RequestAsync(command, payload, response, timeoutMs: 20000).GetAwaiter().GetResult();
                bool playing = command == CommandId.PlayButton && result.Type == MessageTypeId.PlayState
                    && result.Length == BinarySerializer.PlayStateResponseSize && response[0] != 0;
                if (result.Type != MessageTypeId.Success && !playing)
                    throw new InvalidOperationException($"{command} returned {result.Type}");
            }
            byte[] settings = new byte[1024];
            int length = BinarySerializer.WriteIpcSetting(settings, new IpcSetting { OutputMode = "WasapiShared", BassOutputDeviceId = -1, Latency = 300, Volume = 0.02f });
            Send(CommandId.UpdateSettings, settings.AsSpan(0, length));
            bool button = args.Contains("--play-button");
            byte[] request = new byte[BinarySerializer.PlayRequestSize];
            BinarySerializer.WritePlayRequest(request, new PlayRequest { Url = media });
            if (button)
            {
                byte[] url = new byte[BinarySerializer.SetMusicUrlRequestSize];
                BinarySerializer.WriteSetMusicUrlRequest(url, new SetMusicUrlRequest { Url = media });
                Send(CommandId.SetMusicUrl, url);
            }
            if (idleMs > 0) Thread.Sleep(idleMs);
            long requestedAt = Stopwatch.GetTimestamp();
            Send(button ? CommandId.PlayButton : CommandId.Play, button ? ReadOnlySpan<byte>.Empty : request);
            double confirmedMs = Stopwatch.GetElapsedTime(requestedAt).TotalMilliseconds;
            if (!SpinWait.SpinUntil(() => states.TryGetProgress(out var p) && p.Playing && p.CurrentMs > 0, 5000))
                throw new InvalidOperationException("Playback did not advance");
            double advancingMs = Stopwatch.GetElapsedTime(requestedAt).TotalMilliseconds;
            Console.WriteLine($"[startup] connected={connectedMs:F1}ms idle={idleMs}ms command={(button ? "PlayButton" : "Play")} confirmed={confirmedMs:F1}ms advancing={advancingMs:F1}ms limit={limitMs}ms");
            if (advancingMs > limitMs) throw new InvalidOperationException($"First playback exceeded {limitMs}ms: {advancingMs:F1}ms");
            Send(CommandId.MusicEnd, ReadOnlySpan<byte>.Empty);
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[startup] FAIL: {ex.Message}");
            return 1;
        }
        finally
        {
            commands?.Dispose();
            states?.Dispose();
            if (!server.WaitForExit(10000)) server.Kill();
            server.WaitForExit();
            alive.ReleaseMutex();
        }
    }
}
