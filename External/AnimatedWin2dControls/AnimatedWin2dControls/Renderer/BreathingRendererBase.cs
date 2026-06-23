using Microsoft.Graphics.Canvas;
using System.Numerics;

namespace AnimatedWin2dControls.Renderer
{
    /// <summary>
    /// 呼吸缩放基类。bassEnergy 传 0 时退化为恒等（不脉动）。
    /// </summary>
    public abstract class BreathingRendererBase
    {
        protected float _currentScale = 1.0f;
        private float _targetScale = 1.0f;

        /// <summary>
        /// 根据低音能量更新呼吸缩放值。
        /// </summary>
        /// <param name="bassEnergy">低音能量 (0.0 - 1.0)</param>
        /// <param name="intensity">呼吸强度 (0 - 100)</param>
        public virtual void UpdateBreathing(float bassEnergy, int intensity)
        {
            if (intensity <= 0)
            {
                _currentScale = 1.0f;
                return;
            }

            float maxScaleOffset = intensity / 100.0f;
            _targetScale = 1.0f + (bassEnergy * maxScaleOffset);

            if (_targetScale > _currentScale)
            {
                _currentScale += (_targetScale - _currentScale) * 0.2f;
            }
            else
            {
                _currentScale += (_targetScale - _currentScale) * 0.05f;
            }
        }

        protected void ApplyBreathingTransform(CanvasDrawingSession ds, Vector2 center, bool isEnabled)
        {
            if (isEnabled && _currentScale > 1.0f)
            {
                ds.Transform = Matrix3x2.CreateScale(_currentScale, center);
            }
        }

        protected static void ResetTransform(CanvasDrawingSession ds, bool isEnabled)
        {
            if (isEnabled)
            {
                ds.Transform = Matrix3x2.Identity;
            }
        }
    }
}
