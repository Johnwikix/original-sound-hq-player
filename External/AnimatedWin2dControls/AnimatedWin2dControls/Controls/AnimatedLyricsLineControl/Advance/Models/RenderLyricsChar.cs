#nullable enable
using Microsoft.Graphics.Canvas.Effects;
using System;
using Windows.Foundation;

namespace AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.Advance
{
    public class RenderLyricsChar : BaseRenderLyrics
    {
        public Rect LayoutRect { get; set; }

        public ValueTransition<double> ScaleTransition { get; set; }
        public ValueTransition<double> GlowTransition { get; set; }
        public ValueTransition<double> FloatTransition { get; set; }

        /// <summary>Captures the outgoing glow radius so its opacity can fade from the current image state.</summary>
        public double GlowExitStartAmount { get; internal set; }

        private CropEffect? _crop;
        private GaussianBlurEffect? _glow;

        /// <summary>Gets the cached crop effect when this character first needs native drawing.</summary>
        public CropEffect Crop => _crop ??= new CropEffect { BorderMode = EffectBorderMode.Hard };

        /// <summary>Gets the cached glow effect when glow or retirement blur is actually drawn.</summary>
        public GaussianBlurEffect Glow => _glow ??= new GaussianBlurEffect { Source = Crop, BorderMode = EffectBorderMode.Soft };

        public double ProgressPlayed { get; set; }

        public RenderLyricsChar(Rect layoutRect)
        {
            ScaleTransition = new(1.0, EasingHelper.GetInterpolatorByEasingType<double>(EasingType.Sine), 0.3);
            GlowTransition = new(0, EasingHelper.GetInterpolatorByEasingType<double>(EasingType.Sine), 1.0);
            FloatTransition = new(0, EasingHelper.GetInterpolatorByEasingType<double>(EasingType.Sine), 0.45);
            LayoutRect = layoutRect;
        }

        public void Update(TimeSpan elapsedTime)
        {
            ScaleTransition.Update(elapsedTime);
            GlowTransition.Update(elapsedTime);
            FloatTransition.Update(elapsedTime);
        }

        public void DisposeEffects()
        {
            // 只退休已经创建的效果；设备/布局缓存重建允许同一字符再次惰性创建。
            var glow = _glow;
            var crop = _crop;
            _glow = null;
            _crop = null;
            try { glow?.Dispose(); }
            finally { crop?.Dispose(); }
        }
    }
}
