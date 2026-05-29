using Microsoft.Graphics.Canvas.Effects;
using System;
using Windows.Foundation;

namespace AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.V2
{
    public class RenderLyricsChar : BaseRenderLyrics
    {
        public Rect LayoutRect { get; set; }

        public ValueTransition<double> ScaleTransition { get; set; }
        public ValueTransition<double> GlowTransition { get; set; }
        public ValueTransition<double> FloatTransition { get; set; }

        public CropEffect Crop { get; }
        public GaussianBlurEffect Glow { get; }

        public double ProgressPlayed { get; set; }

        public RenderLyricsChar(Rect layoutRect)
        {
            ScaleTransition = new(1.0, EasingHelper.GetInterpolatorByEasingType<double>(EasingType.Sine), 0.3);
            GlowTransition = new(0, EasingHelper.GetInterpolatorByEasingType<double>(EasingType.Sine), 1.0);
            FloatTransition = new(0, EasingHelper.GetInterpolatorByEasingType<double>(EasingType.Sine), 0.45);
            LayoutRect = layoutRect;
            Crop = new CropEffect { BorderMode = EffectBorderMode.Hard };
            Glow = new GaussianBlurEffect { Source = Crop, BorderMode = EffectBorderMode.Soft };
        }

        public void Update(TimeSpan elapsedTime)
        {
            ScaleTransition.Update(elapsedTime);
            GlowTransition.Update(elapsedTime);
            FloatTransition.Update(elapsedTime);
        }

        public void DisposeEffects()
        {
            Crop?.Dispose();
            Glow?.Dispose();
        }
    }
}
