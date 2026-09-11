using System.Diagnostics;
using System.Runtime.CompilerServices;
using AudioPlayer;
using AudioPlayer.Playback;
using BassPlayerIpc.Shared;

internal static unsafe partial class Program
{
    private static DspState NumberedState(int number) =>
        new(0, number % 2 == 0, number, LoudnessStatus.Applied, number, -number, true);

    private static int DspWriter(string name)
    {
        using var mailbox = new DspStateMailbox(create: false, name);
        for (int i = 1; i <= 10000; i++) mailbox.Publish(NumberedState(i));
        return 0;
    }

    private static void RunDspNotificationTests()
    {
        Run("DSP mailbox: late subscription, NaN deduplication and coalesced wakeups", () =>
        {
            string name = "DspTest-" + Guid.NewGuid().ToString("N");
            using var writer = new DspStateMailbox(create: true, name);
            var initial = new DspState(0, false, 0, LoudnessStatus.Off, 0, double.NaN);
            Require(writer.Read() == null, "unpublished state must be unavailable");
            Require(writer.Publish(initial), "initial state missing");
            using var reader = new DspStateMailbox(create: false, name);
            Require(reader.Read() is { Revision: 1 } first && first.State == initial, "late reader missed initial state");
            Require(reader.Changed.WaitOne(1000), "initial wakeup missing");
            Require(!writer.Publish(initial) && !reader.Changed.WaitOne(0), "NaN causes duplicate notifications");
            for (int i = 1; i <= 100; i++) writer.Publish(NumberedState(i));
            Require(reader.Changed.WaitOne(1000), "burst wakeup lost");
            Require(reader.Read() is { Revision: 101 } last && last.State == NumberedState(100), "burst lost final state");
            Require(!reader.Changed.WaitOne(0), "wakeups should coalesce");
        });

        Run("DSP mailbox: cross-process burst never exposes torn or regressing snapshots", () =>
        {
            string name = "DspTest-" + Guid.NewGuid().ToString("N");
            using var reader = new DspStateMailbox(create: true, name);
            var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            info.ArgumentList.Add("--dsp-writer"); info.ArgumentList.Add(name);
            using var process = Process.Start(info)!;
            try
            {
                long revision = 0;
                var timeout = Stopwatch.StartNew();
                while (revision < 10000 && timeout.Elapsed < TimeSpan.FromSeconds(15))
                {
                    Require(reader.Changed.WaitOne(5000), "writer stopped notifying");
                    var snapshot = reader.Read()!;
                    Require(snapshot.Revision >= revision && snapshot.State == NumberedState((int)snapshot.Revision),
                        "snapshot torn or revision regressed");
                    revision = snapshot.Revision;
                }
                Require(revision == 10000, "final snapshot missing");
                Require(process.WaitForExit(5000) && process.ExitCode == 0, "writer failed");
            }
            finally { if (!process.HasExited) { process.Kill(); process.WaitForExit(); } }
        });

        Run("DSP engine: settings, session replacement and effects changes actively publish", () =>
        {
            string name = "DspTest-" + Guid.NewGuid().ToString("N");
            using var mailbox = new DspStateMailbox(create: true, name);
            var service = (PlayerIpcService)RuntimeHelpers.GetUninitializedObject(typeof(PlayerIpcService));
            Set(service, "_dspMailbox", mailbox);
            var engine = Engine("DirectSound");
            var streamLock = new object();
            Set(engine, "_streamLock", streamLock);
            Set(engine, "_ipc", service);
            using var pcm = Source(engine, RenderKind.Pcm, 44100, 2);
            using var bitstream = Source(engine, RenderKind.Dop);
            try
            {
                engine.UpdateDsp(new() { IsEnabled = false });
                Require(mailbox.Changed.WaitOne(5000) && mailbox.Read()!.State.IsEnabled == false, "idle setting change missing");
                lock (streamLock) Invoke(engine, "SetSession", pcm);
                Require(mailbox.Changed.WaitOne(5000) && mailbox.Read()!.State.Channels == 2, "PCM attachment missing");
                pcm.ConfigureDsp(new() { NormalizeLoudness = true }); // No file: Unavailable.
                Require(mailbox.Changed.WaitOne(5000) && mailbox.Read()!.State.Loudness == LoudnessStatus.Unavailable,
                    "effects state change missing");
                lock (streamLock) Invoke(engine, "SetSession", bitstream);
                Require(mailbox.Changed.WaitOne(5000) && mailbox.Read()!.State.RenderKind == (byte)RenderKind.Dop,
                    "bitstream attachment missing");
                pcm.ConfigureDsp(new());
                Require(!mailbox.Changed.WaitOne(100), "detached effects still publish");
                lock (streamLock) Invoke(engine, "SetSession", (object?)null);
                Require(mailbox.Changed.WaitOne(5000) && mailbox.Read()!.State.Channels == 0, "session removal missing");
            }
            finally
            {
                // Drain any worker before disposing the mailbox; avoid touching global endpoint registration.
                lock (streamLock) { Set(engine, "_disposed", 1); Invoke(engine, "SetSession", (object?)null); }
            }
        });

        Run("DSP analysis: asynchronous failure actively notifies without polling", () =>
        {
            using var effects = new PcmEffects(44100, 2);
            effects.SetFile(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".wav"), 0, 0);
            using var completed = new ManualResetEventSlim();
            effects.StateChanged += () =>
            {
                if (effects.GetState(0, false).Loudness is LoudnessStatus.Failed or LoudnessStatus.Unavailable)
                    completed.Set();
            };
            effects.Configure(new() { NormalizeLoudness = true });
            Require(completed.Wait(5000), "analysis completion did not raise state notification");
        });
    }
}
