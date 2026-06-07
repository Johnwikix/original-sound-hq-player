# GCReason 枚举参考

来自 TraceEvent `GCStartTraceData.Reason` 字段。

| GCReason      | 含义               | WinUI 3 常见来源                                   | 处理方向                        |
| ------------- | ------------------ | -------------------------------------------------- | ------------------------------- |
| AllocSmall    | SOH 小对象堆耗尽   | DispatcherQueue 闭包、string 格式化、LINQ 中间对象 | 减少短命对象；对象池            |
| AllocLarge    | LOH 大对象分配触发 | WriteableBitmap、byte[] 图像缓冲、大型集合初始化   | ArrayPool；复用缓冲区           |
| Induced       | 显式 GC.Collect()  | EF Core、第三方库、自己代码                        | 找调用栈；评估是否必要          |
| LowMemory     | 操作系统内存压力   | 容器/低内存设备                                    | 减少工作集；检查泄漏            |
| OutOfSpaceLOH | LOH 完全耗尽       | 持续大对象分配未复用                               | 严重碎片化；需要 ArrayPool 重构 |
| Internal      | CLR 内部触发       | —                                                  | 通常无害                        |

## 关键判断

```
Gen2 STW > 50ms
├── AllocLarge → 查 WriteableBitmap / byte[] / 大集合
├── AllocSmall → 查 DispatcherQueue 闭包 / 数据绑定 / LINQ
└── Induced    → 开栈采集找 GC.Collect() 调用者

Gen0 频率 > 500次/分
└── AllocSmall → 高频短命对象，查 DispatcherQueue / 事件处理器内的临时对象
```
