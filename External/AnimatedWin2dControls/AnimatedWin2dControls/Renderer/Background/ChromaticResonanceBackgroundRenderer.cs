using AnimatedWin2dControls.Shaders.Background;
using ComputeSharp;
using ComputeSharp.D2D1.WinUI;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI.Xaml;
using System;
using System.Threading;

namespace AnimatedWin2dControls.Renderer.Background
{
    /// <summary>
    /// 彩虹流光线背景 (Chromatic Resonance 1:1 移植，署名归原作者 Philippe Desgranges)。
    /// 4 色调色板作为 hue 端点，shader 内用 sin 连续映射 t∈[0,1] 以避免 frac 跳变产生的垂直扫描线。
    /// musicReaction 默认 0，由 <see cref="MusicReaction"/> 属性从外部音源（若有）注入。
    /// </summary>
    public sealed class ChromaticResonanceBackgroundRenderer : BaseBackgroundRenderer
    {
        // 保护 _effect 生命周期：Dispose/LoadResources 持锁，Draw 走 TryEnter(0) 抢不到就丢一帧。
        private readonly object _gate = new();
        private PixelShaderEffect<ChromaticResonanceEffect>? _effect;

        public float MusicReaction { get; set; } = 0f;

        public override void LoadResources()
        {
            lock (_gate)
            {
                _effect?.Dispose();
                _effect = new PixelShaderEffect<ChromaticResonanceEffect>();
            }
            if (CurrentPalette is not null) SetPalette(CurrentPalette);
            else ApplyDefaultColors();
            SnapToTarget();
        }

        public override void Update(TimeSpan deltaTime)
        {
            UpdateBreathing(0f, 0);
            Advance(deltaTime);
        }

        public override void Draw(ICanvasAnimatedControl control, CanvasDrawingSession ds)
        {
            if (!Monitor.TryEnter(_gate, 0)) return;
            try
            {
                var effect = _effect;
                if (effect == null || Opacity <= 0) return;

                float width = control.ConvertDipsToPixels((float)control.Size.Width, CanvasDpiRounding.Round);
                float height = control.ConvertDipsToPixels((float)control.Size.Height, CanvasDpiRounding.Round);

                effect.ConstantBuffer = new ChromaticResonanceEffect(
                    Time,
                    new float2(width, height),
                    new float3(C1.X, C1.Y, C1.Z),
                    new float3(C2.X, C2.Y, C2.Z),
                    new float3(C3.X, C3.Y, C3.Z),
                    new float3(C4.X, C4.Y, C4.Z),
                    MusicReaction,
                    IsDark);

                if (Opacity >= 1.0) ds.DrawImage(effect);
                else
                {
                    using var opacityEffect = new OpacityEffect
                    {
                        Source = effect,
                        Opacity = (float)Opacity
                    };
                    ds.DrawImage(opacityEffect);
                }
            }
            finally { Monitor.Exit(_gate); }
        }

        public override void Dispose()
        {
            lock (_gate)
            {
                _effect?.Dispose();
                _effect = null;
            }
        }
    }
}