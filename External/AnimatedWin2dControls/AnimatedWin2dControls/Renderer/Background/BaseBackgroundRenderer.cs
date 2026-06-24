using AnimatedWin2dControls.Impressionist;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace AnimatedWin2dControls.Renderer.Background
{
    /// <summary>
    /// 背景着色器渲染器抽象基类。统一 4 色来源、调色板提取、暗亮缩放、4 色平滑过渡，
    /// 供 Fluid / SeventiesMelt / Cosmic / PS3XMB / GradientFlow / WavyBackground 共用。
    /// 子类只负责装配自己的 <see cref="ComputeSharp.D2D1.WinUI.PixelShaderEffect{T}"/>
    /// 并在 <see cref="Draw"/> 中把 4 色塞进常量缓冲。
    /// </summary>
    public abstract class BaseBackgroundRenderer : BreathingRendererBase, IDisposable
    {
        // 4 色平滑过渡状态
        protected Vector3 _c1, _c2, _c3, _c4;
        private Vector3 _target1, _target2, _target3, _target4;
        private float _transitionProgress = 1f;
        private const float TransitionSpeed = 0.5f;

        private PaletteResult? _currentPalette;
        protected static readonly Random Rng = new();

        public bool EnableLightWave { get; set; } = false;   // 仅 Fluid 关心，其它 renderer 可忽略
        public bool IsDark { get; set; } = true;
        public bool UseImageDominantTheme { get; set; } = false;
        public double Opacity { get; set; } = 1.0;

        public float Time { get; protected set; }

        public abstract void LoadResources();
        public abstract void Update(TimeSpan deltaTime);
        public abstract void Draw(ICanvasAnimatedControl control, CanvasDrawingSession ds);
        public abstract void Dispose();

        // ── 颜色入口 ─────────────────────────────────────────────────────

        public void SetPalette(PaletteResult? palette)
        {
            _currentPalette = palette;
            if (palette?.Palette is not { Count: > 0 }) { ApplyDefaultColors(); return; }
            ApplyPaletteColors(palette);
        }

        public void RefreshColors()
        {
            if (_currentPalette is not null)
                ApplyPaletteColors(_currentPalette);
            else
                ApplyDefaultColors();
        }

        // 暴露给子类的"开盒即用"调色板入口
        protected PaletteResult? CurrentPalette => _currentPalette;
        protected Vector3 C1 => _c1;
        protected Vector3 C2 => _c2;
        protected Vector3 C3 => _c3;
        protected Vector3 C4 => _c4;

        // ── 默认色 / 调色板 / 缩放 / 过渡推进 ───────────────────

        protected void ApplyDefaultColors()
        {
            bool isDark = IsDark;
            _target1 = isDark ? new Vector3(0.08f, 0.08f, 0.08f) : new Vector3(0.95f, 0.95f, 0.95f);
            _target2 = isDark ? new Vector3(0.25f, 0.28f, 0.27f) : new Vector3(0.70f, 0.68f, 0.71f);
            _target3 = isDark ? new Vector3(0.07f, 0.07f, 0.07f) : new Vector3(0.80f, 0.80f, 0.80f);
            _target4 = isDark ? new Vector3(0.05f, 0.05f, 0.05f) : new Vector3(0.85f, 0.85f, 0.85f);
            _transitionProgress = 0f;
        }

        protected void ApplyPaletteColors(PaletteResult palette)
        {
            bool isDark = UseImageDominantTheme ? palette.PaletteIsDark : IsDark;
            var weighted = new List<(Vector3 Color, int Population)>(palette.Palette.Count);
            for (int i = 0; i < palette.Palette.Count; i++)
                weighted.Add((palette.Palette[i] / 255f, Math.Max(1, palette.Palette.Count - i)));

            ScalePaletteLuminance(weighted, isDark, UseImageDominantTheme);
            var slots = DistributeByPopulation(weighted);
            _target1 = slots[0];
            _target2 = slots[1];
            _target3 = slots[2];
            _target4 = slots[3];
            _transitionProgress = 0f;
        }

        /// <summary>
        /// 推进 4 色平滑过渡 + time 累计。子类 Update 应先调此方法。
        /// </summary>
        protected void Advance(TimeSpan deltaTime)
        {
            Time += (float)deltaTime.TotalSeconds;

            if (_transitionProgress >= 1f) return;

            float delta = (float)deltaTime.TotalSeconds;
            _transitionProgress = Math.Min(1f, _transitionProgress + delta * TransitionSpeed);

            float t = _transitionProgress * _transitionProgress * (3f - 2f * _transitionProgress);
            _c1 = Vector3.Lerp(_c1, _target1, t);
            _c2 = Vector3.Lerp(_c2, _target2, t);
            _c3 = Vector3.Lerp(_c3, _target3, t);
            _c4 = Vector3.Lerp(_c4, _target4, t);
        }

        /// <summary>
        /// 调色板/默认色应用后立刻把当前色同步到 _c1.._c4，避免首帧从 0 渐入。
        /// 子类 LoadResources 末尾应调此方法。
        /// </summary>
        protected void SnapToTarget()
        {
            _c1 = _target1; _c2 = _target2;
            _c3 = _target3; _c4 = _target4;
            _transitionProgress = 1f;
        }

        private static Vector3[] DistributeByPopulation(
            List<(Vector3 Color, int Population)> weighted)
        {
            const int Total = 4;
            int count = weighted.Count;

            int totalPop = 0;
            for (int i = 0; i < count; i++)
                totalPop += weighted[i].Population;

            int perColorMax = count >= 3 ? 2 : Total - 1;

            Span<int> slots = stackalloc int[count <= 8 ? count : 8];
            slots = slots[..count];
            slots.Clear();

            int assigned = 0;
            for (int i = 0; i < count; i++)
            {
                slots[i] = Math.Min(perColorMax,
                    (int)Math.Floor((float)weighted[i].Population / totalPop * Total));
                assigned += slots[i];
            }

            int remaining = Total - assigned;
            for (int r = 0; r < remaining; r++)
            {
                int best = -1;
                float bestRem = -1f;
                for (int i = 0; i < count; i++)
                {
                    if (slots[i] >= perColorMax) continue;
                    float rem = (float)weighted[i].Population / totalPop * Total - slots[i];
                    if (rem > bestRem) { bestRem = rem; best = i; }
                }
                if (best == -1)
                    for (int i = 0; i < count; i++)
                    {
                        float rem = (float)weighted[i].Population / totalPop * Total - slots[i];
                        if (rem > bestRem) { bestRem = rem; best = i; }
                    }
                slots[best]++;
            }

            if (count >= 2 && slots[0] == Total)
            {
                slots[0] = Total - 1;
                slots[1] = 1;
            }
            else if (count == 1)
            {
                var nudge = Vector3.Clamp(
                    weighted[0].Color + new Vector3(0.05f, -0.03f, 0.04f),
                    new Vector3(0.01f), new Vector3(0.99f));
                weighted.Add((nudge, 0));
                count = 2;
                slots = stackalloc int[2];
                slots[0] = Total - 1;
                slots[1] = 1;
            }

            var result = new Vector3[Total];
            int idx = 0;
            for (int i = 0; i < count && idx < Total; i++)
                for (int j = 0; j < slots[i] && idx < Total; j++)
                    result[idx++] = weighted[i].Color;

            for (int i = Total - 1; i > 0; i--)
            {
                int j = Rng.Next(i + 1);
                (result[i], result[j]) = (result[j], result[i]);
            }
            return result;
        }

        private static void ScalePaletteLuminance(
            List<(Vector3 Color, int Population)> weighted,
            bool isDark, bool useImageDominantTheme)
        {
            float targetAvg = isDark ? 0.45f : 0.55f;
            int count = weighted.Count;

            Span<float> hs = stackalloc float[count <= 8 ? count : 8];
            Span<float> ss = stackalloc float[count <= 8 ? count : 8];
            Span<float> ls = stackalloc float[count <= 8 ? count : 8];
            hs = hs[..count];
            ss = ss[..count];
            ls = ls[..count];

            int totalPop = 0;
            float weightedL = 0f;
            for (int i = 0; i < count; i++)
            {
                RgbToHsl(weighted[i].Color, out hs[i], out ss[i], out ls[i]);
                weightedL += ls[i] * weighted[i].Population;
                totalPop += weighted[i].Population;
            }

            float avgL = totalPop > 0 ? weightedL / totalPop : 0.5f;
            if (isDark && avgL <= targetAvg) return;
            if (!isDark && avgL >= targetAvg) return;

            float shift = targetAvg - avgL;
            float clampMin = isDark ? 0f : 0.3f;
            float clampMax = isDark ? 0.7f : 1f;

            for (int i = 0; i < count; i++)
            {
                if (isDark && ls[i] < 0.3f) continue;
                if (!isDark && ls[i] > 0.7f) continue;
                float newL = Math.Clamp(ls[i] + shift, clampMin, clampMax);
                weighted[i] = (HslToRgb(hs[i], ss[i], newL), weighted[i].Population);
            }
        }

        private static void RgbToHsl(Vector3 rgb, out float h, out float s, out float l)
        {
            float r = rgb.X, g = rgb.Y, b = rgb.Z;
            float max = Math.Max(r, Math.Max(g, b));
            float min = Math.Min(r, Math.Min(g, b));
            float delta = max - min;
            l = (max + min) * 0.5f;
            if (delta < 1e-5f) { h = 0f; s = 0f; return; }
            s = l > 0.5f ? delta / (2f - max - min) : delta / (max + min);
            if (max == r) h = ((g - b) / delta + (g < b ? 6f : 0f)) / 6f;
            else if (max == g) h = ((b - r) / delta + 2f) / 6f;
            else h = ((r - g) / delta + 4f) / 6f;
        }

        private static Vector3 HslToRgb(float h, float s, float l)
        {
            if (s < 1e-5f) return new Vector3(l);
            float q = l < 0.5f ? l * (1f + s) : l + s - l * s;
            float p = 2f * l - q;
            return new Vector3(HueToRgb(p, q, h + 1f / 3f),
                               HueToRgb(p, q, h),
                               HueToRgb(p, q, h - 1f / 3f));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float HueToRgb(float p, float q, float t)
        {
            if (t < 0f) t += 1f;
            if (t > 1f) t -= 1f;
            if (t < 1f / 6f) return p + (q - p) * 6f * t;
            if (t < 1f / 2f) return q;
            if (t < 2f / 3f) return p + (q - p) * (2f / 3f - t) * 6f;
            return p;
        }
    }
}
