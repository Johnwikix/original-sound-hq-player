using AnimatedWin2dControls.Shaders;
using ComputeSharp;
using ComputeSharp.D2D1.WinUI;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using System;
using System.Numerics;

namespace AnimatedWin2dControls.Renderer
{
    public sealed class SnowRenderer : BreathingRendererBase, IDisposable
    {
        private PixelShaderEffect<SnowEffect>? _snowEffect;
        private float _timeAccumulator = 0f;

        public bool IsEnabled { get; set; } = false;
        public float Amount { get; set; } = 0.5f;
        public float Speed { get; set; } = 1.0f;

        public void LoadResources()
        {
            Dispose();
            _snowEffect = new PixelShaderEffect<SnowEffect>();
        }

        public void Update(double deltaTime)
        {
            if (_snowEffect == null || !IsEnabled) return;
            base.UpdateBreathing(0f, 0);
            _timeAccumulator += (float)deltaTime;
        }

        public void Draw(ICanvasAnimatedControl control, CanvasDrawingSession ds)
        {
            if (_snowEffect == null || !IsEnabled) return;

            float width = control.ConvertDipsToPixels((float)control.Size.Width, CanvasDpiRounding.Round);
            float height = control.ConvertDipsToPixels((float)control.Size.Height, CanvasDpiRounding.Round);

            _snowEffect.ConstantBuffer = new SnowEffect(
                _timeAccumulator,
                new float2(width, height),
                Amount,
                Speed
            );

            ds.DrawImage(_snowEffect);
        }

        public void Dispose()
        {
            _snowEffect?.Dispose();
            _snowEffect = null;
        }
    }
}
