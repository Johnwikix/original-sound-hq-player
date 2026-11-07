using CommunityToolkit.WinUI;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using System;
using System.Collections.Generic;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.UI.ViewManagement;
using WinUIMusicPlayer.Helper.Animations;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.View.Controls;

/// <summary>
/// 目录树展开/收起的过渡。TreeView 内部的扁平列表不执行 ItemContainerTransitions
/// （上游 microsoft-ui-xaml#208），布局层也不会在重排后留下可测量的位移——容器在布局
/// 完成时已经在最终位置，因此行位移只能用隐式 Offset 动画补间：布局改写视觉偏移的
/// 那一刻触发，正是重排本身。
/// 旧实现把隐式动画挂到了所有行，包括马上会被移除、进容器池复用的子树行；复用容器
/// 重新摆放时残留动画从旧坐标补间，就是「收起后行从左上方飞入」的来源。这里在
/// IsExpanded 变更的最早时机（框架的节点同步、项目增删和布局都在之后）识别出即将被
/// 移除的行，既不挂新动画、也摘掉它们残留的动画，其余存留行照常补间。
/// - 新行淡入：容器 Loaded 时先把视觉透明度置 0 再补间到 1，首帧不会露全亮；窗口由
///   扁平列表的插入事件打开，滚动虚拟化只实现容器、不插入条目，不会误淡入。
/// </summary>
internal sealed class TreeViewExpansionMotion
{
    /// <summary>行位移补间时长。</summary>
    private const int ReflowMilliseconds = 180;
    /// <summary>补间摘除前的余量。</summary>
    private const int ReflowCleanupMargin = 80;
    /// <summary>新行淡入时长。</summary>
    private const int FadeMilliseconds = 200;
    /// <summary>展开后新行分多轮布局实现；窗口过后视为滚动实现，不再淡入。</summary>
    private const int FadeWindowMilliseconds = 450;
    /// <summary>根节点重建后保持布局稳定多久再恢复过渡。</summary>
    private const int RestoreSettleMilliseconds = 120;

    private readonly TreeView _tree;
    private readonly UISettings _settings = new();
    /// <summary>本轮挂上位移补间的行视觉。</summary>
    private readonly List<Visual> _reflowVisuals = [];
    /// <summary>已注册 IsExpanded 变更回调的行容器及注销令牌。</summary>
    private readonly Dictionary<TreeViewItem, long> _trackedRows = [];
    /// <summary>已应用淡入的行视觉，避免同一轮里重复置 0。</summary>
    private readonly HashSet<Visual> _fading = [];
    private DispatcherQueueTimer? _reflowTimer;
    private DispatcherQueueTimer? _fadeWindowTimer;
    private DispatcherQueueTimer? _restoreTimer;
    private bool _itemsHooked;
    private bool _fadeWindowActive;
    private bool _suppressRestore;
    private bool _restoreHooked;

    internal TreeViewExpansionMotion(TreeView tree) => _tree = tree;

    /// <summary>根节点重建期间暂时关闭过渡，布局稳定后恢复。</summary>
    internal void SuppressRestorePass()
    {
        _suppressRestore = true;
        _fadeWindowActive = false;
        _fadeWindowTimer?.Stop();
        EnsureItemsHooked();
        if (_restoreHooked) return;
        _restoreHooked = true;
        _tree.LayoutUpdated += OnRestoreGuardLayout;
    }

    private void OnRestoreGuardLayout(object? sender, object args)
    {
        _restoreHooked = false;
        _tree.LayoutUpdated -= OnRestoreGuardLayout;
        // 等重建布局真正稳定后再恢复过渡，避免淡入和补间介入容器实现与选择恢复。
        var queue = _tree.DispatcherQueue;
        if (queue is null)
        {
            _suppressRestore = false;
            return;
        }
        var timer = _restoreTimer ??= queue.CreateTimer();
        timer.Stop();
        timer.Interval = TimeSpan.FromMilliseconds(RestoreSettleMilliseconds);
        timer.IsRepeating = false;
        timer.Tick -= OnRestoreSettled;
        timer.Tick += OnRestoreSettled;
        timer.Start();
    }

    private void OnRestoreSettled(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        _suppressRestore = false;
    }

