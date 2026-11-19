using AnimatedWin2dControls.Controls;

namespace WinUIMusicPlayer.Services;

internal sealed class PlaybackSpectrumSource(BassPlayerCommandService player) : IPlaybackSpectrumSource
{
    public void SetActive(bool active) => player.SetFftEnabled(FftConsumer.PlaybackSpectrum, active);

    public bool TryRead(out PlaybackSpectrumFrame frame)
    {
        var snapshot = player.ReadFftSnapshot(out long receivedAtMs);
        if (snapshot is null)
        {
            frame = default;
            return false;
        }

        frame = new(snapshot.Sequence, snapshot.Epoch, receivedAtMs, snapshot.SampleRate, snapshot.FftSize, snapshot.Left, snapshot.Right);
        return true;
    }
}
