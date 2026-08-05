using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using System;

namespace AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.Advance
{
    /// <summary>
    /// 歌词 region 的播放进度填充链。
    ///
    /// 渐变内容在布局期一次性录制：<see cref="RenderLyricsLine.EnsureRampFill"/> 录制
    /// 全行白→黑 Ramp（已播/未播固体切片用，固体矩阵忽略采样值）与"带宽渐变"
    /// BandFill（fade 切片用，255 电平铺满最大带宽，消除采样量化断层）。
    /// 运行时通过 CropEffect + ColorMatrixEffect 切片（已播 / 淡出带 / 未播）叠加进
    /// CompositeEffect，最后经 AlphaMaskEffect 施加文字遮罩；fade 切片的位置由
    /// <see cref="FillTransformFade"/>（Transform2DEffect）每帧平移驱动。
    ///
    /// 动画只依赖 SourceRectangle / Matrix / TransformMatrix 属性更新，全程不创建/
    /// 销毁任何 Win2D 资源（原实现每帧 new+Dispose CanvasCommandList 与
    /// CanvasLinearGradientBrush，是持续绘制时内存增长与 GC 压力的主源）。
    /// </summary>
    public class RenderLyricsRegion : IDisposable
    {
        public CropEffect FillCropPlayed { get; }
        public CropEffect FillCropFade { get; }
        public CropEffect FillCropUnplayed { get; }

        /// <summary>
        /// fade 切片的平移变换：把 [0, fadeWMax] 处的 BandFill 渐变平移到当前 band
        /// 位置（bandX = rX + p*rW）。每帧仅属性写入，零分配。
        /// </summary>
        public Transform2DEffect FillTransformFade { get; }

        public ColorMatrixEffect FillColorPlayed { get; }
        public ColorMatrixEffect FillColorFade { get; }
        public ColorMatrixEffect FillColorUnplayed { get; }

        public CompositeEffect FillComposite { get; }
        public AlphaMaskEffect FinalFillEffect { get; }

        /// <summary>上一次已应用的已播宽度（像素），脏检查用。</summary>
        public float LastPlayedWidthPx = float.NegativeInfinity;
        /// <summary>上一次已应用的淡出带宽度（像素），脏检查用。</summary>
        public float LastFadeWidthPx = float.NegativeInfinity;

        /// <summary>
        /// 三个 ColorMatrix 是否已被渲染器赋值过一次。
        /// 布局重建（同 line 对象重建 region）后新 region 的矩阵初始为恒等，
        /// 若渲染器的 line 级脏检查（lineFillChanged）未命中就会永远不赋值，
        /// 导致 Ramp 白→黑渐变直通显示（"诡异渐变"）。此标志强制首次赋值。
        /// </summary>
        public bool FillMatricesApplied;

        /// <summary>初始全零矩阵：未赋值时输出透明，绝不透传 Ramp 渐变。</summary>
        private static readonly Matrix5x4 s_transparentMatrix = new();

        public RenderLyricsRegion(ICanvasResourceCreator resourceCreator, ICanvasImage cachedFill, ICanvasImage rampFill, ICanvasImage bandFill)
        {
            FillCropPlayed = new CropEffect { Source = rampFill, BorderMode = EffectBorderMode.Hard };
            FillCropFade = new CropEffect { BorderMode = EffectBorderMode.Hard };
            FillCropUnplayed = new CropEffect { Source = rampFill, BorderMode = EffectBorderMode.Hard };

            FillTransformFade = new Transform2DEffect
            {
                Source = bandFill,
                InterpolationMode = CanvasImageInterpolation.Linear,
            };
            FillCropFade.Source = FillTransformFade;

            FillColorPlayed = new ColorMatrixEffect { Source = FillCropPlayed, ColorMatrix = s_transparentMatrix };
            FillColorFade = new ColorMatrixEffect { Source = FillCropFade, ColorMatrix = s_transparentMatrix };
            FillColorUnplayed = new ColorMatrixEffect { Source = FillCropUnplayed, ColorMatrix = s_transparentMatrix };

            // 明确 straight 空间语义（D2D 文档默认即 PREMULTIPLIED：
            // 输入 un-premultiply → 矩阵在 straight 空间应用 → 输出 premultiply），
            // 矩阵端点按 straight 分量构造，见 LyricsLineRenderer.BuildSolidMatrix/BuildFadeMatrix。
            FillColorPlayed.AlphaMode = CanvasAlphaMode.Premultiplied;
            FillColorFade.AlphaMode = CanvasAlphaMode.Premultiplied;
            FillColorUnplayed.AlphaMode = CanvasAlphaMode.Premultiplied;

            FillComposite = new CompositeEffect
            {
                Sources = { FillColorPlayed, FillColorFade, FillColorUnplayed },
                Mode = CanvasComposite.SourceOver
            };

            FinalFillEffect = new AlphaMaskEffect
            {
                AlphaMask = cachedFill,
                Source = FillComposite
            };
        }

        public void Dispose()
        {
            FinalFillEffect?.Dispose();
            FillComposite?.Dispose();
            FillColorPlayed?.Dispose();
            FillColorFade?.Dispose();
            FillColorUnplayed?.Dispose();
            FillCropPlayed?.Dispose();
            FillCropFade?.Dispose();
            FillCropUnplayed?.Dispose();
            FillTransformFade?.Dispose();
        }
    }
}
