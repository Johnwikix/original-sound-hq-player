using AnimatedWin2dControls.Shaders;
using ComputeSharp;
using ComputeSharp.D2D1.WinUI;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using System;
using System.Numerics;

namespace AnimatedWin2dControls.Renderer
{
    public sealed class FogRenderer : BreathingRendererBase, IDisposable
    {
        private PixelShaderEffect<FogEffect>? _fogEffect;
        private float _timeAccumulator = 0f;

        public bool IsEnabled { get; set; } = false;

        public void LoadResources()
        {
            Dispose();
            _fogEffect = new PixelShaderEffect<FogEffect>();
        }

        public void Update(double deltaTime)
        {
            if (_fogEffect == null || !IsEnabled) return;
            base.UpdateBreathing(0f, 0);
            _timeAccumulator += (float)deltaTime;
        }

        public void Draw(ICanvasAnimatedControl control, CanvasDrawingSession ds)
        {
            if (_fogEffect == null || !IsEnabled) return;

            float width = control.ConvertDipsToPixels((float)control.Size.Width, CanvasDpiRounding.Round);
            float height = control.ConvertDipsToPixels((float)control.Size.Height, CanvasDpiRounding.Round);

            _fogEffect.ConstantBuffer = new FogEffect(
                 _timeAccumulator,
                 new float2(width, height)
             );

            ds.DrawImage(_fogEffect);
        }

        public void Dispose()
        {
            _fogEffect?.Dispose();
            _fogEffect = null;
        }
    }
}
