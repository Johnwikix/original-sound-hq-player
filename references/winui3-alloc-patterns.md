# WinUI 3 特有分配热点模式与修复方案

## 1. WriteableBitmap / BitmapImage — LOH 大对象压力

### 症状

- Pause Reason: `AllocLarge`
- Allocation Stacks 热点：`byte[]`，来源 `WriteableBitmap..ctor` 或 `SoftwareBitmap`
- Gen2 STW 周期性出现，间隔与图像刷新频率一致

### 原因

`WriteableBitmap(1920, 1080)` 内部分配约 8MB byte[]，直接进 LOH（>85KB 阈值）。
每次创建新实例都是一次 LOH 分配，LOH 不压缩，长期导致碎片化。

### 修复

```csharp
// ❌ 错误：每帧/每次更新都 new
private void UpdateFrame()
{
    var bmp = new WriteableBitmap(1920, 1080); // 8MB LOH 分配
    // ...
}

// ✅ 正确：复用同一个 WriteableBitmap 实例
private WriteableBitmap _bitmap = new WriteableBitmap(1920, 1080); // 只分配一次

private async void UpdateFrame()
{
    using var stream = _bitmap.PixelBuffer.AsStream();
    await stream.WriteAsync(_pixelData); // 写入复用的缓冲区
    _bitmap.Invalidate();
}
```

---

## 2. DispatcherQueue lambda 闭包 — Gen0 高频分配

### 症状

- Pause Reason: `AllocSmall`
- Gen0 GC 频率 > 500次/分钟
- Allocation Stacks 热点：匿名类型 `<>c__DisplayClass`，来源 `DispatcherQueue.TryEnqueue`

### 原因

每次 `TryEnqueue(() => { ... })` 如果 lambda 捕获了外部变量，编译器会生成一个堆分配的闭包对象。
60fps 场景下每帧都投递 = 每秒 60 次堆分配。

### 修复

```csharp
// ❌ 每次调用都分配闭包
_dispatcherQueue.TryEnqueue(() =>
{
    MyTextBlock.Text = data.Value; // 捕获 data → 闭包堆分配
});

// ✅ 方案一：static lambda + 显式传参（.NET 6+）
_dispatcherQueue.TryEnqueue(static (state) =>
{
    var (block, value) = ((TextBlock, string))state;
    block.Text = value;
}, (MyTextBlock, data.Value)); // 值传递，无闭包

// ✅ 方案二：缓存委托（适合固定操作）
private readonly DispatcherQueueHandler _updateHandler;

public MyViewModel()
{
    _updateHandler = () => MyTextBlock.Text = _cachedValue;
}

private string _cachedValue;
private void UpdateUI(string value)
{
    _cachedValue = value;
    _dispatcherQueue.TryEnqueue(_updateHandler); // 复用同一个委托对象
}
```

---

## 3. 数据绑定 ToString() — string 高频分配

### 症状

- Allocation Stacks 热点：`string`，来源 `System.Convert.ToString` 或属性 getter
- 与 ItemsRepeater / ListView 滚动同步出现

### 原因

XAML 数据绑定每次刷新都调用 `Convert.ToString()`，对数值类型每次都产生新 string。
ListView 虚拟化回收时会触发大量绑定刷新。

### 修复

```csharp
// ❌ 每次绑定刷新都分配新 string
public int Count { get; set; } // 绑定到 TextBlock.Text，每次变化都 ToString()

// ✅ 缓存格式化结果，只在值真正变化时更新
private int _count;
private string _countText = "0";

public string CountText
{
    get => _countText;
    private set => SetProperty(ref _countText, value);
}

public int Count
{
    get => _count;
    set
    {
        if (_count == value) return;
        _count = value;
        CountText = value.ToString(); // 只在变化时分配
    }
}
```

---

## 4. WinRT COM 包装对象未释放 — 堆持续增长

### 症状

- `gc-heap-size` 持续增长，不随 GC 回落
- Allocation Stacks 热点：`WinRT.IInspectable` 或 `__com_ICoreWebView2` 等包装类型

### 原因

WinRT 对象在 .NET 侧有托管包装（RCW），但底层 COM 引用计数靠 `Dispose()` 释放。
事件没有解绑时，COM 对象会一直被 RCW 持有，导致托管堆和 Native 堆双重泄漏。

### 修复

```csharp
// ❌ 事件未解绑导致 RCW 泄漏
public MyPage()
{
    _sensor.ReadingChanged += OnReadingChanged; // 隐式持有 this
}
// 页面关闭时 _sensor 仍持有对 MyPage 的引用

// ✅ 在 Unloaded 事件中解绑
public MyPage()
{
    _sensor.ReadingChanged += OnReadingChanged;
    Unloaded += (_, _) =>
    {
        _sensor.ReadingChanged -= OnReadingChanged;
        _sensor.Dispose(); // 释放 COM 引用计数
    };
}
```

---

## 5. Induced GC — 框架或第三方库调用 GC.Collect()

### 症状

- Pause Reason: `Induced`
- 停顿与用户操作（如打开对话框、切换页面）同步出现

### 排查

```bash
# 采集时必须开启栈，否则看不到调用者
dotnet-trace collect -p <PID> `
  --providers "Microsoft-Windows-DotNETRuntime:0x4C14FCCBD:4" `
  -o induced.nettrace

# PerfView → GC Stats → 筛选 Reason=Induced
# → 找对应时间戳的调用栈
```

### 常见来源

- Entity Framework Core 旧版本的 `DbContext.Dispose()`
- 某些图像处理库在处理完成后调用
- 自己代码里的 `~析构函数` 触发终结器线程，间接引发 GC
