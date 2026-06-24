using AnimatedWin2dControls.Renderer.Background;
using AnimatedWin2dControls.Shaders.Background;
using ComputeSharp;
using ComputeSharp.D2D1.WinUI;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI.Xaml;
using System;

namespace AnimatedWin2dControls.Renderer.Background
{
    /// <summary>
    /// PS3 XMB 伪星空背景。HLSL 内含 100 次 raymarch + dust + 4 色梯度 lerp。
    /// </summary>
    public sealed class PS3XMBBackgroundRenderer : BaseBackgroundRenderer
    {
        private PixelShaderEffect<PS3XMBEffect>? _effect;

        public override void LoadResources()
        {
            Dispose();
            _effect = new PixelShaderEffect<PS3XMBEffect>();
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
            if (_effect == null || Opacity <= 0) return;

            float width = control.ConvertDipsToPixels((float)control.Size.Width, CanvasDpiRounding.Round);
            float height = control.ConvertDipsToPixels((float)control.Size.Height, CanvasDpiRounding.Round);

            _effect.ConstantBuffer = new PS3XMBEffect(
                Time,
                new float2(width, height),
                new float3(C1.X, C1.Y, C1.Z),
                new float3(C2.X, C2.Y, C2.Z),
                new float3(C3.X, C3.Y, C3.Z),
                new float3(C4.X, C4.Y, C4.Z));

            if (Opacity >= 1.0) ds.DrawImage(_effect);
            else
            {
                using var opacityEffect = new OpacityEffect
                {
                    Source = _effect,
                    Opacity = (float)Opacity
                };
                ds.DrawImage(opacityEffect);
            }
        }

        public override void Dispose()
        {
            _effect?.Dispose();
            _effect = null;
        }
    }
}