    /// <summary>
    /// 行容器 Loaded 时由页面转发：注册展开状态回调，并在淡入窗口内置 0 起步补间透明度。
    /// </summary>
    internal void OnRowLoaded(TreeViewItem row)
    {
        EnsureItemsHooked();
        if (!_trackedRows.ContainsKey(row))
            _trackedRows[row] = row.RegisterPropertyChangedCallback(TreeViewItem.IsExpandedProperty, OnRowExpansionChanged);
        ApplyFade(row);
    }

    /// <summary>
    /// 淡入窗口内的行从 0 起步补间到 1。容器内容变更时就已应用，Loaded 只是兜底：
    /// 等到 Loaded 才置 0 会先渲染出一帧全亮，正是用户看到的闪烁。
    /// </summary>
    private void ApplyFade(TreeViewItem row)
    {
        if (!_fadeWindowActive || !_settings.AnimationsEnabled) return;
        var visual = ElementCompositionPreview.GetElementVisual(row);
        // 同一容器在一轮淡入里只应用一次：ContainerContentChanging 与 Loaded 都会到达。
        if (!_fading.Add(visual)) return;
        var animation = visual.Compositor.GetCached("__TreeRowFadeIn", () => CreateFadeIn(visual.Compositor));
        // 先停掉视觉上残留的淡入（容器复用），再置 0 起点，首帧渲染前生效，不会露全亮帧。
        visual.StopAnimation(nameof(Visual.Opacity));
        visual.Opacity = 0;
        visual.StartAnimation(nameof(Visual.Opacity), animation);
    }

    /// <summary>放弃在途过渡与重建监视；用于离页，之后仍可继续使用。</summary>
    internal void Cancel()
    {
        ClearReflow();
        foreach (var (row, token) in _trackedRows)
            row.UnregisterPropertyChangedCallback(TreeViewItem.IsExpandedProperty, token);
        _trackedRows.Clear();
        _fadeWindowActive = false;
        _fadeWindowTimer?.Stop();
        _restoreTimer?.Stop();
        _suppressRestore = false;
        if (!_restoreHooked) return;
        _restoreHooked = false;
        _tree.LayoutUpdated -= OnRestoreGuardLayout;
    }

    /// <summary>
    /// 行容器展开状态变更：整个增删流程里最早的时机（框架的节点同步、项目增删和布局
    /// 都在之后的派发轮里）。此刻能识别出即将被移除的子树行，避免给它们挂动画。
    /// </summary>
    private void OnRowExpansionChanged(DependencyObject sender, DependencyProperty property)
    {
        if (_suppressRestore || !_settings.AnimationsEnabled) return;
        if (sender is not TreeViewItem changed) return;
        var host = _tree.FindDescendant<ListView>();
        if (host?.ItemFromContainer(changed) is not FolderBrowserNode node) return;
        // 收起时把该节点的可见后代作为「将被移除」的行排除；展开时没有要排除的行，
        // 因为新行此刻还没有容器，而它们从容器池复用的旧动画已在上一次结算时摘除。
        AttachReflow(changed.IsExpanded ? null : node);
    }

    /// <summary>
    /// 给存留行挂 Offset 隐式动画，让插入/移除引起的上下位移被补间。排除即将被移除的
    /// 子树行：它们会进容器池并可能在同一轮里被复用重摆放，残留动画会让它们飞入。
    /// </summary>
    private void AttachReflow(FolderBrowserNode? collapsing)
    {
        var host = _tree.FindDescendant<ListView>();
        if (host?.ItemsPanelRoot is not { } panel) return;
        double viewHeight = _tree.ActualHeight;
        foreach (var child in panel.Children)
        {
            if (child is not TreeViewItem row) continue;
            double y = row.TransformToVisual(_tree).TransformPoint(default).Y;
            if (y + row.ActualHeight <= 0 || y >= viewHeight) continue;
            var visual = ElementCompositionPreview.GetElementVisual(row);
            if (collapsing is not null && host.ItemFromContainer(row) is FolderBrowserNode item && IsDescendantOf(item, collapsing))
            {
                // 即将离场的行：摘掉上一批次可能残留的动画，确保它进容器池时是干净的。
                visual.SetImplicitAnimation(nameof(Visual.Offset), null);
                continue;
            }
            visual.SetImplicitAnimation(
                nameof(Visual.Offset),
                visual.Compositor.GetCached("__TreeRowReflow", () => CreateReflow(visual.Compositor)));
            _reflowVisuals.Add(visual);
        }
        if (_reflowVisuals.Count > 0) ScheduleReflowCleanup();
    }

