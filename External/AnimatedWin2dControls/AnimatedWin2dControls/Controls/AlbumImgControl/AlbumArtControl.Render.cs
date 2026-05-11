

using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI.Xaml;
using System;
using System.Numerics;
using Windows.Foundation;

namespace AnimatedWin2dControls.Controls.AlbumImgControl
{
    public sealed partial class AlbumArtControl
    {
        // ── Canvas Draw 事件 ──────────────────────────────────────────────────

        private void Canvas_Draw(CanvasControl sender, CanvasDrawEventArgs e)
        {
            if (_lastDpi == 0f) {
                _lastDpi = e.DrawingSession.Dpi;
            }
            ConsumeDecodeChannel(sender, _lastDpi);

            if (_isFading)
                TickAnimation();

            DrawFrame(e.DrawingSession, sender);

            if (_isFading)
                sender.Invalidate();
        }

        // ── 解码通道消费 ──────────────────────────────────────────────────────

        private void ConsumeDecodeChannel(CanvasControl sender,float dpi)
        {
            if (!_decodeChannel.Reader.TryRead(out var item)) return;

            var (frame, req) = item;
            _aspectRatio = (float)frame.W / frame.H;
            _desiredSize = new Size(frame.W, frame.H);
            _canvas?.DispatcherQueue.TryEnqueue(() =>
            {
                InvalidateMeasure();
            });
            float cw = req.ContentW, ch = req.ContentH;
            if (cw <= 0 || ch <= 0)
            {
                ComputeContentRect((float)sender.Size.Width, (float)sender.Size.Height);
                cw = (float)_contentRect.Width;
                ch = (float)_contentRect.Height;
            }
            if (cw <= 0 || ch <= 0)
            {
                ReleaseAnimLockIfNeeded(req.IsResize);
                return;
            }

            var bmp = GpuBake(frame, cw, ch, req.Shadow, dpi, sender);
            if (bmp == null)
            {
                ReleaseAnimLockIfNeeded(req.IsResize);
                return;
            }

            float pad = req.Shadow ? ShadowPad : 0f;
            ApplyNewBitmap(bmp, new FrameInfo(frame.W, frame.H, pad), req.IsResize);
        }

        private void ReleaseAnimLockIfNeeded(bool isResize)
        {
            if (isResize || !_animLock) return;
            _animLock = false;
            CheckPendingAfterUnlock();
        }

        // ── 位图应用 ──────────────────────────────────────────────────────────

        private void ApplyNewBitmap(CanvasBitmap bmp, FrameInfo info, bool isResize)
        {
            if (isResize)
            {
                FinishFadeImmediately();
                _currentBmp?.Dispose();
                _currentBmp  = bmp;
                _currentInfo = info;
                _canvas?.Invalidate();
                return;
            }

            FinishFadeImmediately();
            _nextBmp  = bmp;
            _nextInfo = info;
            BeginTransition();
        }

        private void FinishFadeImmediately()
        {
            if (!_isFading) return;

            if (_nextBmp != null)
            {
                _currentBmp?.Dispose();
                _currentBmp  = _nextBmp;
                _currentInfo = _nextInfo;
                _nextBmp     = null;
                // 强制结束时同步 _currentDisplayHash，防止 dedup 误判
                _currentDisplayHash = _lastHash;
            }

            _isFading      = false;
            _t             = 0f;
            _lastDrawTicks = 0;
        }

        // ── 帧绘制 ────────────────────────────────────────────────────────────

        private void DrawFrame(CanvasDrawingSession ds, CanvasControl sender)
        {
            if (!IsActive) return;

            float cw = (float)sender.Size.Width;
            float ch = (float)sender.Size.Height;
            if (cw <= 0 || ch <= 0) return;

            ComputeContentRect(cw, ch);
            if (_contentRect == Rect.Empty) return;

            if (!_isFading)
            {
                if (_currentBmp != null)
                    DrawBitmap(ds, _currentBmp, _currentInfo, alpha: 1f, scale: 1f);
                return;
            }

            float t = _t;
            if (t < 0.5f)
            {
                float e = EaseIn(t / 0.5f);
                if (_currentBmp != null)
                    DrawBitmap(ds, _currentBmp, _currentInfo,
                        alpha: 1f - e,
                        scale: 1f - (1f - ScaleSmall) * e);
            }
            else
            {
                float e = EaseOut((t - 0.5f) / 0.5f);
                if (_currentBmp != null)
                    DrawBitmap(ds, _currentBmp, _currentInfo,
                        alpha: e,
                        scale: ScaleSmall + (1f - ScaleSmall) * e);
            }
        }

