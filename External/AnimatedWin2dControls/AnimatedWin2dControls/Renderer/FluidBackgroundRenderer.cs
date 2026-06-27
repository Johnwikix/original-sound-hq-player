using AnimatedWin2dControls.Renderer.Background;
using AnimatedWin2dControls.Shaders;
using ComputeSharp;
using ComputeSharp.D2D1.WinUI;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI.Xaml;
using System;
using System.Threading;

namespace AnimatedWin2dControls.Renderer
{
    /// <summary>
    /// 流体背景渲染器。基类 <see cref="BaseBackgroundRenderer"/> 管 4 色 + 调色板 + 过渡，
    /// 本类负责装配 <see cref="FluidBackgroundEffect"/> 并处理 LightWave 子特效。
    /// </summary>
    public sealed class FluidBackgroundRenderer : BaseBackgroundRenderer
    {
        // 保护 _effect 生命周期:Dispose/LoadResources 持锁,Draw 走 TryEnter(0) 抢不到就丢一帧,渲染线程永不阻塞。
        private readonly object _gate = new();
        private PixelShaderEffect<FluidBackgroundEffect>? _effect;

        private float _rnd1 = (float)(Rng.NextDouble() * Math.PI * 2);
        private float _rnd2 = (float)(Rng.NextDouble() * Math.PI * 2);
        private float _rnd3 = (float)(Rng.NextDouble() * Math.PI * 2);

        public override void LoadResources()
        {
            lock (_gate)
            {
                _effect?.Dispose();
                _effect = new PixelShaderEffect<FluidBackgroundEffect>();
            }

            if (CurrentPalette is not null)
                SetPalette(CurrentPalette);
            else
                ApplyDefaultColors();

            SnapToTarget();
        }

        public override void Update(TimeSpan deltaTime)
        {
            // bass 恒为 0，呼吸退化为恒等
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

                effect.ConstantBuffer = new FluidBackgroundEffect(
                    new float2(width, height),
                    Time,
                    new float3(C1.X, C1.Y, C1.Z),
                    new float3(C2.X, C2.Y, C2.Z),
                    new float3(C3.X, C3.Y, C3.Z),
                    new float3(C4.X, C4.Y, C4.Z),
                    _rnd1, _rnd2, _rnd3,
                    useHSVBlending: false,
                    enableLightWave: EnableLightWave,
                    enableDithering: true);

                if (Opacity >= 1.0)
                {
                    ds.DrawImage(effect);
                }
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
