using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using System;

namespace AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.Advance
{
    public class RenderLyricsRegion : IDisposable
    {
        public CanvasGradientStop[] FillStops { get; } = new CanvasGradientStop[4];
        public CanvasGradientStop[] StrokeStops { get; } = new CanvasGradientStop[4];

        public AlphaMaskEffect FinalFillEffect { get; }
        public AlphaMaskEffect? FinalStrokeEffect { get; }
        public CompositeEffect? CombinedEffect { get; }

        public CanvasCommandList? PrevFillLayer;
        public CanvasCommandList? PrevStrokeLayer;
        public CropEffect? FillCrop;
        public CanvasLinearGradientBrush? CachedFillGradientBrush;

        public RenderLyricsRegion(ICanvasImage cachedFill, ICanvasImage? cachedStroke)
        {
            FinalFillEffect = new AlphaMaskEffect { AlphaMask = cachedFill };

            if (cachedStroke != null)
            {
                FinalStrokeEffect = new AlphaMaskEffect { AlphaMask = cachedStroke };
                CombinedEffect = new CompositeEffect
                {
                    Sources = { FinalStrokeEffect, FinalFillEffect },
                    Mode = CanvasComposite.SourceOver
                };
            }
        }

        public void Dispose()
        {
            CachedFillGradientBrush?.Dispose();
            CachedFillGradientBrush = null;
            PrevFillLayer?.Dispose();
            PrevFillLayer = null;
            PrevStrokeLayer?.Dispose();
            PrevStrokeLayer = null;
            FillCrop?.Dispose();
            FillCrop = null;
            FinalFillEffect?.Dispose();
            FinalStrokeEffect?.Dispose();
            CombinedEffect?.Dispose();
        }
    }
}
