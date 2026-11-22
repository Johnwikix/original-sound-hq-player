using AudioPlayer;
using AudioPlayer.Playback;
using BassPlayerIpc.Shared;

internal static unsafe partial class Program
{
    private static void RunCueTransitionTests()
    {
        const int length = 20_480;
        string directory = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, $"cue-transitions-{Guid.NewGuid():N}")).FullName;
        try
        {
            string image = Path.Combine(directory, "image.wav");
            string secondImage = Path.Combine(directory, "image 2.wav");
            var whole = new AudioInput(Path.Combine(directory, "single.wav"));
            var otherRate = new AudioInput(Path.Combine(directory, "48k.wav"));
            WriteCueWav(image);
            WriteCueWav(secondImage);
            WriteTransitionWave(whole.Path, CueRate, length);
            WriteTransitionWave(otherRate.Path, 48000, length);
            var first = new AudioInput(image, new(317, 317 + length, CueRate));
            var adjacent = new AudioInput(image, new(first.SampleRange!.Value.EndSample, first.SampleRange.Value.EndSample + length, CueRate));
            var skipped = adjacent with { SampleRange = new(CueRate * 2, CueRate * 2 + length, CueRate) };
            var other = skipped with { Path = secondImage };

            Run("CUE transition: contiguous natural ranges splice at the exact sample even in Crossfade", () =>
                CheckCueHandoff(first, adjacent, TrackTransitionStyle.Crossfade, preserveBoundary: true, manual: false, expectedOverlap: 0));
            Run("CUE transition: Next overrides contiguous album-boundary preservation in Fixed and Smart modes", () =>
            {
                CheckCueHandoff(first, adjacent, TrackTransitionStyle.Crossfade, preserveBoundary: true, manual: true, expectedOverlap: length / 2);
                CheckCueHandoff(first, adjacent, TrackTransitionStyle.Smart, preserveBoundary: true, manual: true, expectedOverlap: length / 4);
            });
            Run("CUE transition: same-image skipped range and different image both mix", () =>
            {
                CheckCueHandoff(first, skipped, TrackTransitionStyle.Crossfade, preserveBoundary: false, manual: false, expectedOverlap: length / 2);
                CheckCueHandoff(first, other, TrackTransitionStyle.Smart, preserveBoundary: false, manual: false, expectedOverlap: length / 4);
            });
            Run("CUE transition: whole-file playback mixes in either direction and can splice without overlap", () =>
            {
                CheckCueHandoff(first, whole, TrackTransitionStyle.Crossfade, preserveBoundary: false, manual: false, expectedOverlap: length / 2);
                CheckCueHandoff(whole, first, TrackTransitionStyle.Crossfade, preserveBoundary: false, manual: false, expectedOverlap: length / 2);
                CheckCueHandoff(first, whole, TrackTransitionStyle.Gapless, preserveBoundary: false, manual: false, expectedOverlap: 0);
                CheckCueHandoff(whole, first, TrackTransitionStyle.Gapless, preserveBoundary: false, manual: false, expectedOverlap: 0);
            });
            Run("CUE transition: experimental Atmos allows Next into PCM ranges and preserves whole-file probing", () =>
            {
                CheckCueHandoff(whole, first, TrackTransitionStyle.Crossfade, preserveBoundary: false, manual: true,
                    expectedOverlap: length / 2, atmos: true);
                CheckCueHandoff(first, whole, TrackTransitionStyle.Crossfade, preserveBoundary: false, manual: true,
                    expectedOverlap: 0, atmos: true, unsupported: true);
            });
            Run("CUE transition: incompatible rates retain the outgoing range through EOF", () =>
                CheckCueHandoff(first, otherRate, TrackTransitionStyle.Crossfade, preserveBoundary: false, manual: false,
                    expectedOverlap: 0, unsupported: true));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void CheckCueHandoff(AudioInput from, AudioInput target, TrackTransitionStyle style, bool preserveBoundary,
        bool manual, int expectedOverlap, bool atmos = false, bool unsupported = false)
    {
        using var ipc = new PlayerIpcService();
        var engine = Engine("WasapiShared");
        Set(engine, "_streamLock", new object());
        Set(engine, "_dspSettings", new DspSettings { IsEnabled = false, GaplessPlayback = true, TransitionStyle = style });
        Set(ipc, "_engine", engine);
        Set(engine, "_ipc", ipc);
        engine.SetAudioInput(from);
        engine.ChangingSetting();
        engine.ExperimentalAtmosPassthrough = atmos;
        Set(engine, "_output", new CorrectionOutput("cue-transitions"));
        var first = (Session)typeof(PlaybackEngine).GetField("_session", Private)!.GetValue(engine)!;
        var source = (GaplessSource)Invoke(engine, "OutputSource", first)!;
        engine.IsPlaying = true;

        try
        {
            Require(SpinWait.SpinUntil(() => first.PcmInputStatus.Ended, 5000), "Outgoing decoder did not prebuffer");
            int length = first.ReadyFrames;
            var request = new GaplessRequest(first.TimelineEpoch, 501, target.Path, preserveBoundary, target.SampleRange);
            bool ranged = target.SampleRange.HasValue;
            byte[] payload = ranged ? request.WriteRanged() : request.Write();
            if (!manual || !unsupported)
            {
                var queue = ipc.HandleCommand(ranged ? CommandId.QueueNextAudioInput : CommandId.QueueNext, payload);
                Require(queue.Type == MessageTypeId.Success, "QueueNext rejected a valid request");
                Require(Preloader(engine).Completion.Wait(5000), "Preload did not complete");
            }

            if (manual)
            {
                var result = ipc.HandleCommand(ranged ? CommandId.TransitionNowAudioInput : CommandId.TransitionNow, payload);
                var expected = unsupported ? TransitionNowStatus.Unavailable : TransitionNowStatus.Preparing;
                Require(result.Type == MessageTypeId.Success && result.Payload.Span[0] == (byte)expected,
                    $"TransitionNow was expected to be {expected}");
            }

            if (unsupported)
            {
                const int tail = 23;
                var unjoined = new double[(length + tail) * 2];
                if (!manual) Require(Preloader(engine).State == GaplessPreparationState.Failed, "Incompatible PCM prepared a render source");
                source.FillPcm(unjoined, length + tail);
                CheckCueWavFrames(unjoined.AsSpan(0, length * 2), from.SampleRange!.Value.StartSample, length);
                Require(source.Current == first && source.CompletedToken == 0 && first.IsDrained
                    && unjoined.AsSpan(length * 2).IndexOfAnyExcept(0.0) < 0, "Incompatible target shortened or extended the source");
                return;
            }

            Require(SpinWait.SpinUntil(() => source.Pending?.PcmInputStatus.Ended == true, 5000), "Incoming decoder did not prebuffer");
            var incoming = source.Pending!;
            int prefix = manual ? 0 : length - expectedOverlap;
            const int post = 23;
            var output = new double[(prefix + expectedOverlap + post) * 2];
            if (prefix > 0) source.FillPcm(output.AsSpan(0, prefix * 2), prefix);
            Require(incoming.FramesPlayed == 0 && source.CompletedToken == 0,
                "Standby consumed or committed target audio before the overlap or splice");
            source.FillPcm(output.AsSpan(prefix * 2), expectedOverlap + post);
            for (int frame = 0; frame < prefix + expectedOverlap + post; frame++)
            {
                double mix = frame < prefix ? 0 : expectedOverlap == 0 ? 1 : Math.Min(1, (frame - prefix + 1.0) / expectedOverlap);
                for (int channel = 0; channel < 2; channel++)
                {
                    double expected = CueTransitionSample(from, frame, channel) * (1 - mix)
                        + CueTransitionSample(target, frame - prefix, channel) * mix;
                    if (Math.Abs(output[frame * 2 + channel] - expected) > 1e-9)
                        throw new InvalidOperationException($"Incorrect waveforms or overlap at frame {frame}.");
                }
            }

            engine.SynchronizeGapless();
            ProgressSnapshot progress = default;
            Require(SpinWait.SpinUntil(() => engine.TryCaptureProgress(out progress), 5000)
                && progress.GaplessToken == request.Token
                && progress.PlaybackId == (ranged ? request.Token : 0) && progress.TotalMs == incoming.TotalMs
                && source.Current == incoming && incoming.FramesPlayed == expectedOverlap + post && GetTestInput(engine) == target,
                $"Wrong handoff state: token={progress.GaplessToken}, playback={progress.PlaybackId}, total={progress.TotalMs}/{incoming.TotalMs}, "
                + $"frames={incoming.FramesPlayed}/{expectedOverlap + post}, current={source.Current == incoming}, input={GetTestInput(engine) == target}");
        }
        finally { CloseManualFixture(engine); }
    }

    private static double CueTransitionSample(AudioInput input, long frame, int channel) =>
        (input.SampleRange is { } range ? CueWavSample(range.StartSample + frame, channel) : channel == 0 ? 8192 : -16384) / 32768.0;

    private static void WriteTransitionWave(string path, int rate, int frames)
    {
        using var writer = new BinaryWriter(File.Create(path));
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
        for (int sample = 0; sample < frames; sample++)
        {
            writer.Write((short)8192);
            writer.Write((short)-16384);
        }
    }
}
