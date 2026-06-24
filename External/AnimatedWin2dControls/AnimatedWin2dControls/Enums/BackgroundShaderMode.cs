namespace AnimatedWin2dControls
{
    /// <summary>
    /// 背景着色器选择。对应 SaveSettings.BackgroundShader 整型索引。
    /// 0 保留给 Fluid 以保证旧版本设置文件向前兼容。
    /// 注：原 1 (SeventiesMelt) / 2 (Cosmic) 已废弃，但仍保留枚举空位以避免破坏历史保存值。
    /// </summary>
    public enum BackgroundShaderMode : byte
    {
        FluidBackground = 0,
        // 1 = SeventiesMelt (已废弃)
        // 2 = Cosmic (已废弃)
        PS3XMB = 3,
        GradientFlow = 4,
        WavyBackground = 5,
    }
}
