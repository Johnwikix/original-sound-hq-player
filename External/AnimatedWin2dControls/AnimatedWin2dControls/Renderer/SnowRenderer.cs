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
    public sealed class SnowRenderer : BreathingRendererBase, IDisposable
    {
        // 保护 _snowEffect 生命周期:Dispose/LoadResources 持锁,Draw 走 TryEnter(0) 抢不到就丢一帧,渲染线程永不阻塞。
        private readonly object _gate = new();
        private PixelShaderEffect<SnowEffect>? _snowEffect;
        private float _timeAccumulator = 0f;

        public bool IsEnabled { get; set; } = false;
        public float Amount { get; set; } = 0.05f;
        public float Speed { get; set; } = 1.0f;

        public void LoadResources()
        {
            lock (_gate)
            {
                _snowEffect?.Dispose();
                _snowEffect = new PixelShaderEffect<SnowEffect>();
            }
        }

        public void Update(double deltaTime)
        {
            if (_snowEffect == null || !IsEnabled) return;
            base.UpdateBreathing(0f, 0);
            _timeAccumulator += (float)deltaTime;
        }

        public void Draw(ICanvasAnimatedControl control, CanvasDrawingSession ds)
        {
            if (!IsEnabled) return;
            if (!Monitor.TryEnter(_gate, 0)) return;
            try
            {
                var effect = _snowEffect;
                if (effect == null) return;

                float width = control.ConvertDipsToPixels((float)control.Size.Width, CanvasDpiRounding.Round);
                float height = control.ConvertDipsToPixels((float)control.Size.Height, CanvasDpiRounding.Round);

                effect.ConstantBuffer = new SnowEffect(
                    _timeAccumulator,
                    new float2(width, height),
                    Amount,
                    Speed
                );

                ds.DrawImage(effect);
            }
            finally { Monitor.Exit(_gate); }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _snowEffect?.Dispose();
                _snowEffect = null;
            }
        }
    }
}
