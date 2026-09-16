# 许可回归验证

运行：`dotnet run --project _tools/LicenseRegression/LicenseRegression.csproj -c Release`

项目直接编译生产 `LicenseService.cs`，仅替换 Store、窗口和 Dispatcher 边界。使用可控异步查询和时钟验证：首次查询前初始化 HWND、缓存同步返回、购买与旧查询重叠后的补查及等待、后台事件回到 UI 线程、查询失败保留状态、剩余天数与到期时间通知、关闭时解绑和忽略迟到结果。Release 模式避免读取个人 `license.debug` 文件。

UI 静态检查：关于页购买卡片绑定 `CanPurchaseLicense` 和 `PurchaseLicenseCommand`；DSP 购买按钮仍仅在受限时显示。两处共用命令。剩余天数通知不应重推音频设置。

输出门控回归还覆盖 DSD、5.1 与 Atmos 三个开关：试用/完整版保留原值，许可非活跃时仅将传输快照中的三个开关关闭，保存的偏好、EQ、音量和输出模式不变。实机还需验证从 5.1 或 Atmos 播放中转为受限状态时的保进度重建，以及购买后的输出恢复。

## 商店实包验收

自动测试不验证 Microsoft Store 真实交易、账户或 Windows 激活策略。发布前使用关联商店的签名包验证：

- 试用中打开“设置 → 关于”，确认剩余天数和购买入口；购买后入口消失，原有 DSP/DSD 偏好恢复生效。
- 对比取消购买、断网、已购买账户，确认失败不覆盖上次已确认许可。
- 应用持续运行至试用到期，以及到期后退出再启动，分别验证行为。
- 打开 DSP 页期间跨过剩余天数边界，确认无需操作播放器即可更新文字。

微软的[试用生命周期文档](https://learn.microsoft.com/en-us/windows/uwp/monetize/implement-a-trial-version-of-your-app)说明，限时试用在启动前已经到期时，系统可能阻止应用启动。因此，本次修复只保证**应用能够运行时**的许可门控；不把“商店限时试用到期后仍能启动并免费使用基础播放/EQ”视为已验证能力。若这是产品硬性要求，需要另行确认 Partner Center 的许可配置及产品收费方式，本次未改变商店配置或改用附加产品。

API 依据：[桌面 StoreContext 的 HWND 初始化](https://learn.microsoft.com/en-us/windows/uwp/monetize/in-app-purchases-and-trials#using-the-storecontext-class-with-the-desktop-bridge)、[许可变更事件](https://learn.microsoft.com/en-us/uwp/api/windows.services.store.storecontext.offlinelicenseschanged)、[IsActive 的含义](https://learn.microsoft.com/en-us/uwp/api/windows.services.store.storeapplicense.isactive)。
