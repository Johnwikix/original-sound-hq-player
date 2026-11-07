using CommunityToolkit.WinUI;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using System;
using System.Collections.Generic;
using Windows.UI.ViewManagement;
using WinUIMusicPlayer.Helper.Animations;

namespace WinUIMusicPlayer.View.Controls;

/// <summary>
/// 目录树展开/收起的布局过渡：只让「留在树上的行」把位置平滑地让开或收拢，
/// 时长固定 180ms，与展开多少行无关。
/// 新行本身不做透明度动画：行容器的首帧与容器复用的时机都在框架内部，任何从回调里补的
/// 淡入都会先露一帧全亮（闪烁），或者让复用的容器停在未显示状态（子目录消失）。
/// 想要真正的逐行入场，只能替换 TreeViewItem 的容器模板。
/// </summary>
internal sealed class TreeViewExpansionMotion
{
    /// <summary>行位移补间时长。</summary>
    private const int ReflowMilliseconds = 180;
    /// <summary>补间摘除后的余量。</summary>
    private const int ReflowCleanupMargin = 80;
    /// <summary>重建恢复窗口的兜底时长。</summary>
    private const int RestoreGuardMilliseconds = 1000;

    private readonly TreeView _tree;
    private readonly UISettings _settings = new();
    /// <summary>本轮已挂上位移隐式动画的行视觉。</summary>
    private readonly List<Visual> _reflowVisuals = [];
    private DispatcherQueueTimer? _reflowTimer;
    /// <summary>顶层行容器的宿主（TreeView 内部的 TreeViewList），按需定位并缓存。</summary>
    private ItemsControl? _rowHost;
    private long _suppressDeadline;
    private bool _suppressRestore;
    private bool _restoreHooked;

    /// <summary>绑定目标 TreeView；过渡状态只随控件生命周期存在。</summary>
    internal TreeViewExpansionMotion(TreeView tree) => _tree = tree;

    /// <summary>系统关闭动画、正在重建根节点或本轮布局用于恢复展开状态时不做过渡。</summary>
    private bool CanAnimate
    {
        get
        {
            if (!_settings.AnimationsEnabled) return false;
            if (!_suppressRestore) return true;
            // 兜底：重建没有后续布局时不允许永久关闭过渡。
            if (Environment.TickCount64 < _suppressDeadline) return false;
            ClearRestoreGuard();
            return true;
        }
    }

    /// <summary>根节点被整体替换时调用：只跳过重建那一轮布局，不吞掉之后用户自己的展开。</summary>
    internal void SuppressRestorePass()
    {
        _suppressRestore = true;
        _suppressDeadline = Environment.TickCount64 + RestoreGuardMilliseconds;
        if (_restoreHooked) return;
        _restoreHooked = true;
        _tree.LayoutUpdated += OnRestoreGuardLayout;
    }

    private void OnRestoreGuardLayout(object? sender, object args) => ClearRestoreGuard();

    private void ClearRestoreGuard()
    {
        _suppressRestore = false;
        if (!_restoreHooked) return;
        _restoreHooked = false;
        _tree.LayoutUpdated -= OnRestoreGuardLayout;
    }

    /// <summary>
    /// 节点展开前调用：给已生成的行挂上位移补间，让它们在子行插入时平滑下移。
    /// 必须在本轮重排之前挂上，否则补间记录不到旧位置。
    /// </summary>
    internal void PrepareExpand()
    {
        ClearTransitions();
        if (!CanAnimate) return;
        AttachReflow();
    }

    /// <summary>
    /// 节点收起后调用。离场行做不了动画：WinUI 在触发 Collapsed 之前就同步移除了子行，
    /// 且同帧把 IsExpanded 设回 true 也不会恢复容器（实测 collapsed 触发、expanding 不触发、
    /// ContainerFromItem 仍为 null）。收起只能让剩余行补间上去。
    /// </summary>
    internal void PrepareCollapse()
    {
        ClearTransitions();
        if (!CanAnimate) return;
        AttachReflow();
    }

    /// <summary>取消在途过渡并摘除隐式动画；用于离页，之后仍可继续使用。</summary>
    internal void Cancel()
    {
        ClearTransitions();
        ClearRestoreGuard();
        _rowHost = null;
    }

