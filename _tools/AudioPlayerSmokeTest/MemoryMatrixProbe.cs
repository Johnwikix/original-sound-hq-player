using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using BassPlayerIpc.Shared;

/// <summary>对隔离 AudioPlayer 进程执行有界、静音的内存实验；不写入播放器偏好。</summary>
internal static class MemoryMatrixProbe
{
    internal static int Run(string[] args, Process server, PipeCommandClient commands, PipeStateClient states,
        string path, string mode, int seconds)
    {
        string Option(string prefix, string fallback) => args.FirstOrDefault(x => x.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..] ?? fallback;
        string scenario = Option("--scenario=", "baseline");
        string output = Path.GetFullPath(Option("--result=", "artifacts/memory-matrix.json"));
        string impulse = Path.GetFullPath(Option("--ir=", "artifacts/audioplayer-dsp-memory-review/long-mono-ir.wav"));
        string nextPath = Path.GetFullPath(Option("--next=", path));
        byte[] response = new byte[IpcConstants.MaxResponseSize];
        var samples = new List<object>();
        var clock = Stopwatch.StartNew();
        int transitions = 0;
        long token = 100, queuedEpoch = -1, lastEpoch = -1, previousPosition = -1;
        bool idle = scenario == "idle";
        bool convolution = scenario is "ir" or "ir-native" or "ir-switch" or "toggle" or "gapless";
        var settings = new DspSettings
        {
            IsEnabled = scenario != "off", AutoPreamp = args.Contains("--auto-preamp"),
            TruePeakLimiterEnabled = scenario is "limiter" or "toggle" or "gapless",
            ResampleMode = convolution && scenario != "ir-native" || scenario == "resample" ? PcmResampleMode.Fixed : PcmResampleMode.Source,
            ResampleTargetRate = scenario == "resample" ? 192000 : 96000,
            ConvolutionEnabled = convolution, ConvolutionSource = ConvolutionSource.Wave, ImpulsePath = convolution ? impulse : "",
            NormalizeLoudness = scenario == "normalize"
        };
        MessageTypeId Send(CommandId command, ReadOnlySpan<byte> payload)
            => commands.RequestAsync(command, payload, response, timeoutMs: 10000).GetAwaiter().GetResult().Type;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
        void Configure(DspSettings value)
        {
            byte[] payload = new byte[DspProtocol.SettingsSize];
            DspProtocol.WriteSettings(payload, value);
            Check(Send(CommandId.UpdateDsp, payload) == MessageTypeId.Success, "DSP update rejected");
        }
        void Play(string location)
        {
            byte[] payload = new byte[BinarySerializer.PlayRequestSize];
            BinarySerializer.WritePlayRequest(payload, new() { Url = location });
            Check(Send(CommandId.Play, payload) == MessageTypeId.Success, "Play rejected");
        }
        void QueueNext()
        {
            Check(states.TryGetProgress(out var progress), "Progress unavailable for preload");
            if (queuedEpoch == progress.Epoch) return;
            Check(Send(CommandId.QueueNext, new GaplessRequest(progress.Epoch, ++token, nextPath).Write()) == MessageTypeId.Success,
                "Gapless plan rejected");
            queuedEpoch = progress.Epoch;
        }
        void Sample(string phase)
        {
            server.Refresh();
            states.TryGetProgress(out var progress);
            var dsp = states.CurrentDspState?.State;
            samples.Add(new { Phase = phase, Seconds = clock.Elapsed.TotalSeconds, progress.Epoch, progress.CurrentMs, progress.Playing,
                progress.GaplessToken, SampleRate = dsp?.SampleRate, Output = dsp?.ActualOutput.ToString(),
                Convolution = dsp?.Convolution.ToString(), Loudness = dsp?.Loudness.ToString(),
                PrivateBytes = server.PrivateMemorySize64, WorkingSet = server.WorkingSet64,
                Handles = server.HandleCount, Threads = server.Threads.Count, CpuMs = server.TotalProcessorTime.TotalMilliseconds });
        }
        void Phase(string name)
        {
            Console.WriteLine($"[matrix-phase] {name},{clock.Elapsed.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture)}");
        }
        void Write(string status, Exception? error = null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.WriteAllText(output, JsonSerializer.Serialize(new { Status = status, Scenario = scenario, Mode = mode, Seconds = seconds,
                Pid = server.Id, Transitions = transitions, Settings = settings, Error = error?.ToString(), Samples = samples },
                new JsonSerializerOptions { WriteIndented = true }));
        }
        try
        {
            byte[] initial = new byte[1024];
            int length = BinarySerializer.WriteIpcSetting(initial, new() { OutputMode = mode, BassOutputDeviceId = -1,
                BassASIODeviceId = 0, Latency = 300, Volume = 0, IsDopEnabled = false });
            Check(Send(CommandId.UpdateSettings, initial.AsSpan(0, length)) == MessageTypeId.Success, "Output setup rejected");
            Configure(settings);
            if (!idle)
            {
                Play(path);
                Check(SpinWait.SpinUntil(() => states.TryGetProgress(out var p) && p.Playing && p.CurrentMs > 100, 10000),
                    "Actual output did not begin rendering");
                if (mode == "ASIO")
                    Check(states.CurrentDspState?.State.ActualOutput.ToString() == "Asio", "ASIO initialization failed; actual output is a fallback");
                if (convolution)
                    Check(SpinWait.SpinUntil(() => states.CurrentDspState?.State.Convolution == ConvolutionStatus.Active, 10000),
                        "IR preparation did not complete");
            }
            if (scenario == "gapless") QueueNext();
            Phase("warmup");
            Thread.Sleep(5000);
            Phase("measuring");
            Sample("playing");
            for (int second = 0; second < seconds; second++)
            {
                if (scenario is "trackswitch" or "ir-switch")
                {
                    states.TryGetProgress(out var before);
                    Play(second % 2 == 0 ? nextPath : path);
                    Check(SpinWait.SpinUntil(() => states.TryGetProgress(out var p) && p.Playing && p.Epoch != before.Epoch && p.CurrentMs > 100, 5000),
                        "Switch did not produce an advancing new session");
                    transitions++;
                    lastEpoch = -1;
                }
                if (scenario == "toggle" && second % 3 == 0)
                    Configure(settings with { IsEnabled = second % 6 != 0 });
                Thread.Sleep(1000);
                if (!idle)
                {
                    Check(states.TryGetProgress(out var progress) && progress.Playing && progress.CurrentMs > 0, "Playback stopped during measurement");
                    if (progress.Epoch == lastEpoch) Check(progress.CurrentMs > previousPosition, "Playback did not advance");
                    else if (scenario == "gapless" && lastEpoch != -1) transitions++;
                    lastEpoch = progress.Epoch;
                    previousPosition = progress.CurrentMs;
                    if (scenario == "gapless") QueueNext();
                }
                Sample("playing");
            }
            if (scenario == "gapless") Check(transitions >= 2, "Insufficient real gapless transitions");
            Phase("stopped");
            Check(Send(CommandId.MusicEnd, []) == MessageTypeId.Success, "Stop rejected");
            Thread.Sleep(3000);
            Sample("stopped");
            if (args.Contains("--collect-after-stop"))
            {
                Phase("explicit-gc");
                using var collect = EventWaitHandle.OpenExisting("Local\\AudioPlayerMemory-" + Environment.GetEnvironmentVariable("ORIGINALSOUND_IPC_SCOPE"));
                collect.Set();
                Thread.Sleep(3000);
                Sample("after-explicit-gc");
            }
            // 当前会话可能为重播而保留；单独旁路其 DSP，再回收一次，区分工作区和环缓冲。
            Phase("stopped-dsp-off");
            Configure(settings with { IsEnabled = false });
            Thread.Sleep(1000);
            Sample("stopped-dsp-off");
            if (args.Contains("--collect-after-stop"))
            {
                using var collect = EventWaitHandle.OpenExisting("Local\\AudioPlayerMemory-" + Environment.GetEnvironmentVariable("ORIGINALSOUND_IPC_SCOPE"));
                collect.Set();
                Thread.Sleep(3000);
                Sample("after-dsp-off-gc");
            }
            Write("PASS");
            Console.WriteLine("[matrix-result] PASS: " + scenario);
            return 0;
        }
        catch (Exception error)
        {
            Write("FAIL", error);
            Console.WriteLine("[matrix-result] FAIL: " + error);
            return 1;
        }
        finally
        {
            commands.Dispose();
            states.Dispose();
            if (!server.WaitForExit(10000)) { server.Kill(); server.WaitForExit(5000); }
        }
    }
}
