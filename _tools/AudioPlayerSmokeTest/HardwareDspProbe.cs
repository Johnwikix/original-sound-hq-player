using System.Diagnostics;
using System.Text.Json;
using BassPlayerIpc.Shared;

/// <summary>隔离发布进程与真实设备；低电平夹具不改动应用偏好、音频文件或系统混音格式。</summary>
internal static class HardwareDspProbe
{
    private static readonly JsonSerializerOptions ReportOptions = new()
    {
        WriteIndented = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
    };
    internal static int Run(Process server, PipeCommandClient commands, PipeStateClient states,
        string path, string mode, int seconds)
    {
        byte[] response = new byte[IpcConstants.MaxResponseSize];
        var samples = new List<object>();
        var clock = Stopwatch.StartNew();
        string log = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "hardware-dsp-" + mode + ".json");
        MessageTypeId Send(CommandId command, ReadOnlyMemory<byte> payload)
            => commands.RequestAsync(command, payload.Span, response, timeoutMs: 10000).GetAwaiter().GetResult().Type;
        void Expect(bool success, string message)
        {
            if (!success) throw new InvalidOperationException(message);
        }
        void Play()
        {
            byte[] payload = new byte[BinarySerializer.PlayRequestSize];
            BinarySerializer.WritePlayRequest(payload, new() { Url = path });
            Expect(Send(CommandId.Play, payload) == MessageTypeId.Success, "Play was rejected");
        }
        void Configure(DspSettings settings)
        {
            byte[] payload = new byte[DspProtocol.SettingsSize];
            DspProtocol.WriteSettings(payload, settings);
            Expect(Send(CommandId.UpdateDsp, payload) == MessageTypeId.Success, "DSP settings were rejected");
        }
        void WaitFor(Func<bool> condition, string message)
            => Expect(SpinWait.SpinUntil(condition, 10000), message);
        void Sample(string phase)
        {
            server.Refresh();
            states.TryGetProgress(out var position);
            var state = states.CurrentDspState?.State;
            samples.Add(new { Phase = phase, Seconds = clock.Elapsed.TotalSeconds, position.Epoch, position.CurrentMs,
                State = state, PrivateBytes = server.PrivateMemorySize64, WorkingSet = server.WorkingSet64,
                Handles = server.HandleCount, Threads = server.Threads.Count });
            File.WriteAllText(log, JsonSerializer.Serialize(new { Status = "RUNNING", Mode = mode, Samples = samples }, ReportOptions));
        }
        try
        {
            if (mode == "ListAsio")
            {
                var devices = new List<object>();
                byte[] request = new byte[1];
                for (byte pageIndex = 0; ; pageIndex++)
                {
                    BinarySerializer.WriteGetDevicesRequest(request, new() { Page = pageIndex });
                    Expect(Send(CommandId.GetAsioDevices, request) == MessageTypeId.AsioDevices, "ASIO enumeration failed");
                    var header = BinarySerializer.ReadDeviceListPageHeader(response);
                    int offset = BinarySerializer.DeviceListPageHeaderSize;
                    for (int i = 0; i < header.count; i++)
                    {
                        var device = BinarySerializer.ReadDeviceEntry(response.AsSpan(offset));
                        offset += device.bytesRead;
                        devices.Add(new { device.id, device.name });
                    }
                    if (pageIndex + 1 >= header.totalPages) break;
                }
                string listing = JsonSerializer.Serialize(new { Status = "PASS", Devices = devices }, ReportOptions);
                File.WriteAllText(log, listing);
                Console.WriteLine(listing);
                return 0;
            }
            byte[] settings = new byte[1024];
            int length = BinarySerializer.WriteIpcSetting(settings, new() { OutputMode = mode, BassOutputDeviceId = -1,
                Latency = 100, Volume = 0.02f, IsDopEnabled = false });
            Expect(Send(CommandId.UpdateSettings, settings.AsMemory(0, length)) == MessageTypeId.Success, "Output settings rejected");
            Configure(new());
            Play();
            WaitFor(() => states.TryGetProgress(out var p) && p.Playing && p.CurrentMs > 200, "Device playback did not advance");
            Sample("source");
            var dsp = new DspSettings { NormalizeLoudness = true, TargetLufs = -8, TruePeakCeilingDb = -6,
                ResampleMode = PcmResampleMode.Fixed, ResampleTargetRate = 96000, DitherEnabled = true };
            Configure(dsp);
            WaitFor(() => states.CurrentDspState?.State is { SampleRate: 96000, Loudness: LoudnessStatus.PeakLimited },
                "Fixed rate or normalized peak budget did not become active");
            double gain = states.CurrentDspState!.State.GainDb;
            Sample("normalization-minus6");
            Configure(dsp with { TruePeakCeilingDb = -3 });
            WaitFor(() => Math.Abs((states.CurrentDspState?.State.GainDb ?? double.NaN) - gain - 3) < 0.01,
                "Shared ceiling did not change real playback normalization gain by 3 dB");
            Sample("normalization-minus3");
            dsp = dsp with { TruePeakLimiterEnabled = true, TruePeakCeilingDb = -3,
                ResampleMode = PcmResampleMode.IntegerMultiple, ResampleTargetRate = 192000 };
            Configure(dsp);
            WaitFor(() => states.CurrentDspState?.State is { SampleRate: 176400 }
                || mode != "WasapiShared" && states.CurrentDspState?.State is { ActualOutput: ActualOutputMode.Shared, SampleRate: > 0 },
                "Integer-multiple rate or explicit compatibility fallback was not negotiated");
            bool compatibilityFallback = states.CurrentDspState?.State.ActualOutput == ActualOutputMode.Shared && mode != "WasapiShared";
            Sample(compatibilityFallback ? "integer-compatibility-fallback" : "integer-multiple");
            long previous = -1;
            for (int second = 0; second < seconds; second++)
            {
                if (second > 0 && second % 6 == 0) { Play(); previous = -1; }
                Thread.Sleep(1000);
                Expect(states.TryGetProgress(out var progress) && progress.Playing && progress.CurrentMs > previous,
                    "Actual device playback stopped advancing");
                previous = progress.CurrentMs;
                Sample("playing");
            }
            Sample("before-stop");
            Expect(Send(CommandId.MusicEnd, ReadOnlyMemory<byte>.Empty) == MessageTypeId.Success, "Stop rejected");
            Thread.Sleep(1000);
            Sample("stopped");
            byte[] page = new byte[1];
            BinarySerializer.WriteGetDevicesRequest(page, new() { Page = 0 });
            int asioDevices = Send(CommandId.GetAsioDevices, page) == MessageTypeId.AsioDevices
                ? BinarySerializer.ReadDeviceListPageHeader(response).count : -1;
            File.WriteAllText(log, JsonSerializer.Serialize(new { Status = "PASS", Mode = mode, CompatibilityFallback = compatibilityFallback, AsioDevices = asioDevices,
                Note = "Actual driver output, IPC state and progress only; analog DAC output and listening are not measured.",
                Samples = samples }, ReportOptions));
            Console.WriteLine("PASS: actual device DSP; " + log);
            return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(log, JsonSerializer.Serialize(new { Status = "FAIL", Mode = mode, Error = error.ToString(), Samples = samples },
                ReportOptions));
            Console.WriteLine("FAIL: " + error);
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