    /// <summary>给已生成的行挂 Offset 隐式动画，让插入/移除引起的上下位移被补间。</summary>
    private void AttachReflow()
    {
        var attached = 0;
        foreach (var panel in FindRowPanels())
        {
            foreach (var child in panel.Children)
            {
                if (child is not UIElement element) continue;
                var visual = ElementCompositionPreview.GetElementVisual(element);
                if (visual is null) continue;
                var animation = visual.Compositor.GetCached(
                    "__TreeRowReflow",
                    () => CreateReflow(visual.Compositor)
                );
                visual.SetImplicitAnimation(nameof(Visual.Offset), animation);
                _reflowVisuals.Add(visual);
                attached++;
            }
        }
        if (attached > 0) ScheduleReflowCleanup();
    }

    /// <summary>
    /// 取树里所有行面板。只沿「行容器」链走：TreeView 内部的列表 → 每一行（TreeViewItem 本身是 ItemsControl）
    /// → 它自己的行面板，因此访问量与行数成正比，而不是与整棵视觉树（含模板内部元素）成正比。
    /// </summary>
    private List<Panel> FindRowPanels()
    {
        // 顶层行的宿主是 TreeView 内部的列表；模板重建后缓存会失效，按 XamlRoot 判断重新定位。
        if (_rowHost is null || _rowHost.XamlRoot is null)
            _rowHost = _tree.FindDescendant<ListView>();
        List<Panel> panels = [];
        if (_rowHost is null) return panels;
        Collect(_rowHost, panels, 0);
        return panels;

        void Collect(ItemsControl owner, List<Panel> found, int depth)
        {
            // 目录层级有限，深度上限只用于防御异常模板造成的递归。
            if (depth > 32) return;
            if (owner.ItemsPanelRoot is Panel panel)
            {
                found.Add(panel);
                foreach (var child in panel.Children)
                    if (child is ItemsControl row) Collect(row, found, depth + 1);
            }
            else
            {
                // 面板尚未生成（未展开的行）时，直接看已实现的行容器。
                for (var i = 0; i < owner.Items.Count; i++)
                    if (owner.ContainerFromIndex(i) is ItemsControl row) Collect(row, found, depth + 1);
            }
        }
    }

    private static Vector3KeyFrameAnimation CreateReflow(Compositor compositor) =>
        compositor
            .CreateVector3KeyFrameAnimation()
            .SetTarget(nameof(Visual.Offset))
            .AddKeyFrame(0, CompositionFactory.STARTING_VALUE)
            .AddKeyFrame(1, CompositionFactory.FINAL_VALUE, compositor.GetCachedFluentEntranceEase())
            .SetDuration(TimeSpan.FromMilliseconds(ReflowMilliseconds));

    private void ScheduleReflowCleanup()
    {
        if (_reflowVisuals.Count == 0) return;
        var timer = EnsureTimer();
        if (timer is null) return;
        timer.Interval = TimeSpan.FromMilliseconds(ReflowMilliseconds + ReflowCleanupMargin);
        timer.Start();
    }

    private DispatcherQueueTimer? EnsureTimer()
    {
        if (_reflowTimer is not null) return _reflowTimer;
        var queue = _tree.DispatcherQueue;
        if (queue is null)
        {
            ClearTransitions();
            return null;
        }
        var timer = queue.CreateTimer();
        timer.IsRepeating = false;
        timer.Tick += (_, _) => ClearReflow();
        _reflowTimer = timer;
        return timer;
    }

    /// <summary>
    /// 隐式动画会随行视觉复用长期存在，补间结束后必须摘除，否则之后任何重排（滚动、过滤）
    /// 都会被动补间。先取走再清空，单个失效视觉不会让清理卡死。
    /// </summary>
    private void ClearReflow()
    {
        _reflowTimer?.Stop();
        var reflow = _reflowVisuals.ToArray();
        _reflowVisuals.Clear();
        foreach (var visual in reflow)
            visual.SetImplicitAnimation(nameof(Visual.Offset), null);
    }

    private void ClearTransitions() => ClearReflow();
}
