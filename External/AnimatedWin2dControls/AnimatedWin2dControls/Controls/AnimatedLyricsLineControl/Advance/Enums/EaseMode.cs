namespace AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.Advance
{
    public enum EaseMode
    {
        In,
        Out,
        InOut,
        Continuous,
        // Per-line stagger; the scalar curve uses Out. Scheduled by the lyrics coordinator.
        FlowWave,
    }
}
