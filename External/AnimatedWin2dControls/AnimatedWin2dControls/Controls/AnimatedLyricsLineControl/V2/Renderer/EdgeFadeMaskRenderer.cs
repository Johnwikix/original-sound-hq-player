using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using System;
using System.Linq;
using System.Numerics;
using Windows.Foundation;
using Windows.UI;

namespace AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.V2
{
    public class EdgeFadeMaskRenderer : IDisposable
    {
        private CanvasCommandList? _maskCommandList;
        private CanvasImageBrush? _maskBrush;

        private Rect _lastBounds;
        private CanvasGradientStop[]? _lastStops;
        private bool _lastIsVertical;

        public CanvasImageBrush? Brush => _maskBrush;

        public void Update(ICanvasResourceCreator resourceCreator, Rect targetRect, CanvasGradientStop[] stops, bool isVertical = true)
        {
            if (Math.Abs(_lastBounds.X - targetRect.X) < 0.1f &&
                Math.Abs(_lastBounds.Y - targetRect.Y) < 0.1f &&
                Math.Abs(_lastBounds.Width - targetRect.Width) < 0.1f &&
                Math.Abs(_lastBounds.Height - targetRect.Height) < 0.1f &&
                _lastIsVertical == isVertical &&
                AreStopsEqual(_lastStops, stops) &&
                _maskBrush != null)
            {
                return;
            }

            _maskBrush?.Dispose();
            _maskCommandList?.Dispose();
            _maskCommandList = new CanvasCommandList(resourceCreator);

            float width = (float)targetRect.Width;
            float height = (float)targetRect.Height;
            float startX = (float)targetRect.X;
            float startY = (float)targetRect.Y;

            using (var ds = _maskCommandList.CreateDrawingSession())
            {
                ds.Clear(Color.FromArgb(0, 0, 0, 0));

                Vector2 startPoint = new(0, 0);
                Vector2 endPoint = isVertical ? new Vector2(0, height) : new Vector2(width, 0);

                using var multiStopBrush = new CanvasLinearGradientBrush(resourceCreator, stops)
                {
                    StartPoint = startPoint,
                    EndPoint = endPoint
                };

                ds.FillRectangle(0, 0, width, height, multiStopBrush);
            }

            _maskBrush = new CanvasImageBrush(resourceCreator, _maskCommandList)
            {
                SourceRectangle = new Rect(0, 0, width, height),
                Transform = Matrix3x2.CreateTranslation(startX, startY)
            };

            _lastBounds = targetRect;
            _lastIsVertical = isVertical;
            _lastStops = stops;
        }

        private static bool AreStopsEqual(CanvasGradientStop[]? a, CanvasGradientStop[]? b)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (Math.Abs(a[i].Position - b[i].Position) > 0.001f || a[i].Color != b[i].Color)
                    return false;
            }
            return true;
        }

        public void Dispose()
        {
            _maskBrush?.Dispose();
            _maskCommandList?.Dispose();
        }
    }
}