    private static bool IsDescendantOf(FolderBrowserNode node, FolderBrowserNode ancestor)
    {
        for (var parent = node.Parent; parent is not null; parent = parent.Parent)
            if (ReferenceEquals(parent, ancestor)) return true;
        return false;
    }

    private static Vector3KeyFrameAnimation CreateReflow(Compositor compositor) =>
        compositor
            .CreateVector3KeyFrameAnimation()
            .SetTarget(nameof(Visual.Offset))
            .AddKeyFrame(0, CompositionFactory.STARTING_VALUE)
            .AddKeyFrame(1, CompositionFactory.FINAL_VALUE, compositor.GetCachedFluentEntranceEase())
            .SetDuration(TimeSpan.FromMilliseconds(ReflowMilliseconds));

    private static ScalarKeyFrameAnimation CreateFadeIn(Compositor compositor) =>
        compositor
            .CreateScalarKeyFrameAnimation()
            .SetTarget(nameof(Visual.Opacity))
            .AddKeyFrame(0, 0f)
            .AddKeyFrame(1, 1f, compositor.GetCachedFluentEntranceEase())
            .SetDuration(TimeSpan.FromMilliseconds(FadeMilliseconds));

    private void ScheduleReflowCleanup()
    {
        var queue = _tree.DispatcherQueue;
        if (queue is null)
        {
            ClearReflow();
            return;
        }
        var timer = _reflowTimer ??= queue.CreateTimer();
        timer.Stop();
        timer.Interval = TimeSpan.FromMilliseconds(ReflowMilliseconds + ReflowCleanupMargin);
        timer.IsRepeating = false;
        timer.Tick -= OnReflowCleanup;
        timer.Tick += OnReflowCleanup;
        timer.Start();
    }

    private void OnReflowCleanup(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        ClearReflow();
    }

    /// <summary>
    /// 隐式动画随行视觉复用长期存在，补间结束后必须摘除，否则之后任何重排（滚动、过滤、
    /// 容器复用）都会被动补间。先取走再清空，单个失效视觉不会让清理卡死。
    /// </summary>
    private void ClearReflow()
    {
        _reflowTimer?.Stop();
        var reflow = _reflowVisuals.ToArray();
        _reflowVisuals.Clear();
        foreach (var visual in reflow)
            visual.SetImplicitAnimation(nameof(Visual.Offset), null);
    }

    /// <summary>
    /// 监听扁平列表的插入事件以打开淡入窗口。滚动虚拟化只实现容器、不插入条目，
    /// 因此窗口只在真正的展开（新增行）时打开。
    /// </summary>
    private void EnsureItemsHooked()
    {
        if (_itemsHooked) return;
        var host = _tree.FindDescendant<ListView>();
        if (host?.Items is not IObservableVector<object> items) return;
        _itemsHooked = true;
        items.VectorChanged += OnItemsInserted;
        // 容器内容变更早于首帧渲染，是置 0 起点、避免露全亮帧的时机。
        host.ContainerContentChanging += OnContainerContentChanging;
    }

    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer is TreeViewItem row) ApplyFade(row);
    }

    private void OnItemsInserted(IObservableVector<object> sender, IVectorChangedEventArgs args)
    {
        if (_suppressRestore || args.CollectionChange != CollectionChange.ItemInserted) return;
        OpenFadeWindow();
    }

    private void OpenFadeWindow()
    {
        _fadeWindowActive = true;
        var queue = _tree.DispatcherQueue;
        if (queue is null) return;
        var timer = _fadeWindowTimer ??= queue.CreateTimer();
        timer.Stop();
        timer.Interval = TimeSpan.FromMilliseconds(FadeWindowMilliseconds);
        timer.IsRepeating = false;
        timer.Tick -= CloseFadeWindow;
        timer.Tick += CloseFadeWindow;
        timer.Start();
    }

    private void CloseFadeWindow(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        _fadeWindowActive = false;
        _fading.Clear();
    }
}
