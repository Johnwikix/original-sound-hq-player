namespace AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.Advance
{
    /// <summary>
    /// Defines one scroll entrance's timing for both displacement and line effects.
    /// </summary>
    public readonly struct LyricScrollTiming
    {
        /// <summary>Gets the configured response duration in seconds.</summary>
        public double DurationSeconds { get; }

        /// <summary>Gets the interval to the next lyric entrance in seconds.</summary>
        public double IntervalSeconds { get; }

        /// <summary>Gets the first visible line index, or -1 when staggering is disabled.</summary>
        public int WaveOriginIndex { get; }

        /// <summary>Gets a value that indicates whether the response preserves spring stiffness.</summary>
        public bool IsSpring { get; }

        /// <summary>Initializes a new instance of the <see cref="LyricScrollTiming"/> struct.</summary>
        /// <param name="durationSeconds">The configured response duration in seconds.</param>
        /// <param name="intervalSeconds">The interval to the next entrance, or zero when unknown.</param>
        /// <param name="waveOriginIndex">The first visible line index, or -1 to disable staggering.</param>
        /// <param name="isSpring"><see langword="true"/> to retain spring stiffness; otherwise, <see langword="false"/>.</param>
        public LyricScrollTiming(double durationSeconds, double intervalSeconds, int waveOriginIndex, bool isSpring)
        {
            DurationSeconds = durationSeconds;
            IntervalSeconds = intervalSeconds;
            WaveOriginIndex = waveOriginIndex;
            IsSpring = isSpring;
        }

        /// <summary>Calculates the shared response duration and startup delay for a line.</summary>
        /// <param name="index">The lyric line index.</param>
        /// <returns>The response duration and startup delay in seconds.</returns>
        public (double Duration, double Delay) GetLineTiming(int index)
        {
            // 值类型时序与标量计算，沿用项目 .NET 版本，不为逐帧调度创建集合或闭包。
            return WaveOriginIndex >= 0
                ? LyricScrollMotion.FlowWaveTiming(index, WaveOriginIndex, DurationSeconds, IntervalSeconds, IsSpring)
                : (DurationSeconds, 0);
        }
    }
}
