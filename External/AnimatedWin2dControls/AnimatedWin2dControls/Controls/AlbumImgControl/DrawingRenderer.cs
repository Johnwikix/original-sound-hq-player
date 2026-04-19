using Microsoft.Graphics.Canvas;
using System;
using Windows.Foundation;

namespace AnimatedWin2dControls.Controls.AlbumImgControl
{
    /// <summary>
    /// 纯绘制器：读取 <see cref="TransitionState"/> 和 <see cref="BakedRTCache"/> 的状态，
    /// 将结果写入 <see cref="CanvasDrawingSession"/>。
    /// <para>不持有任何 GPU 资源，也不修改传入状态的字段（仅写 TargetRectDirty / IncomingTargetRect / CurrentDestRectAtStart）。</para>
    /// </summary>
    internal static class DrawingRenderer
    {
        /// <summary>
        /// 执行一帧绘制。
        /// </summary>
        public static void Draw(
            CanvasDrawingSession ds,
            TransitionState state,
            BakedRTCache cache,
            float canvasW, float canvasH,
            float padTop, float padBottom, float padLeft, float padRight)
        {
            if (canvasW <= 0 || canvasH <= 0) return;

            float contentX = padLeft;
            float contentY = padTop;
            float contentW = canvasW - padLeft - padRight;
            float contentH = canvasH - padTop - padBottom;
            if (contentW <= 0 || contentH <= 0) return;

            // 确定参考位图（用于计算 incoming 目标矩形）
            CanvasBitmap? refBmp = state.IncomingBitmap ?? state.CurrentBitmap;
            if (refBmp == null) return;

            // ── 计算/缓存 incoming 目标矩形 ─────────────────────────────────
            var incomingTarget = CalcDestRect(refBmp, contentX, contentY, contentW, contentH);

            if (state.IsFading && state.TargetRectDirty)
            {
                state.IncomingTargetRect = incomingTarget;
                if (state.CurrentDestRectAtStart == Rect.Empty)
                    state.CurrentDestRectAtStart = incomingTarget;
                state.TargetRectDirty = false;
            }

            // ── 计算 eased/linear 进度 ───────────────────────────────────────
            float easedT = state.IsFading
                ? TransitionState.EaseOut(state.TransitionT)
                : (state.CurrentBitmap != null ? 1f : 0f);
            float linearT = state.TransitionT;

            // ── 计算绘制矩形 ─────────────────────────────────────────────────
            Rect currentDrawRect = state.IsFading
                ? LerpRect(state.CurrentDestRectAtStart, state.IncomingTargetRect, easedT)
                : incomingTarget;

            // ── 绘制 current ─────────────────────────────────────────────────
            float currentAlpha = state.IsFading ? Math.Max(0f, 1f - linearT) : 1f;
            if (cache.Current != null)
            {
                if (state.IsFading && currentAlpha > 0f)
                {
                    DrawBaked(ds, cache.Current, currentDrawRect, currentAlpha);
                }
                else if (!state.IsFading && state.CurrentBitmap != null)
                {
                    var staticRect = CalcDestRect(
                        state.CurrentBitmap, contentX, contentY, contentW, contentH);
                    DrawBaked(ds, cache.Current, staticRect, 1f);
                }
            }

            // ── 绘制 incoming ────────────────────────────────────────────────
            float incomingAlpha = state.IsFading ? Math.Min(1f, linearT) : 0f;
            if (cache.Incoming != null && incomingAlpha > 0f)
                DrawBaked(ds, cache.Incoming, currentDrawRect, incomingAlpha);
        }

        // ── 静态辅助 ─────────────────────────────────────────────────────────

        /// <summary>在内容区域内等比例居中绘制位图，返回目标矩形。</summary>
        public static Rect CalcDestRect(
            CanvasBitmap bmp, float cx, float cy, float cw, float ch)
        {
            float imgW = bmp.SizeInPixels.Width;
            float imgH = bmp.SizeInPixels.Height;
            if (imgW <= 0 || imgH <= 0) return new Rect(cx, cy, cw, ch);

            float aspect = imgW / imgH;
            float drawW, drawH;
            if (aspect >= cw / ch) { drawW = cw; drawH = drawW / aspect; }
            else { drawH = ch; drawW = drawH * aspect; }

            return new Rect(
                cx + (cw - drawW) * 0.5f,
                cy + (ch - drawH) * 0.5f,
                drawW, drawH);
        }

        /// <summary>矩形线性插值。</summary>
        public static Rect LerpRect(Rect a, Rect b, float t) =>
            new(a.X + (b.X - a.X) * t,
                a.Y + (b.Y - a.Y) * t,
                a.Width + (b.Width - a.Width) * t,
                a.Height + (b.Height - a.Height) * t);

        /// <summary>将 BakedRT 绘制到 destRect（图像内容区域，不含 pad）。</summary>
        private static void DrawBaked(
            CanvasDrawingSession ds,
            BakedRT baked,
            Rect destRect,
            float alpha)
        {
            if (destRect.Width <= 0 || destRect.Height <= 0) return;

            var destWithPad = new Rect(
                destRect.X - baked.Pad,
                destRect.Y - baked.Pad,
                destRect.Width + baked.Pad * 2,
                destRect.Height + baked.Pad * 2);

            ds.DrawImage(baked.RT, destWithPad, baked.RT.Bounds, alpha);
        }
    }
}