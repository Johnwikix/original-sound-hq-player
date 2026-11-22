using System.Buffers.Binary;
using System.Collections.Concurrent;
using BassPlayerIpc.Shared;

internal static class CueTransitionProbe
{
    internal static int Run(PipeCommandClient commands, PipeStateClient states, Action stopPlayer)
    {
        const int rate = 44100;
        string directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"cue-transition-smoke-{Guid.NewGuid():N}")).FullName;
        var response = new byte[IpcConstants.MaxResponseSize];
        var ended = new ConcurrentQueue<PlaybackEnded>();
        var transitions = new ConcurrentQueue<long>();
        long identity = 500;
        void Notification(MessageTypeId type, ReadOnlyMemory<byte> payload)
        {
            if (type == MessageTypeId.PlayEnded && PlaybackEnded.TryRead(payload.Span, out var value)) ended.Enqueue(value);
            if (type == MessageTypeId.GaplessTransition) transitions.Enqueue(BinaryPrimitives.ReadInt64LittleEndian(payload.Span));
        }
        states.NotificationReceived += Notification;
        try
        {
            string image = Path.Combine(directory, "CUE image.wav");
            var whole = new AudioInput(Path.Combine(directory, "ordinary 44k.wav"));
            var incompatible = new AudioInput(Path.Combine(directory, "ordinary 48k.wav"));
            WriteWave(image, rate, seconds: 5);
            WriteWave(whole.Path, rate, seconds: 2);
            WriteWave(incompatible.Path, 48000, seconds: 2);
            var first = new AudioInput(image, new(rate / 2, rate * 5 / 2, rate));
            var adjacent = new AudioInput(image, new(rate * 5 / 2, rate * 9 / 2, rate));
            var dsp = new byte[DspProtocol.SettingsSize];
            DspProtocol.WriteSettings(dsp, new()
            {
                IsEnabled = false, GaplessPlayback = true, TransitionStyle = TrackTransitionStyle.Crossfade, CrossfadeMilliseconds = 500,
            });
            Send(CommandId.UpdateDsp, dsp);
            SetOutput();
            CheckHandoff(first, adjacent, preserve: true, manual: false);
            CheckHandoff(first, whole, preserve: false, manual: false);
            CheckHandoff(whole, first, preserve: false, manual: false);
            SetOutput(atmos: true);
            CheckHandoff(first, adjacent, preserve: true, manual: true);
            SetOutput();
            CheckFallback(first, incompatible);
            Console.WriteLine("[CUE transitions] PASS: shared WASAPI, PCM overlap/splice, single terminal EOF and incompatible rate fallback.");
            return 0;
        }
        finally
        {
            states.NotificationReceived -= Notification;
            try { stopPlayer(); }
            finally { Directory.Delete(directory, recursive: true); }
        }

        void Send(CommandId command, ReadOnlySpan<byte> payload)
        {
            var (type, _) = commands.RequestAsync(command, payload, response, timeoutMs: 5000).GetAwaiter().GetResult();
            Check(type == MessageTypeId.Success, $"IPC rejected {command}: {type}");
        }

        void SetOutput(bool atmos = false)
        {
            var settings = new byte[BinarySerializer.IpcSettingSize];
            int length = BinarySerializer.WriteIpcSetting(settings, new()
            {
                OutputMode = "WasapiShared", BassOutputDeviceId = -1, Latency = 100, Volume = 0, IsSettingChanged = true,
                ExperimentalAtmosPassthrough = atmos,
            });
            Send(CommandId.UpdateSettings, settings.AsSpan(0, length));
        }

        ProgressSnapshot WaitProgress(Func<ProgressSnapshot, bool> check, string failure)
        {
            ProgressSnapshot progress = default;
            Check(SpinWait.SpinUntil(() => states.TryGetProgress(out progress) && check(progress), 5000), failure);
            return progress;
        }

        ProgressSnapshot Play(AudioInput input, long id)
        {
            long previousEpoch = states.TryGetProgress(out var prior) ? prior.Epoch : 0;
            if (input.SampleRange.HasValue)
                Send(CommandId.PlayAudioInput, AudioInputProtocol.WriteOccurrence(id, input));
            else
            {
                Span<byte> payload = stackalloc byte[BinarySerializer.PlayOccurrenceSize];
                int length = BinarySerializer.WritePlayOccurrence(payload, id, input.Path);
                Send(CommandId.PlayOccurrence, payload[..length]);
            }
            return WaitProgress(p => p.Playing && p.Epoch > previousEpoch && p.GaplessToken == 0
                && p.PlaybackId == (input.SampleRange.HasValue ? id : 0), $"Expected fresh playback progress for {input}");
        }

        void CheckHandoff(AudioInput from, AudioInput to, bool preserve, bool manual)
        {
            long outgoingId = ++identity;
            long token = ++identity;
            var initial = Play(from, outgoingId);
            var request = new GaplessRequest(initial.Epoch, token, to.Path, preserve, to.SampleRange);
            bool ranged = to.SampleRange.HasValue;
            byte[] payload = ranged ? request.WriteRanged() : request.Write();
            Send(ranged ? CommandId.QueueNextAudioInput : CommandId.QueueNext, payload);
            if (manual)
            {
                Send(ranged ? CommandId.TransitionNowAudioInput : CommandId.TransitionNow, payload);
                Check(response[0] is (byte)TransitionNowStatus.Preparing or (byte)TransitionNowStatus.Started, "Manual transition did not start");
            }
            bool overlap = manual || !preserve;
            if (overlap)
            {
                var mixing = WaitProgress(p => p.TransitionToken == token, "The incoming decoder never entered the mix");
                Check(mixing.Epoch == initial.Epoch && mixing.TransitionTotalMs == 2000
                    && mixing.TransitionCurrentMs is >= 0 and < 500, "The mixed lanes reported the wrong timelines");
            }
            var adopted = WaitProgress(p => p.Playing && p.GaplessToken == token, "The incoming decoder never took over");
            Check(adopted.Epoch != initial.Epoch && adopted.PlaybackId == (ranged ? token : 0)
                && adopted.TotalMs == 2000 && (overlap ? adopted.CurrentMs is >= 350 and < 850 : adopted.CurrentMs < 300),
                "Adopted progress did not report the consumed intro or preserved boundary");
            var terminal = WaitForEnd(token, adopted.Epoch);
            Check(terminal.GaplessToken == token && terminal.CurrentMs == terminal.TotalMs && transitions.Count(t => t == token) == 1
                && !ended.Any(e => e.PlaybackId == outgoingId), "The handoff emitted duplicate transitions or an outgoing EOF");
            Console.WriteLine($"[CUE transitions] {Path.GetFileName(from.Path)} → {Path.GetFileName(to.Path)} manual={manual} position={adopted.CurrentMs}ms, end={terminal.CurrentMs}ms");
        }

        void CheckFallback(AudioInput from, AudioInput to)
        {
            long outgoingId = ++identity;
            long rejected = ++identity;
            var initial = Play(from, outgoingId);
            Send(CommandId.QueueNext, new GaplessRequest(initial.Epoch, rejected, to.Path).Write());
            var terminal = WaitForEnd(outgoingId, initial.Epoch);
            Check(terminal.GaplessToken == 0 && terminal.CurrentMs == terminal.TotalMs && terminal.TotalMs == 2000
                && !transitions.Contains(rejected), "Different sample rates truncated or took over the outgoing clip");
            long playingId = ++identity;
            var next = Play(to, playingId);
            var nextEnd = WaitForEnd(playingId, next.Epoch);
            Check(nextEnd.GaplessToken == 0 && nextEnd.TotalMs == 2000 && nextEnd.CurrentMs == 2000,
                "Ordinary playback did not recover from the incompatible preload");
            Console.WriteLine("[CUE transitions] 44.1-to-48 kHz incompatible preload drained once, followed by one ordinary Play.");
        }

        ProgressSnapshot WaitForEnd(long id, long epoch)
        {
            Check(SpinWait.SpinUntil(() => ended.Any(e => e.PlaybackId == id), 5000), $"Missing EOF for occurrence {id}");
            Check(ended.Count(e => e.PlaybackId == id) == 1 && ended.Single(e => e.PlaybackId == id).Epoch == epoch,
                $"Wrong EOF identity for occurrence {id}");
            return WaitProgress(p => !p.Playing && p.Epoch == epoch, "EOF did not settle to stopped progress");
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void WriteWave(string path, int rate, int seconds)
    {
        using var writer = new BinaryWriter(File.Create(path));
        int frames = rate * seconds;
        writer.Write("RIFF"u8);
        writer.Write(36 + frames * 4);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)2);
        writer.Write(rate);
        writer.Write(rate * 4);
        writer.Write((short)4);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(frames * 4);
        for (int frame = 0; frame < frames; frame++)
        {
            short sample = (short)(8192 * Math.Sin(2 * Math.PI * 440 * frame / rate));
            writer.Write(sample);
            writer.Write(sample);
        }
    }
}
