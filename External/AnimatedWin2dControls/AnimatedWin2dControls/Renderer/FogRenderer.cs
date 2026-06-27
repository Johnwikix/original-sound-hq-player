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
    public sealed class FogRenderer : BreathingRendererBase, IDisposable
    {
        // 保护 _fogEffect 生命周期:Dispose/LoadResources 持锁,Draw 走 TryEnter(0) 抢不到就丢一帧,渲染线程永不阻塞。
        private readonly object _gate = new();
        private PixelShaderEffect<FogEffect>? _fogEffect;
        private float _timeAccumulator = 0f;

        public bool IsEnabled { get; set; } = false;

        public void LoadResources()
        {
            lock (_gate)
            {
                _fogEffect?.Dispose();
                _fogEffect = new PixelShaderEffect<FogEffect>();
            }
        }

        public void Update(double deltaTime)
        {
            if (_fogEffect == null || !IsEnabled) return;
            base.UpdateBreathing(0f, 0);
            _timeAccumulator += (float)deltaTime;
        }

        public void Draw(ICanvasAnimatedControl control, CanvasDrawingSession ds)
        {
            if (!IsEnabled) return;
            if (!Monitor.TryEnter(_gate, 0)) return;
            try
            {
                var effect = _fogEffect;
                if (effect == null) return;

                float width = control.ConvertDipsToPixels((float)control.Size.Width, CanvasDpiRounding.Round);
                float height = control.ConvertDipsToPixels((float)control.Size.Height, CanvasDpiRounding.Round);

                effect.ConstantBuffer = new FogEffect(
                     _timeAccumulator,
                     new float2(width, height)
                 );

                ds.DrawImage(effect);
            }
            finally { Monitor.Exit(_gate); }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _fogEffect?.Dispose();
                _fogEffect = null;
            }
        }
    }
}
