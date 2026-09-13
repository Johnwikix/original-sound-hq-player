# 逐行歌词滚动回归

在仓库根目录运行：

```powershell
dotnet run --project _tools/LyricsEasingRegression/LyricsEasingRegression.csproj
```

测试链接生产代码的 `LyricScrollMotion`、`EasingHelper` 和 `LyricsLayoutManager`。
`LayoutFixtures.cs` 只替代 Win2D 文本/GPU 对象，不替代可见范围或命中算法。
覆盖逐行启动、延迟期间继续运动、快速换行替换目标、速度连续、30/60/120/144 Hz、
反向目标、布局重置、零时长、不同曲线组合、独立行可见性、缩放/滚轮偏移命中、
重叠行绘制顺序、两个设置入口、枚举持久化和所有语言资源。

## 设计参考

- [AMLL 布局调度](https://github.com/amll-dev/applemusic-like-lyrics/blob/main/packages/core/src/lyric-player/base/index.ts)：每个歌词组独立位移，布局更新时分配递增启动延迟。
- [AMLL 歌词组](https://github.com/amll-dev/applemusic-like-lyrics/blob/main/packages/core/src/lyric-player/base/group.ts)：逐组更新纵向弹簧。
- [AMLL 弹簧](https://github.com/amll-dev/applemusic-like-lyrics/blob/main/packages/core/src/utils/spring.ts)：切换目标时保持运动速度，等待新目标时继续旧运动。

本实现参考这些机制，独立编写 C# 调度和临界阻尼解析解，没有引入该项目依赖或复制其代码。
参数按本项目的布局与响应时间独立设定。

## 设置语义

- 曲线 `FlowWave`：在 Out / Continuous / FlowWave 方向下使用保留速度的弹簧；
  In / InOut 保留用户指定的时间反转/对称曲线语义。
- 方向 `FlowWave`：逐行错峰；可以搭配原有曲线。标量插值采用 Out。
- 两项都选流波：独立弹簧和逐行延迟组合，也是新配置的默认值。
- 兼容读取旧实验名称，保存时统一写入 `FlowWave`；已有的其他曲线选择保持不变。
- 自动前进逐行传播；跳转、布局重建、手动滚动归位不新增错峰延迟。
- 主歌词和译文作为一个布局组运动。鼠标滚动偏移叠加在每行位置上。
- 延迟间隔从约 50 ms 逐步收短，总延迟小于 250 ms 且受滚动时长限制；快速歌词缩短弹簧响应时间。
- 首次布局直接对齐；重排清除待执行动画；屏外行继续更新位置，文字效果仍按可见范围更新。

这些是 CPU 回归检查，不代替 WinUI 实际播放时的视觉验收。
