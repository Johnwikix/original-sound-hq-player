using AnimatedWin2dControls.Shaders;
using ComputeSharp;
using ComputeSharp.D2D1.WinUI;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI.Xaml;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace AnimatedWin2dControls.Renderer
{
    /// <summary>
    /// 流体渐变背景渲染模块，Powered by Isolation https://github.com/Storyteller-Studios/Isolation 。
    /// 着色器逻辑与调色板/颜色过渡均内聚于此，供合成宿主在单一 DrawingSession 中调用。
    /// </summary>
    public sealed class FluidBackgroundRenderer : BreathingRendererBase, IDisposable
    {
        private PixelShaderEffect<FluidBackgroundEffect>? _effect;

        private float _time;
        private static readonly Random _random = new();
        private float _rnd1 = (float) (_random.NextDouble()* Math.PI * 2);
        private float _rnd2 = (float) (_random.NextDouble()* Math.PI * 2);
        private float _rnd3 = (float) (_random.NextDouble()* Math.PI * 2);        

        private Vector3 _c1, _c2, _c3, _c4;
        private Vector3 _target1, _target2, _target3, _target4;
        private float _transitionProgress = 1f;
        private const float TransitionSpeed = 0.5f;

        private AnimatedWin2dControls.Impressionist.PaletteResult? _currentPalette;

        public bool IsEnabled { get; set; } = true;
        public bool EnableLightWave { get; set; } = false;
        public bool IsDark { get; set; } = true;
        public bool UseImageDominantTheme { get; set; } = false;
        public double Opacity { get; set; } = 1.0;

        public void LoadResources()
        {
            Dispose();
            _effect = new PixelShaderEffect<FluidBackgroundEffect>();

            if (_currentPalette is not null)
                ApplyPaletteColors(_currentPalette);
            else
                ApplyDefaultColors();

            _c1 = _target1; _c2 = _target2;
            _c3 = _target3; _c4 = _target4;
            _transitionProgress = 1f;
        }

        public void Update(TimeSpan deltaTime)
        {
            if (_effect == null || !IsEnabled) return;

            // bass 恒为 0，呼吸退化为恒等
            base.UpdateBreathing(0f, 0);

            _time += (float)deltaTime.TotalSeconds;

            if (_transitionProgress >= 1f) return;

            float delta = (float)deltaTime.TotalSeconds;
            _transitionProgress = Math.Min(1f, _transitionProgress + delta * TransitionSpeed);

            float t = _transitionProgress * _transitionProgress * (3f - 2f * _transitionProgress);
            _c1 = Vector3.Lerp(_c1, _target1, t);
            _c2 = Vector3.Lerp(_c2, _target2, t);
            _c3 = Vector3.Lerp(_c3, _target3, t);
            _c4 = Vector3.Lerp(_c4, _target4, t);
        }

        public void Draw(ICanvasAnimatedControl control, CanvasDrawingSession ds)
        {
            if (_effect == null || !IsEnabled || Opacity <= 0) return;

            float width = control.ConvertDipsToPixels((float)control.Size.Width, CanvasDpiRounding.Round);
            float height = control.ConvertDipsToPixels((float)control.Size.Height, CanvasDpiRounding.Round);

            _effect.ConstantBuffer = new FluidBackgroundEffect(
                new float2(width, height),
                _time,
                new float3(_c1.X, _c1.Y, _c1.Z),
                new float3(_c2.X, _c2.Y, _c2.Z),
                new float3(_c3.X, _c3.Y, _c3.Z),
                new float3(_c4.X, _c4.Y, _c4.Z),
                _rnd1, _rnd2, _rnd3,
                useHSVBlending: false,
                enableLightWave: EnableLightWave,
                enableDithering: true);

            if (Opacity >= 1.0)
            {
                ds.DrawImage(_effect);
            }
            else
            {
                using var opacityEffect = new OpacityEffect
                {
                    Source = _effect,
                    Opacity = (float)Opacity
                };
                ds.DrawImage(opacityEffect);
            }
        }

        // ── 颜色 ──────────────────────────────────────────────────────────────

        public void SetPalette(AnimatedWin2dControls.Impressionist.PaletteResult? palette)
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

        private void ApplyDefaultColors()
        {
            bool isDark = IsDark;
            _target1 = isDark ? new Vector3(0.08f, 0.08f, 0.08f) : new Vector3(0.95f, 0.95f, 0.95f);
            _target2 = isDark ? new Vector3(0.25f, 0.28f, 0.27f) : new Vector3(0.70f, 0.68f, 0.71f);
            _target3 = isDark ? new Vector3(0.07f, 0.07f, 0.07f) : new Vector3(0.80f, 0.80f, 0.80f);
            _target4 = isDark ? new Vector3(0.05f, 0.05f, 0.05f) : new Vector3(0.85f, 0.85f, 0.85f);
            _transitionProgress = 0f;
        }

        private void ApplyPaletteColors(AnimatedWin2dControls.Impressionist.PaletteResult palette)
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
                int j = _random.Next(i + 1);
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

        public void Dispose()
        {
            _effect?.Dispose();
            _effect = null;
        }
    }
}
