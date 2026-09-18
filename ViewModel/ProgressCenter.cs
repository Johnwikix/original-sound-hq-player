using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Text;

namespace WinUIMusicPlayer.ViewModel;

/// <summary>
/// 全局唯一的进行中任务指示中心，驱动 MainPage 标题栏（AppTitle 旁）的唯一进度指示器：
/// 环 + 百分比 + 操作文本横排。每个操作以 Key 注册一条（显示文本 + 可选百分比 0-100，-1 表示不定进度），
/// 并发操作在文本中横排并列、各自带百分比。
/// 百分比不做跨操作加权平均：第二个操作加入时平均值会倒退，观感等同进度回退；因此只有
/// "恰好一个操作且它上报百分比"时进度环才用确定进度并显示独立百分比元素，其余情况
/// （多操作或含不定进度操作）回到不定进度，百分比只出现在各操作文本内。
/// 变更统一在 UI 线程应用：非 UI 线程调用自动转派 DispatcherQueue（FIFO 保证同一操作的
/// Begin/Report/Complete 顺序），窗口尚未创建的启动极早期没有绑定，直接改即可。
/// </summary>
public sealed partial class ProgressCenter : ObservableObject
{
    /// <summary>操作标识，调用方共用同一常量，避免字符串散落导致注册/移除错配。</summary>
    public static class Keys
    {
        public const string IpcConnecting = nameof(IpcConnecting);
        public const string LibraryLoading = nameof(LibraryLoading);
        public const string LibraryScanning = nameof(LibraryScanning);
        public const string LibraryRescanning = nameof(LibraryRescanning);
        public const string UsbTransmitting = nameof(UsbTransmitting);
    }

    private sealed class Entry(string key, string text, double percent)
    {
        public readonly string Key = key;
        public string Text = text;
        public double Percent = percent; // -1 = 不定进度
    }

    // 有序：显示文本按注册顺序逐行排列。条目数个位数，线性查找即可。
    private readonly List<Entry> _entries = [];

    public bool IsActive => _entries.Count > 0;

    /// <summary>仅当唯一条目上报了百分比时为 false；零条目时值无意义（进度层已隐藏）。</summary>
    public bool IsIndeterminate => OnlyEntry is null || OnlyEntry.Percent < 0;

    /// <summary>确定进度时的环值（0-100）；不定进度时 ProgressRing 忽略该值。</summary>
    public double Percent => OnlyEntry is null ? 0 : Math.Clamp(OnlyEntry.Percent, 0, 100);

    /// <summary>操作文本：单操作只显示名称（百分比由独立元素显示）；多操作横排并列、各自带百分比，
    /// 如 "正在扫描音乐库 45% · 正在传输到USB设备 12%"。</summary>
    public string DisplayText
    {
        get
        {
            if (_entries.Count == 0) return string.Empty;
            if (_entries.Count == 1) return _entries[0].Text;
            var builder = new StringBuilder(96);
            foreach (var entry in _entries)
            {
                if (builder.Length > 0) builder.Append(" · ");
                builder.Append(entry.Text);
                if (entry.Percent >= 0)
                    builder.Append(' ').Append((int)Math.Round(Math.Clamp(entry.Percent, 0, 100))).Append('%');
            }
            return builder.ToString();
        }
    }

    /// <summary>百分比元素（独立文本）仅在唯一操作且其上报百分比时显示，与原标题栏指示器语义一致。</summary>
    public bool IsPercentVisible => OnlyEntry is not null && OnlyEntry.Percent >= 0;

    /// <summary>独立百分比元素的文本，如 "45%"；不可见时为空串。</summary>
    public string PercentText => IsPercentVisible ? $"{(int)Math.Round(Percent)}%" : string.Empty;

    private Entry? OnlyEntry => _entries.Count == 1 ? _entries[0] : null;

    /// <summary>注册一个操作条目；同 Key 再次 Begin 视为重开该操作：更新文本并重置百分比。</summary>
    public void Begin(string key, string text, double percent = -1) => Run(() =>
    {
        var entry = Find(key);
        if (entry is null) _entries.Add(new Entry(key, text, percent));
        else
        {
            entry.Text = text;
            entry.Percent = percent;
        }
        OnEntriesChanged();
    });

    /// <summary>上报百分比（0-100）；条目不存在时忽略（迟到回调），只前进不回退。</summary>
    public void Report(string key, double percent) => Run(() =>
    {
        var entry = Find(key);
        if (entry is null) return;
        var value = Math.Clamp(percent, 0, 100);
        if (value <= entry.Percent) return;
        entry.Percent = value;
        OnEntriesChanged();
    });

    /// <summary>移除操作条目；不存在时忽略，可安全放入 finally 或重复调用。</summary>
    public void Complete(string key) => Run(() =>
    {
        for (var i = 0; i < _entries.Count; i++)
        {
            if (!string.Equals(_entries[i].Key, key, StringComparison.Ordinal)) continue;
            _entries.RemoveAt(i);
            OnEntriesChanged();
            return;
        }
    });

    private Entry? Find(string key)
    {
        foreach (var entry in _entries)
            if (string.Equals(entry.Key, key, StringComparison.Ordinal)) return entry;
        return null;
    }

    private void OnEntriesChanged()
    {
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(IsIndeterminate));
        OnPropertyChanged(nameof(Percent));
        OnPropertyChanged(nameof(DisplayText));
        OnPropertyChanged(nameof(IsPercentVisible));
        OnPropertyChanged(nameof(PercentText));
    }

    private void Run(Action mutation)
    {
        // 窗口关闭后 TryEnqueue 失败而丢失的更新只影响已不存在的展示，无需补救。
        var dispatcher = App.MainWindow?.DispatcherQueue;
        if (dispatcher is null || dispatcher.HasThreadAccess) mutation();
        else dispatcher.TryEnqueue(() => mutation());
    }
}
