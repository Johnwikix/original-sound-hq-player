namespace AnimatedWin2dControls
{
    /// <summary>
    /// 背景着色器选择。对应 SaveSettings.BackgroundShader 整型索引。
    /// 0 保留给 Fluid 以保证旧版本设置文件向前兼容。
    /// 老版本曾有 SeventiesMelt=1 / Cosmic=2 / PS3XMB=3 / GradientFlow=4 / WavyBackground=5，
    /// 加载时由 <see cref="SaveSettings"/> 侧的迁移函数映射到新编号，避免索引与 ComboBox 错位。
    /// ChromaticResonance=4 为新追加，索引在尾部不破坏旧值。
    /// </summary>
    public enum BackgroundShaderMode : byte
    {
        FluidBackground = 0,
        PS3XMB = 1,
        GradientFlow = 2,
        WavyBackground = 3,
        ChromaticResonance = 4,
    }
}
