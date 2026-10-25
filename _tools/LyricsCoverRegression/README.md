# 歌词与默认封面回归

运行：`dotnet run --project _tools/LyricsCoverRegression/LyricsCoverRegression.csproj -c Release`

测试直接编译生产代码中的 LrcService、封面显示状态、调色板缓存和歌词断路器，引用实际 Lyricify 提供者。
HTTP 使用内存响应，不访问真实歌词服务；Music/AppData 和取色算法枚举使用最小替身。

覆盖：多来源回退、业务错误/空结果分类、取消、逐字歌词错误、冷却及并发探测、迟到结果、默认封面随主题变化、缓存合并与失败重试。
加载入口还检查 500 ms 防抖只保留最后一首、立即取消在途请求、忽略迟到缓存／最终结果，以及退出取消待执行的防抖。生产 `LyricsLoader` 在可排队的 UI 同步上下文中运行；实际 Win2D 原生资源生命周期由独立的 `_tools/LyricsRenderRegression` 检查。
缓存测试验证四种主题/算法组合只加载一次，并测量预热后 1000 次命中的托管分配。

这些测试不启动 WinUI，也不执行图像解码。实际页面切换、动画和着色器显示仍需在应用中验证。
