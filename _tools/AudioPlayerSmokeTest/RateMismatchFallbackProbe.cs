using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using BassPlayerIpc.Shared;

/// <summary>以真实设备、双管道和静音 PCM 验证不同采样率的普通换曲回退。</summary>
internal static class RateMismatchFallbackProbe
{
    private sealed record Device(int Index, string Name, string Identity);

    internal static int Run(string[] args, PipeCommandClient commands, PipeStateClient states, string firstPath, string mode)
    {
        var response = new byte[IpcConstants.MaxResponseSize];
        if (args.Contains("--list-output-devices"))
        {
            foreach (bool useAsio in new[] { false, true })
            {
                foreach (var device in Enumerate(useAsio))
                    Console.WriteLine($"[rate-fallback] {(useAsio ? "ASIO" : "WASAPI")} [{device.Index}] {device.Name} | {device.Identity}");
            }
            return 0;
        }

        string scenario = Option("--rate-mismatch-fallback=");
        Check(scenario is "manual" or "natural", "Select --rate-mismatch-fallback=manual or natural");
        bool manual = scenario == "manual";
        bool control = args.Contains("--no-queued-candidate");
        Check(!control || !manual, "--no-queued-candidate is a natural EOF control");
        bool asio = mode == "ASIO";
        Check(asio || mode is "WasapiExclusiveEvent" or "WasapiExclusivePush", "Select ASIO, WasapiExclusiveEvent, or WasapiExclusivePush");
        string? sharedRateText = args.FirstOrDefault(a => a.StartsWith("--shared-fallback-rate=", StringComparison.Ordinal))?["--shared-fallback-rate=".Length..];
        int? sharedRate = sharedRateText == null ? null : int.Parse(sharedRateText, CultureInfo.InvariantCulture);
        Check(sharedRate == null || asio && sharedRate > 0, "A documented shared fallback can only be expected when requesting ASIO");
        int firstRate = int.Parse(Option("--expect-rate="), CultureInfo.InvariantCulture);
        int nextRate = int.Parse(Option("--next-rate="), CultureInfo.InvariantCulture);
        Check(firstRate > 0 && nextRate > 0 && firstRate != nextRate, "Fixture rates must differ");
        string nextPath = Path.GetFullPath(Option("--next="));
        string deviceSelector = Option("--device-name=");
        var matches = Enumerate(asio).Where(device => device.Name.Contains(deviceSelector, StringComparison.OrdinalIgnoreCase)).ToArray();
        Check(matches.Length == 1, $"Device selector '{deviceSelector}' matched {matches.Length} entries. Run --list-output-devices to disambiguate.");
        Device selected = matches[0];
        ActualOutputMode expectedOutput = sharedRate.HasValue ? ActualOutputMode.Shared : asio ? ActualOutputMode.Asio : ActualOutputMode.Exclusive;
        string? expectedEndpoint = sharedRate.HasValue ? null : selected.Identity;
        Stopwatch elapsed = Stopwatch.StartNew();
        int ended = 0, transitions = 0, explicitSwitches = 0;
        void OnNotification(MessageTypeId type, ReadOnlyMemory<byte> _)
        {
            if (type == MessageTypeId.PlayEnded) Interlocked.Increment(ref ended);
            if (type == MessageTypeId.GaplessTransition) Interlocked.Increment(ref transitions);
        }
        states.NotificationReceived += OnNotification;
        try
        {
            Span<byte> settings = stackalloc byte[1024];
            int settingsSize = BinarySerializer.WriteIpcSetting(settings, new()
            {
                OutputMode = mode,
                WasapiEndpointId = asio ? null : selected.Identity,
                BassOutputDeviceId = asio ? -1 : selected.Index,
                BassASIODeviceId = asio ? selected.Index : 0,
                Latency = 100,
                Volume = 0,
                IsDopEnabled = false,
                IsFadeEnabled = true,
            });
            Send(CommandId.UpdateSettings, settings[..settingsSize]);
            Span<byte> dspSettings = stackalloc byte[DspProtocol.SettingsSize];
            DspProtocol.WriteSettings(dspSettings, new()
            {
                GaplessPlayback = true,
                TransitionStyle = TrackTransitionStyle.Smart,
                CrossfadeMilliseconds = 2000,
                ResampleMode = PcmResampleMode.Source,
            });
            Send(CommandId.UpdateDsp, dspSettings);
            Play(firstPath);
            Wait(() => ReadProgress() is { Playing: true, CurrentMs: >= 150 } && FormatIs(firstRate), 12000, "Initial output did not advance in the expected mode");
            var initial = ReadProgress();
            expectedEndpoint = states.CurrentDspState!.State.OutputDeviceId;
            long initialGeneration = states.CurrentDspState!.State.OutputGeneration;

            if (!manual)
            {
                Check(initial.TotalMs > 6000, "Use a fixture longer than six seconds");
                const long seekId = 101;
                long seekTarget = initial.TotalMs - 3000;
                Span<byte> seek = stackalloc byte[BinarySerializer.ChangePositionRequestSize];
                BinarySerializer.WriteChangePositionRequest(seek, new() { PositionMs = seekTarget, SeekId = seekId });
                Send(CommandId.ChangePosition, seek);
                Wait(() => ReadProgress() is { Playing: true, SeekId: seekId } p && p.CurrentMs >= seekTarget && p.Epoch != initial.Epoch,
                    4000, "Seek to natural boundary did not advance on the same output");
                initial = ReadProgress();
            }
            else Check(initial.TotalMs - initial.CurrentMs >= 15000, "Manual test requires at least 15 seconds remaining");

            Console.WriteLine($"[rate-fallback] initial {mode}/{expectedOutput} {sharedRate ?? firstRate} Hz, requested={selected.Name}, actualEndpoint={expectedEndpoint}, epoch={initial.Epoch}, generation={initialGeneration}, progress={initial.CurrentMs}/{initial.TotalMs}ms");
            const long token = 77;
            var plan = new GaplessRequest(initial.Epoch, token, nextPath);
            byte[] start = plan.Write();
            if (!control)
            {
                var queued = Send(CommandId.QueueNext, start);
                Check(queued.Length == 16 && BinaryPrimitives.ReadInt64LittleEndian(queued[8..]) == initial.Epoch, "Next candidate was not queued for the expected epoch");
            }
            if (manual)
            {
                var initialStatus = ReadStatus(start);
                Check(initialStatus == TransitionNowStatus.Preparing, $"Unexpected manual admission: {initialStatus}");
                byte[] query = new byte[TransitionNowQuery.Size];
                TransitionNowQuery.Write(query, plan.Epoch, plan.Token);
                Wait(() =>
                {
                    ValidateOldTrack();
                    var status = ReadStatus(query);
                    Check(status is TransitionNowStatus.Preparing or TransitionNowStatus.Unavailable, $"Incompatible format unexpectedly reported {status}");
                    return status == TransitionNowStatus.Unavailable;
                }, 7000, "Incompatible manual candidate never reported Unavailable");
                Check(ReadProgress().Playing && ended == 0, "Outgoing track did not survive manual rejection");
                Console.WriteLine("[rate-fallback] manual query Preparing -> Unavailable, original track still playing");
            }
            else
            {
                Wait(() => { ValidateOldTrack(); return Volatile.Read(ref ended) > 0; }, 8500, "Incompatible natural candidate did not leave a normal PlayEnded");
                var atEnd = ReadTime();
                Check(ended == 1 && Math.Abs(atEnd.Current - initial.TotalMs) <= 200 && atEnd.Total == initial.TotalMs,
                    $"Expected one drained PlayEnded, got {ended} at {atEnd.Current}/{atEnd.Total}ms");
                Console.WriteLine($"[rate-fallback] natural end at {atEnd.Current}/{atEnd.Total}ms, one PlayEnded");
            }

            var cancelled = Send(CommandId.QueueNext, new GaplessRequest(0, 0, "").Write());
            Check(cancelled.Length == 16 && BinaryPrimitives.ReadInt64LittleEndian(cancelled) == 0
                && BinaryPrimitives.ReadInt64LittleEndian(cancelled[8..]) == initial.Epoch, "Cancellation barrier reported a hidden handoff");
            ValidateOldTrack();
            Play(nextPath);
            explicitSwitches++;
            long observedSwitchEpoch = 0;
            Wait(() =>
            {
                var progress = ReadProgress();
                if (progress.Epoch == initial.Epoch || progress.Epoch == 0) return false;
                if (observedSwitchEpoch == 0) observedSwitchEpoch = progress.Epoch;
                Check(observedSwitchEpoch == progress.Epoch, "Multiple new playback epochs were observed after one Play");
                return progress is { Playing: true, CurrentMs: >= 250 } && FormatIs(nextRate);
            },
                12000, "Ordinary Play did not restart on the target output format");
            var switched = ReadProgress();
            long newGeneration = states.CurrentDspState!.State.OutputGeneration;
            Check(newGeneration > initialGeneration, "Rate change did not rebuild the hardware output");
            Check(switched.Revision > initial.Revision && switched.TotalMs != initial.TotalMs && switched.CurrentMs < 3000
                && switched.GaplessToken == 0, "Ordinary Play did not produce fresh target-track progress");
            Wait(() =>
            {
                var latest = ReadProgress();
                Check(latest.Playing && latest.Epoch == switched.Epoch && latest.GaplessToken == 0 && FormatIs(nextRate)
                    && states.CurrentDspState!.State.OutputGeneration == newGeneration, "Second switch, output rebuild, or fallback mode intercepted the resumed track");
                return latest.CurrentMs - switched.CurrentMs >= 600;
            }, 5000, "Target track progress stalled");
            Check(explicitSwitches == 1 && ended == (manual ? 0 : 1) && transitions == 0, "Unexpected switch or end notification");
            var final = ReadProgress();
            Console.WriteLine($"[rate-fallback] PASS {mode} {scenario} source {firstRate}->{nextRate} Hz, actual={expectedOutput}/{sharedRate ?? nextRate} Hz, queued={!control}, elapsed={elapsed.ElapsedMilliseconds}ms, epoch={initial.Epoch}->{final.Epoch}, generation={initialGeneration}->{newGeneration}, progress={switched.CurrentMs}->{final.CurrentMs}/{final.TotalMs}ms, PlayEnded={ended}, GaplessTransition={transitions}, ordinaryPlay=1");
            Send(CommandId.MusicEnd, []);
            return 0;

            void ValidateOldTrack()
            {
                var progress = ReadProgress();
                Check(progress.Epoch == initial.Epoch && progress.GaplessToken == 0 && transitions == 0,
                    "Incompatible plan changed track identity before ordinary Play");
                Check(FormatIs(firstRate) && states.CurrentDspState!.State.OutputGeneration == initialGeneration,
                    "Incompatible preload changed the outgoing output before ordinary Play");
            }
        }
        finally { states.NotificationReceived -= OnNotification; }

        string Option(string prefix) => args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..]
            ?? throw new ArgumentException($"Required option: {prefix}...");

