using AnimatedWin2dControls.Shaders;
using ComputeSharp;
using ComputeSharp.D2D1.WinUI;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using System;
using System.Numerics;
using System.Threading;

namespace AnimatedWin2dControls.Renderer
{
    public sealed class RaindropRenderer : BreathingRendererBase, IDisposable
    {
        // 保护 _raindropEffect 生命周期:Dispose/LoadResources 持锁,Draw 走 TryEnter(0) 抢不到就丢一帧,渲染线程永不阻塞。
        private readonly object _gate = new();
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
            lock (_gate)
            {
                _raindropEffect?.Dispose();
                _raindropEffect = new PixelShaderEffect<RaindropEffect>();
            }
        }

        public void Update(double deltaTime)
        {
            if (_raindropEffect == null || !IsEnabled) return;
            base.UpdateBreathing(0f, 0);
            _timeAccumulator += (float)deltaTime;
        }

        public void Draw(ICanvasAnimatedControl control, CanvasDrawingSession ds)
        {
            if (!IsEnabled) return;
            if (!Monitor.TryEnter(_gate, 0)) return;
            try
            {
                var effect = _raindropEffect;
                if (effect == null) return;

                float width = control.ConvertDipsToPixels((float)control.Size.Width, CanvasDpiRounding.Round);
                float height = control.ConvertDipsToPixels((float)control.Size.Height, CanvasDpiRounding.Round);

                effect.ConstantBuffer = new RaindropEffect(
                    _timeAccumulator,
                    new float2(width, height),
                    RainSpeed,
                    RainSize,
                    RainDensity,
                    LightAngle,
                    ShadowIntensity
                );

                ds.DrawImage(effect);
            }
            finally { Monitor.Exit(_gate); }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _raindropEffect?.Dispose();
                _raindropEffect = null;
            }
        }
    }
}
