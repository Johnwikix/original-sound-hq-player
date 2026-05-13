namespace AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.V2
{
    public struct Keyframe<T>
    {
        public T Value { get; }
        public double Duration { get; }

        public Keyframe(T value, double durationSeconds)
        {
            Value = value;
            Duration = durationSeconds;
        }
    }
}
