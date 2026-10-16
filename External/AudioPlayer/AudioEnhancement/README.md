# 升频数据布局

`Coefficients/Assets/dsp-tables.json` 是系数资源的可编辑目录。每个命名表保存小端字节，表间引用单独记录为表名及表内偏移；共享指数参数已解码。生成器按表名排序、八字节对齐并写入只读资源句柄，同时更新 C# 索引、资源长度和摘要。

在仓库根目录执行：

```powershell
python _tools/PackDspTables.py External/AudioPlayer/AudioEnhancement/Coefficients
python _tools/PackDspTables.py External/AudioPlayer/AudioEnhancement/Coefficients --check
```

`DspStateLayout` 按预测矩阵、升频控制、分析描述符、修复控制、配置、声道和参数组织状态。声道内部再分为缓冲区句柄、升频历史及修复历史。`DspMemory` 分别持有状态、输入、输出及十三个工作缓冲区；句柄高位选择缓冲区，低位为缓冲区内偏移，最高位标记只读系数资源。数组切片会检查各自的边界，写入接口拒绝系数资源句柄。

运算中的中间 float 写入、double 运算顺序、滤波抽头和延迟样本数均属于数值行为约束。调整布局时应保留这些约束，并运行 `SpectralEnhancementRegression`：其中固定基准覆盖 1,536 组配置和 18,874,368 个 PCM 样本，另外验证解码、延迟裁剪、尾部排空、重置及连续读取分配。可选的 `--golden` 参数还会比较既有 PCM 和全通滤波器状态捕获。
