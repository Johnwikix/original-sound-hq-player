using System.Collections.Concurrent;
using BassPlayerIpc.Shared;

internal static class CuePlaybackProbe
{
    internal static int Run(PipeCommandClient commands, PipeStateClient states, string path)
    {
        const int sampleRate = 44100;
        const long trackBoundary = 11_186_112;
        const long physicalEnd = 94_518_159;
        const long playbackId = 301;
        const long nextToken = 302;
        const long finalPlaybackId = 303;
        var first = new AudioInput(path, new(trackBoundary - 5 * sampleRate, trackBoundary, sampleRate));
        var incoming = new AudioInput(path, new(trackBoundary, trackBoundary + 4 * sampleRate, sampleRate));
        var response = new byte[IpcConstants.MaxResponseSize];
        var endings = new ConcurrentQueue<PlaybackEnded>();
        void OnNotification(MessageTypeId type, ReadOnlyMemory<byte> payload)
        {
            if (type == MessageTypeId.PlayEnded && PlaybackEnded.TryRead(payload.Span, out var ended)) endings.Enqueue(ended);
        }

        (MessageTypeId Type, int Length) Send(CommandId command, ReadOnlySpan<byte> payload) =>
            commands.RequestAsync(command, payload, response, timeoutMs: 5000).GetAwaiter().GetResult();

        static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        states.NotificationReceived += OnNotification;
        try
        {
            var settings = new byte[BinarySerializer.IpcSettingSize];
            int settingsLength = BinarySerializer.WriteIpcSetting(settings, new()
            {
                OutputMode = "WasapiShared",
                BassOutputDeviceId = -1,
                Latency = 100,
                Volume = 0,
                IsSettingChanged = true,
            });
            byte[] dsp = new byte[DspProtocol.SettingsSize];
            DspProtocol.WriteSettings(dsp, new() { IsEnabled = false, GaplessPlayback = true, TransitionStyle = TrackTransitionStyle.Crossfade });
            Check(Send(CommandId.UpdateDsp, dsp).Type == MessageTypeId.Success, "DSP setup failed");
            Check(Send(CommandId.SetAudioInput, AudioInputProtocol.Write(first)).Type == MessageTypeId.Success, "Startup source was rejected");
            Check(Send(CommandId.UpdateSettings, settings.AsSpan(0, settingsLength)).Type == MessageTypeId.Success, "Startup settings failed");
            Check(SpinWait.SpinUntil(() => states.TryGetProgress(out var snap) && !snap.Playing && snap.TotalMs == 5000, 5000),
                "Prepared progress did not describe the first clip");

            Check(Send(CommandId.PlayAudioInput, AudioInputProtocol.WriteOccurrence(playbackId, first)).Type == MessageTypeId.Success,
                "Explicit ranged playback command was rejected");
            ProgressSnapshot playing = default;
            Check(SpinWait.SpinUntil(() => states.TryGetProgress(out playing) && playing.Playing && playing.PlaybackId == playbackId, 5000)
                && playing.TotalMs == 5000 && playing.CurrentMs < 1000, "First clip failed to start on WASAPI");
            long firstEpoch = playing.Epoch;
            var seek = new byte[BinarySerializer.ChangePositionRequestSize];
            BinarySerializer.WriteChangePositionRequest(seek, new() { PositionMs = 1000, SeekId = 8 });
            Check(Send(CommandId.ChangePosition, seek).Type == MessageTypeId.Success, "Range seek was rejected");
            Check(SpinWait.SpinUntil(() => states.TryGetProgress(out playing) && playing.Epoch != firstEpoch && playing.SeekId == 8, 5000)
                && playing.CurrentMs is >= 1000 and < 2000 && playing.PlaybackId == playbackId,
                "Seek did not confirm the clip-relative position");

            var next = new GaplessRequest(playing.Epoch, nextToken, incoming.Path, true, incoming.SampleRange);
            Check(Send(CommandId.QueueNextAudioInput, next.WriteRanged()).Type == MessageTypeId.Success, "Ranged queue plan was rejected");
            ProgressSnapshot adopted = default;
            Check(SpinWait.SpinUntil(() => states.TryGetProgress(out adopted) && adopted.PlaybackId == nextToken && adopted.GaplessToken == nextToken, 6500)
                && adopted.Playing && adopted.TotalMs == 4000 && adopted.CurrentMs < 1000,
                "Second clip did not take over at its own track clock");
            Check(SpinWait.SpinUntil(() => endings.Count == 1, 6000)
                && endings.TryPeek(out var ended) && ended.PlaybackId == nextToken && ended.Epoch == adopted.Epoch,
                "Expected a single natural end for the final clip");
            Check(SpinWait.SpinUntil(() => states.TryGetProgress(out var snap) && !snap.Playing && snap.PlaybackId == nextToken, 1000)
                && endings.Count == 1, "EOF did not settle to one stopped playback occurrence");

            var finalRange = new AudioSampleRange(physicalEnd - 10003, physicalEnd, sampleRate);
            Check(Send(CommandId.PlayAudioInput, AudioInputProtocol.WriteOccurrence(finalPlaybackId, new(path, finalRange))).Type == MessageTypeId.Success,
                "Final ranged playback command was rejected");
            Check(SpinWait.SpinUntil(() => endings.Count == 2, 5000)
                && endings.Last().PlaybackId == finalPlaybackId, "Near-EOF FLAC clip did not complete");
            Check(SpinWait.SpinUntil(() => states.TryGetProgress(out var snap) && !snap.Playing
                && snap.PlaybackId == finalPlaybackId && snap.TotalMs == finalRange.DurationMs, 1000),
                "Final clip reported the physical image duration");
            Console.WriteLine("[CUE smoke] startup, explicit range, seek, gapless handoff and physical FLAC EOF passed on WASAPI.");
            return 0;
        }
        finally { states.NotificationReceived -= OnNotification; }
    }
}
