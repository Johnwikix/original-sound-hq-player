using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.Advance;
using Windows.Foundation;

internal static class ZeroDurationChecks
{
    internal static void Run()
    {
        var character = new RenderLyricsChar(new Rect(0, 0, 10, 10))
        {
            StartMs = 1500,
            EndMs = 1500,
        };

        Check(character.GetPlayProgress(1499) == 0, "A zero-duration character must remain unplayed before its start.");
        Check(character.GetPlayProgress(1500) == 1, "A zero-duration character must be complete at its start boundary.");
        Check(character.GetPlayProgress(1501) == 1, "A zero-duration character must remain complete after its start.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
