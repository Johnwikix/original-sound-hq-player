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
    public sealed class MorphingAbstractBackgroundRenderer : BaseBackgroundRenderer
    {
        private readonly object _gate = new();
        private PixelShaderEffect<MorphingAbstractEffect>? _effect;

        public override void LoadResources()
        {
            lock (_gate)
            {
                _effect?.Dispose();
                _effect = new PixelShaderEffect<MorphingAbstractEffect>();
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

                effect.ConstantBuffer = new MorphingAbstractEffect(
                    Time,
                    new float2(width, height),
                    new float3(C1.X, C1.Y, C1.Z),
                    new float3(C2.X, C2.Y, C2.Z),
                    new float3(C3.X, C3.Y, C3.Z),
                    new float3(C4.X, C4.Y, C4.Z));

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