        private void DrawBitmap(CanvasDrawingSession ds, CanvasBitmap bmp,
            FrameInfo info, float alpha, float scale)
        {
            var contentDest = CalcAspectFitRect(info.SrcW, info.SrcH, _contentRect);
            if (contentDest.Width <= 0 || contentDest.Height <= 0) return;

            double cx   = contentDest.X + contentDest.Width  * 0.5;
            double cy   = contentDest.Y + contentDest.Height * 0.5;
            double bmpW = (contentDest.Width  + info.Pad * 2) * scale;
            double bmpH = (contentDest.Height + info.Pad * 2) * scale;
            var dest = new Rect(cx - bmpW * 0.5, cy - bmpH * 0.5, bmpW, bmpH);

            ds.DrawImage(bmp, dest, bmp.Bounds, alpha, CanvasImageInterpolation.Linear);
        }

        // ── GPU Bake ──────────────────────────────────────────────────────────

        /// <summary>
        /// 在 GPU 上完成缩放、圆角裁剪，并可选地叠加投影，
        /// 烘焙成最终的 <see cref="CanvasBitmap"/>（RenderTarget）供绘制使用。
        ///
        /// <para>
        /// 注意：scaledRt 的生命周期须延伸到 finalRt 的 DrawImage 调用之后，
        /// 因为 shadowEffect → blur → scaledRt 形成引用链，提前 Dispose 会崩溃。
        /// 所以不使用 using，改为手动 Dispose（finally 块）。
        /// </para>
        /// </summary>
        private static CanvasBitmap? GpuBake(
            DecodedFrame frame,
            float contentW, float contentH,
            bool shadow, float dpi,
            CanvasControl sender)
        {
            if (frame.W <= 0 || frame.H <= 0 || contentW <= 0 || contentH <= 0) return null;

            var   device = sender.Device;
            using var srcBmp = CanvasBitmap.CreateFromBytes(
                device, frame.Pixels, frame.W, frame.H,
                Windows.Graphics.DirectX.DirectXPixelFormat.R8G8B8A8UIntNormalized,
                dpi, CanvasAlphaMode.Premultiplied);

            float aspect = (float)frame.W / frame.H;
            float drawW, drawH;
            if (aspect >= contentW / contentH) { drawW = contentW; drawH = drawW / aspect; }
            else                               { drawH = contentH; drawW = drawH * aspect; }

            int dstW = Math.Max(1, (int)drawW);
            int dstH = Math.Max(1, (int)drawH);

            CanvasRenderTarget? scaledRt = null;
            try
            {
                scaledRt = new CanvasRenderTarget(device, dstW, dstH, dpi,
                    Windows.Graphics.DirectX.DirectXPixelFormat.R8G8B8A8UIntNormalized,
                    CanvasAlphaMode.Premultiplied);

                using (var ds = scaledRt.CreateDrawingSession())
                {
                    ds.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0));
                    using var geom = CanvasGeometry.CreateRoundedRectangle(
                        device, new Rect(0, 0, dstW, dstH), CornerRadius, CornerRadius);
                    using (ds.CreateLayer(1f, geom))
                    {
                        ds.DrawImage(srcBmp,
                            new Rect(0, 0, dstW, dstH),
                            new Rect(0, 0, frame.W, frame.H),
                            1f, CanvasImageInterpolation.MultiSampleLinear);
                    }
                }

                float pad  = shadow ? ShadowPad : 0f;
                int   rtW  = dstW + (int)(pad * 2);
                int   rtH  = dstH + (int)(pad * 2);

                var finalRt = new CanvasRenderTarget(device, rtW, rtH, dpi,
                    Windows.Graphics.DirectX.DirectXPixelFormat.R8G8B8A8UIntNormalized,
                    CanvasAlphaMode.Premultiplied);

                using (var ds = finalRt.CreateDrawingSession())
                {
                    ds.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0));

                    if (shadow)
                    {
                        // blur 和 shadowEffect 必须在 scaledRt dispose 之前 DrawImage 完成
                        using var blur = new GaussianBlurEffect
                        {
                            Source     = scaledRt,
                            BlurAmount = 4f,
                            BorderMode = EffectBorderMode.Soft,
                        };
                        using var shadowEffect = new ColorMatrixEffect
                        {
                            Source      = blur,
                            ColorMatrix = new Microsoft.Graphics.Canvas.Effects.Matrix5x4 { M44 = 100f / 255f }
                        };
                        ds.DrawImage(shadowEffect, new Vector2(pad + 1, pad + 2));
                    }

                    ds.DrawImage(scaledRt, new Vector2(pad, pad));
                }

                return finalRt;
            }
            finally
            {
                scaledRt?.Dispose();
            }
        }
    }
}