        bool FormatIs(int rate) => states.CurrentDspState?.State is { } dsp && dsp.ActualOutput == expectedOutput
            && dsp.SampleRate == (sharedRate ?? rate) && dsp.OutputDeviceId.Length > 0
            && (expectedEndpoint == null || string.Equals(dsp.OutputDeviceId, expectedEndpoint, StringComparison.OrdinalIgnoreCase));

        ProgressSnapshot ReadProgress() => states.TryGetProgress(out var progress) ? progress : default;

        (long Current, long Total) ReadTime() => BinarySerializer.ReadTimeProgress(Send(CommandId.GetTimeProgress, [], MessageTypeId.TimeProgress));

        void Play(string path)
        {
            Span<byte> request = stackalloc byte[BinarySerializer.PlayRequestSize];
            int bytes = BinarySerializer.WritePlayRequest(request, new() { Url = path });
            Send(CommandId.Play, request[..bytes]);
        }

        TransitionNowStatus ReadStatus(ReadOnlySpan<byte> payload)
        {
            var reply = Send(CommandId.TransitionNow, payload);
            Check(reply.Length == 1 && Enum.IsDefined((TransitionNowStatus)reply[0]), "Invalid transition status payload");
            return (TransitionNowStatus)reply[0];
        }

        void Wait(Func<bool> condition, int timeoutMs, string failure)
        {
            var timer = Stopwatch.StartNew();
            do
            {
                if (condition()) return;
                Thread.Sleep(20);
            } while (timer.ElapsedMilliseconds < timeoutMs);
            var p = ReadProgress();
            var d = states.CurrentDspState?.State;
            throw new InvalidOperationException($"{failure}. Epoch={p.Epoch}, progress={p.CurrentMs}/{p.TotalMs}ms, playing={p.Playing}, output={d?.ActualOutput}/{d?.SampleRate} Hz, endpoint={d?.OutputDeviceId}");
        }

