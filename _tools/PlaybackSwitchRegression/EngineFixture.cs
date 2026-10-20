namespace AudioPlayer.Playback;

public sealed partial class PlaybackEngine
{
    // Test assembly only: keep production field initializers, without IPC, endpoint listeners or a watchdog.
    // A typed constructor also avoids reflection-created generic dictionaries losing their constructors in NativeAOT.
    internal PlaybackEngine()
    {
        _ipc = null!;
        _fftAnalyzer = new FftAnalyzer(static _ => { });
    }
}
