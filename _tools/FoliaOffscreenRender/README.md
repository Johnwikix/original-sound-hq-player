# Folia 离屏对照渲染

这个工具调用生产 `FoliaLyricsRenderer` 的离屏入口，复用同一套歌词布局、逐字时序、镜头和模式前景，并使用生产环境的 `FoliaWallpaperEffect` ComputeSharp D2D1 shader。它在隐藏的 2×2 WinUI 窗口上初始化图形设备，然后把当前保留的 11 个模式直接渲染到 `CanvasRenderTarget` 并保存 PNG。它不会把主程序窗口显示到桌面。

```powershell
$env:NUGET_SCRATCH = "$PWD\.nuget-scratch"
dotnet restore .\_tools\FoliaOffscreenRender\FoliaOffscreenRender.csproj --ignore-failed-sources
dotnet run --project .\_tools\FoliaOffscreenRender\FoliaOffscreenRender.csproj -c Debug --no-build -- --output "$PWD\artifacts\folia-offscreen"
```

默认输出 1280×720，每个模式生成 `0.0`、`6.0`、`37.5` 秒三帧；`37.5` 秒与 Folia 的冻结探针对齐，便于逐帧比较，并写入 `manifest.txt`。可以用 `--width`、`--height` 调整尺寸。输出是本地 GPU 管线的验证素材；它不会把 Folia 的 React/WebGL 运行时当作参考实现，也不会调用网络服务。