        List<Device> Enumerate(bool useAsio)
        {
            var devices = new List<Device>();
            Span<byte> request = stackalloc byte[1];
            for (byte page = 0; ; page++)
            {
                BinarySerializer.WriteGetDevicesRequest(request, new() { Page = page });
                var payload = Send(useAsio ? CommandId.GetAsioDevices : CommandId.GetWasapiDevices, request,
                    useAsio ? MessageTypeId.AsioDevices : MessageTypeId.WasapiDevices);
                var header = BinarySerializer.ReadDeviceListPageHeader(payload);
                int offset = BinarySerializer.DeviceListPageHeaderSize;
                for (int i = 0; i < header.count; i++)
                {
                    var (index, name, nameBytes) = BinarySerializer.ReadDeviceEntry(payload[offset..]);
                    offset += nameBytes;
                    var (identityIndex, identity, identityBytes) = BinarySerializer.ReadDeviceEntry(payload[offset..]);
                    offset += identityBytes;
                    Check(nameBytes > 0 && identityBytes > 0 && identityIndex == index && identity.Length > 0, "Missing stable device identity");
                    devices.Add(new(index, name, identity));
                }
                if (page + 1 >= header.totalPages) return devices;
            }
        }

        ReadOnlySpan<byte> Send(CommandId command, scoped ReadOnlySpan<byte> payload, MessageTypeId expected = MessageTypeId.Success)
        {
            var (type, length) = commands.RequestAsync(command, payload, response, timeoutMs: 12000).GetAwaiter().GetResult();
            Check(type == expected, $"{command} returned {type}; expected {expected}");
            return response.AsSpan(0, length);
        }
    }

    private static void Check(bool condition, string details)
    {
        if (!condition) throw new InvalidOperationException(details);
    }
}
