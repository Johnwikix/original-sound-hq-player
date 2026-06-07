# WinUI 3 XAML 渲染层性能分析

> 当 `time-in-gc` < 2% 但 UI 仍然卡顿时，问题在 XAML 渲染层，不是 GC。

## 快速判断工具

### Visual Studio 诊断工具（最直接）

```
Debug → Performance Profiler
→ 勾选 "XAML Layout"
→ 开始录制 → 复现卡顿 → 停止
→ 查看 Layout 耗时 > 16ms 的帧（60fps 预算）
```

### Windows Performance Analyzer (WPA)

```bash
# 用 PerfView 采集 XAML ETW
PerfView collect output.etl `
  /OnlyProviders:"Microsoft-Windows-XAML:0xFFFFFFFF:5" `
  /NoGui /AcceptEULA

# WPA 打开 → Generic Events → 找 Layout/Measure/Arrange
```

---

## 常见 XAML 渲染卡顿原因

### Measure/Arrange 过深

```
症状：滚动时卡顿，VisualTree 层级 > 10 层
排查：Live Visual Tree（VS 调试时）→ 看嵌套深度
修复：减少嵌套，用 Grid 替代多层 StackPanel
```

### ItemsRepeater / ListView 未虚拟化

```
症状：列表项 > 100 条时明显卡顿
排查：检查是否在 ScrollViewer 内用了 StackPanel（会禁用虚拟化）
修复：确保使用 ItemsRepeater + UniformGridLayout，或 ListView 默认虚拟化
```

### 图像未异步解码

```csharp
// ❌ 同步解码阻塞 UI 线程
var bmp = new BitmapImage(new Uri("ms-appx:///Assets/large.png"));

// ✅ 异步解码
var bmp = new BitmapImage();
bmp.DecodePixelWidth = 200; // 限制解码尺寸
await bmp.SetSourceAsync(stream);
```

### DataTemplate 过于复杂

```
症状：列表滚动时每次回收/复用模板都卡
排查：WPA → Microsoft-Windows-XAML → DataTemplate Instantiate 耗时
修复：简化 DataTemplate；用 x:Load 延迟加载非关键元素
     <Border x:Load="{x:Bind IsExpanded}" .../>
```

---

## WinRT Interop 跨线程阻塞

```
症状：调用 WinRT API 时主线程阻塞（如 StorageFile、Clipboard）
排查：WPA → CPU Usage → 找 CoWait / SwitchToThread
修复：所有 WinRT 异步 API 必须 await，不能 .GetAwaiter().GetResult()
```

```csharp
// ❌ 死锁风险，阻塞 UI 线程
var file = StorageFile.GetFileFromPathAsync(path).GetAwaiter().GetResult();

// ✅ 正确异步
var file = await StorageFile.GetFileFromPathAsync(path);
```
