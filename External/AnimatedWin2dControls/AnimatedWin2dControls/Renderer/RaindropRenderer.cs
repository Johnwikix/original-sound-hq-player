using AnimatedWin2dControls.Shaders;
using ComputeSharp;
using ComputeSharp.D2D1.WinUI;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using System;
using System.Numerics;

namespace AnimatedWin2dControls.Renderer
{
    public sealed class RaindropRenderer : BreathingRendererBase, IDisposable
    {
        private PixelShaderEffect<RaindropEffect>? _raindropEffect;
        private float _timeAccumulator = 0f;

        public bool IsEnabled { get; set; } = false;
        public float RainSpeed { get; set; } = 1.0f;
        public float RainSize { get; set; } = 1.0f;
        public float RainDensity { get; set; } = 0.5f;
        public float LightAngle { get; set; } = MathF.PI * 0.25f;
        public float ShadowIntensity { get; set; } = 0.5f;

        public void LoadResources()
        {
            Dispose();
            _raindropEffect = new PixelShaderEffect<RaindropEffect>();
        }

        public void Update(double deltaTime)
        {
            if (_raindropEffect == null || !IsEnabled) return;
            base.UpdateBreathing(0f, 0);
            _timeAccumulator += (float)deltaTime;
        }

        public void Draw(ICanvasAnimatedControl control, CanvasDrawingSession ds)
        {
            if (_raindropEffect == null || !IsEnabled) return;

            float width = control.ConvertDipsToPixels((float)control.Size.Width, CanvasDpiRounding.Round);
            float height = control.ConvertDipsToPixels((float)control.Size.Height, CanvasDpiRounding.Round);

            _raindropEffect.ConstantBuffer = new RaindropEffect(
                _timeAccumulator,
                new float2(width, height),
                RainSpeed,
                RainSize,
                RainDensity,
                LightAngle,
                ShadowIntensity
            );

            ds.DrawImage(_raindropEffect);
        }

        public void Dispose()
        {
            _raindropEffect?.Dispose();
            _raindropEffect = null;
        }
    }
}
