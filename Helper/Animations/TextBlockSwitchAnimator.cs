using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace WinUIMusicPlayer.Helper.Animations;

/// <summary>
/// 通用 TextBlock 文本切换动画组件：Composition 驱动的三段式切换——
/// 旧文本淡出 → <paramref name="applyText"/> 换内容 → 新文本淡入（可选自下滑入），
/// 换文本发生在合成器批次完成回调中，前后帧各层内容严格一致，不再生硬瞬换。
///
/// <para>
/// 用法一（多处同步切换，如描边层 + 主层）：把一组需要同步换字的 TextBlock 交给
/// 同一实例，回调里一次性写入全部新文本，所有层在同一帧换内容：
/// <code>
/// var animator = new TextBlockSwitchAnimator(mainText, shadowText) { SlideInDistance = 8 };
/// animator.Switch(() => { mainText.Text = newMain; shadowText.Text = newMain; });
/// </code></para>
///
/// <para>用法二（单个 TextBlock 速用）：<see cref="SwitchText(TextBlock, string)"/>。</para>
///
/// <para>
/// 行为细节：动画只作用于元素视觉的 Opacity/Translation（不碰布局属性）；淡入终点
/// 取各自 XAML Opacity 作为静止值（不同透明度的目标互不干扰）；切换期间再次
/// <see cref="Switch"/> 以代计数作废旧序列并从当前透明度续接（快速连续换行不闪跳）；
/// 目标未加载（不在可视树）时直接落地文本，加载后自动启用动画。
/// </para>
/// </summary>
public sealed class TextBlockSwitchAnimator
{
    private static readonly ConditionalWeakTable<TextBlock, TextBlockSwitchAnimator> Attached = [];

    private readonly TextBlock[] _targets;
    private int _generation;

    /// <summary>旧文本退场淡出时长。</summary>
    public TimeSpan FadeOutDuration { get; set; } = TimeSpan.FromMilliseconds(110);

    /// <summary>新文本进场淡入时长。</summary>
    public TimeSpan FadeInDuration { get; set; } = TimeSpan.FromMilliseconds(260);

    /// <summary>淡入阶段新文本自下而上滑入的距离（DIP），0 为纯交叉淡化。</summary>
    public double SlideInDistance { get; set; }

    /// <summary>总开关：false 时 <see cref="Switch(Action, bool)"/> 直接落地文本。</summary>
    public bool IsEnabled { get; set; } = true;

    public TextBlockSwitchAnimator(IEnumerable<TextBlock> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        _targets = targets.Where(static t => t is not null).Distinct().ToArray();
    }

    public TextBlockSwitchAnimator(params TextBlock[] targets)
        : this(targets.AsEnumerable())
    {
    }

    /// <summary>取挂载在某 TextBlock 上的共享实例（懒创建），适合散点复用。</summary>
    public static TextBlockSwitchAnimator Get(TextBlock target) =>
        Attached.GetValue(target, static t => new TextBlockSwitchAnimator(t));

    /// <summary>单个 TextBlock 速用：文本有变化才以动画切换，无变化不动作。</summary>
    public static void SwitchText(TextBlock target, string text)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Text == text) return;
        Get(target).Switch(() => target.Text = text);
    }

    /// <summary>淡出旧文本 → <paramref name="applyText"/> → 淡入新文本。</summary>
    public void Switch(Action applyText) => Switch(applyText, fadeOutFirst: true);

    /// <summary>
    /// 执行一次切换。<paramref name="fadeOutFirst"/> 为 false 时跳过退场淡出
    /// （上一条为空内容、无东西可退场的场景），直接换文本并淡入。
    /// </summary>
    public void Switch(Action applyText, bool fadeOutFirst)
    {
        ArgumentNullException.ThrowIfNull(applyText);

        if (!IsEnabled || _targets.Length == 0)
        {
            applyText();
            return;
        }

        if (!fadeOutFirst)
        {
            applyText();
            StartFadeIn(CollectActive());
            return;
        }

        int generation = ++_generation;
        var actives = CollectActive();
        if (actives.Count == 0)
        {
            applyText();
            return;
        }

        // 按合成器分组建作用域批次（同窗口目标共享一个合成器，通常只有一组）；
        // 全部组完成后才换文本，保证跨窗口组合同样帧对齐。
        var groups = new Dictionary<Compositor, List<(int Index, Visual Visual)>>();
        foreach (var active in actives)
        {
            if (!groups.TryGetValue(active.Visual.Compositor, out var group))
                groups[active.Visual.Compositor] = group = [];
            group.Add(active);
        }

        int pending = groups.Count;
        foreach (var (compositor, indexes) in groups)
        {
            var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            batch.Completed += (_, _) =>
            {
                batch.Dispose();
                if (Interlocked.Decrement(ref pending) != 0 || generation != _generation)
                    return; // 还有别的组未完成 / 序列已被新的 Switch 作废

                applyText();
                StartFadeIn(CollectActive());
            };

            foreach (var (_, visual) in indexes)
                StartFadeOut(visual);

            batch.End();
        }
    }

    // 收集本轮可动画目标：已加载且可见；拿不到元素视觉（未布局完成等）的静默跳过，
    // 其内容仍由 applyText 统一写入，不参与动画而已。
    private List<(int Index, Visual Visual)> CollectActive()
    {
        List<(int, Visual)>? actives = null;
        for (int i = 0; i < _targets.Length; i++)
        {
            var tb = _targets[i];
            if (tb is not { IsLoaded: true, Visibility: Visibility.Visible })
                continue;

            Visual? visual = null;
            try { visual = tb.GetElementVisual(); }
            catch { }

            if (visual is not null)
                (actives ??= []).Add((i, visual));
        }

        return actives ?? [];
    }

    private void StartFadeOut(Visual visual)
    {
        // 只给终点关键帧：从当前透明度出发，中断中的旧动画也能平滑续接。
        visual.StartAnimation(
            visual.CreateScalarKeyFrameAnimation(nameof(Visual.Opacity))
                .AddKeyFrame(1f, 0f)
                .SetDuration(FadeOutDuration));
    }

    private void StartFadeIn(List<(int Index, Visual Visual)> actives)
    {
        if (actives.Count == 0) return;

        foreach (var (index, visual) in actives)
        {
            var tb = _targets[index];
            float restOpacity = (float)tb.Opacity;

            var compositor = visual.Compositor;
            var ease = compositor.GetCachedFluentEntranceEase();
            var fade = compositor.CreateScalarKeyFrameAnimation()
                .SetTarget(nameof(Visual.Opacity))
                .AddKeyFrame(0f, 0f)
                .AddKeyFrame(1f, restOpacity, ease)
                .SetDuration(FadeInDuration);

            if (SlideInDistance > 0.01)
            {
                // Translation 为布局无关的合成偏移（需启用），淡入终点归零，
                // 与 XAML RenderTransform（描边层平移）互不干扰。
                tb.EnableCompositionTranslation();
                var slide = compositor.CreateVector3KeyFrameAnimation()
                    .SetTarget(CompositionFactory.TRANSLATION)
                    .AddKeyFrame(0f, new Vector3(0f, (float)SlideInDistance, 0f))
                    .AddKeyFrame(1f, Vector3.Zero, ease)
                    .SetDuration(FadeInDuration);

                visual.StartAnimation(compositor.CreateAnimationGroup(fade, slide));
            }
            else
            {
                visual.StartAnimation(fade);
            }
        }
    }
}
