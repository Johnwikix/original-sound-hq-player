using System;
using System.IO;
using System.Threading.Tasks;
using BassPlayerIpc.Shared;
using CommunityToolkit.WinUI;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Windows.ApplicationModel;
using Windows.Services.Store;

namespace WinUIMusicPlayer.Services;

/// <summary>许可状态；受限表示 Store 许可非活跃或无法确认（含非 Store 渠道）。</summary>
public enum AppLicenseState
{
    /// <summary>非 Store 渠道或首次查询失败：无法确认许可，按受限处理（fail-closed）。</summary>
    StoreUnavailable,
    /// <summary>已购完整版。</summary>
    FullLicense,
    /// <summary>试用期内。</summary>
    TrialActive,
    /// <summary>许可非活跃（包含试用到期）：限制 LicensePolicy 中列出的功能。</summary>
    LicenseInactive
}

/// <summary>Store 许可状态源：非活跃或无法确认许可时按 LicensePolicy 限制功能；
/// 非 Store 渠道和首次查询失败同样受限（fail-closed）；后续查询失败保持上次确认的状态。</summary>
public sealed class LicenseService : IDisposable
{
    private const string ProductId = "9NFW1RPPT999";
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(30);

    private readonly ILogger<LicenseService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly DispatcherQueue _queue;
    private readonly Func<Task> _refreshOnUi;
    private StoreContext? _context;
    private string? _productStoreId;
    private DispatcherQueueTimer? _refreshTimer;
    private DispatcherQueueTimer? _trialClockTimer;
    private TaskCompletionSource? _refreshCompletion;
    private bool _refreshPending, _disposed;
    private int? _lastNotifiedDays;

    public AppLicenseState State { get; private set; } = AppLicenseState.StoreUnavailable;
    /// <summary>试用到期时间；仅 <see cref="AppLicenseState.TrialActive"/> 时有意义。</summary>
    public DateTimeOffset? TrialExpiration { get; private set; }
    /// <summary>许可非活跃或 Store 状态未知；具体功能应使用 IsFeatureRestricted 查询。</summary>
    public bool IsRestricted => State is AppLicenseState.LicenseInactive or AppLicenseState.StoreUnavailable;
    public LicenseFeature RestrictedFeatures => IsRestricted ? LicensePolicy.RestrictedFeatures : LicenseFeature.None;
    public bool IsFeatureRestricted(LicenseFeature feature) => LicensePolicy.Contains(RestrictedFeatures, feature);

    /// <summary>仅覆盖发往引擎的输出快照，不改写持久化偏好；值类型原地修改不分配堆对象。</summary>
    public void ApplyOutputRestrictions(ref IpcSetting settings)
    {
        LicensePolicy.ApplyOutputRestrictions(ref settings, RestrictedFeatures);
    }
    /// <summary>试用期内或许可非活跃时可发起商店购买；Store 状态未知（非 Store 渠道或查询失败）时购买无法完成，不提供购买入口。</summary>
    public bool CanPurchase => State is AppLicenseState.TrialActive or AppLicenseState.LicenseInactive;
    /// <summary>试用剩余天数（不足一天按 1 计，到期为 0）；非试用期内为 null。</summary>
    public int? TrialRemainingDays => State == AppLicenseState.TrialActive && TrialExpiration is { } expiration
        ? Math.Max(0, (int)Math.Ceiling((expiration - _timeProvider.GetUtcNow()).TotalDays))
        : null;
    /// <summary>许可状态、到期时间或剩余天数变化后在 UI 线程触发。</summary>
    public event Action? StateChanged;

    public LicenseService(ILogger<LicenseService> logger) : this(logger, TimeProvider.System) { }

    internal LicenseService(ILogger<LicenseService> logger, TimeProvider timeProvider)
    {
        _logger = logger;
        _timeProvider = timeProvider;
        _queue = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("LicenseService 必须在 UI 线程创建。");
        _refreshOnUi = RefreshOnUiAsync;
    }

    /// <summary>启动时判定许可状态；必须在首次向播放进程推送设置前完成。</summary>
    public async Task InitializeAsync()
    {
        if (_disposed || _refreshTimer != null) return;
        #if DEBUG
        if (TryApplyLicenseFileOverride()) { StartTimers(); return; }
        #endif
        var signature = Package.Current.SignatureKind;
        if (signature != PackageSignatureKind.Store)
        {
            // 文件解锁仅限未签名包（本地 Add-AppxPackage -Register 注册的构建布局）：
            // 开发者/企业签名的旁加载包可能被再分发，保持受限；Store 签名包已在上部分流，
            // 商业渠道只认商店许可。
            if (signature == PackageSignatureKind.None && TryApplyLicenseFileOverride()) { StartTimers(); return; }
            _logger.LogInformation("包签名类型为 {Kind}，非 Store 渠道，无法确认许可，按受限处理（fail-closed）", signature);
            return;
        }
        try
        {
            EnsureContext();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "StoreContext 初始化失败，无法确认许可，按受限处理（fail-closed）");
            return;
        }
        StartTimers();
        await RefreshAsync();
    }

    private StoreContext EnsureContext()
    {
        if (_context != null) return _context;
        var window = App.MainWindow ?? throw new InvalidOperationException("许可初始化需要主窗口。");
        var context = StoreContext.GetDefault();
        WinRT.Interop.InitializeWithWindow.Initialize(context, WinRT.Interop.WindowNative.GetWindowHandle(window));
        context.OfflineLicensesChanged += OnOfflineLicensesChanged;
        return _context = context;
    }

    private void StartTimers()
    {
        _refreshTimer = _queue.CreateTimer();
        _refreshTimer.Interval = RefreshInterval;
        _refreshTimer.Tick += OnRefreshTick;
        _refreshTimer.Start();
        // UI 时钟只计算剩余天数，不触发 Store IO；复用方法委托，避免每次 Tick 分配闭包。
        _trialClockTimer = _queue.CreateTimer();
        _trialClockTimer.Interval = TimeSpan.FromMinutes(1);
        _trialClockTimer.Tick += OnTrialClockTick;
        _trialClockTimer.Start();
    }

    private void OnOfflineLicensesChanged(StoreContext sender, object args) => _ = RefreshAsync();
    private void OnRefreshTick(DispatcherQueueTimer sender, object args) => _ = RefreshAsync();
    private void OnTrialClockTick(DispatcherQueueTimer sender, object args)
    {
        if (!_disposed && State == AppLicenseState.TrialActive) ApplyState(State, TrialExpiration);
    }

    /// <summary>许可覆盖文件：读取 Documents\OriginalSoundPlayer\license.debug（内容 full/trial/expired，
    /// 删除即恢复真实判定）。DEBUG 构建对任意包生效，便于随时模拟各许可状态；Release 仅对
    /// 未签名包生效（本地 -Register 布局的自用完整版入口），开发者/企业签名的旁加载包与
    /// Store 签名包均不读取。MsixPackage 调试激活不传递 launchSettings 环境变量，
    /// 文件方式不依赖启动机制。</summary>
    private bool TryApplyLicenseFileOverride()
    {
        string? source = ReadLicenseFile();
        if (string.IsNullOrWhiteSpace(source)) return false;
        switch (source)
        {
            case "full":
                _logger.LogInformation("许可覆盖文件：完整版");
                ApplyState(AppLicenseState.FullLicense, null);
                return true;
            case "trial":
                var trialEnd = DateTimeOffset.Now.AddDays(3);
                _logger.LogInformation("许可覆盖文件：试用中（{Expiration} 到期）", trialEnd);
                ApplyState(AppLicenseState.TrialActive, trialEnd);
                return true;
            case "expired":
                _logger.LogInformation("许可覆盖文件：试用已到期");
                ApplyState(AppLicenseState.LicenseInactive, null);
                return true;
            default:
                _logger.LogWarning("许可覆盖文件值无效：{Value}（可用：full/trial/expired）", source);
                return false;
        }
    }

    /// <summary>测试注入点：非 null 时替代真实文件读取，回归不依赖开发机文档目录的实际文件。</summary>
    internal Func<string?>? LicenseOverrideReader { get; set; }

    private string? ReadLicenseFile()
    {
        if (LicenseOverrideReader is { } reader) return reader();
        try
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "OriginalSoundPlayer", "license.debug");
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>重查许可；失败时记录日志并保持当前状态（初始 Store 状态未知时按受限处理）。</summary>
    public async Task RefreshAsync()
    {
        // Store 回调可能来自后台；查询调度、快照和通知统一由 UI 线程维护。
        try
        {
            await _queue.EnqueueAsync(_refreshOnUi);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "许可刷新调度失败，保持当前状态");
        }
    }

    private Task RefreshOnUiAsync()
    {
        if (_disposed || _context == null) return Task.CompletedTask;
        _refreshPending = true;
        if (_refreshCompletion is { } running) return running.Task;
        // 先发布完成源，再开始查询，兼容 Store 同步返回缓存结果。
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _refreshCompletion = completion;
        _ = DrainRefreshAsync(completion);
        return completion.Task;
    }

    private async Task DrainRefreshAsync(TaskCompletionSource completion)
    {
        try
        {
            do
            {
                _refreshPending = false;
                try
                {
                    StoreAppLicense license = await _context!.GetAppLicenseAsync();
                    if (_disposed) break;
                    if (!license.IsActive) ApplyState(AppLicenseState.LicenseInactive, null);
                    else if (license.IsTrial) ApplyState(AppLicenseState.TrialActive, license.ExpirationDate);
                    else ApplyState(AppLicenseState.FullLicense, null);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Store 许可查询失败，保持当前状态（{State}）", State);
                }
                // 在途查询期间的请求合并为一次补查，所有调用方等待补查完成。
            } while (_refreshPending && !_disposed);
        }
        finally { _refreshCompletion = null; completion.TrySetResult(); }
    }

    /// <summary>弹出 Store 购买对话框；无法取得 StoreId 时回退打开商店页面。完成后强制刷新许可。</summary>
    public async Task PurchaseAsync()
    {
        if (_disposed || !CanPurchase) return;
        try
        {
            var context = EnsureContext();
            var storeId = _productStoreId ??= await GetProductStoreIdAsync(context);
            if (storeId == null)
            {
                _logger.LogInformation("无法获取应用 StoreId，回退打开商店页面");
                await Windows.System.Launcher.LaunchUriAsync(new Uri($"ms-windows-store://pdp/?ProductId={ProductId}"));
            }
            else
            {
                var result = await context.RequestPurchaseAsync(storeId);
                _logger.LogInformation("购买请求返回 {Status}{Error}", result.Status,
                    result.ExtendedError == null ? "" : $"（{result.ExtendedError.Message}）");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "购买请求失败，回退打开商店页面");
            try { await Windows.System.Launcher.LaunchUriAsync(new Uri($"ms-windows-store://pdp/?ProductId={ProductId}")); }
            catch { }
        }
        await RefreshAsync();
    }

    private static async Task<string?> GetProductStoreIdAsync(StoreContext context)
    {
        try
        {
            var result = await context.GetStoreProductForCurrentAppAsync();
            return result.Product?.StoreId;
        }
        catch
        {
            return null;
        }
    }

    private void ApplyState(AppLicenseState next, DateTimeOffset? expiration)
    {
        var previous = State;
        var previousExpiration = TrialExpiration;
        State = next;
        TrialExpiration = expiration;
        int? remainingDays = TrialRemainingDays;
        bool detailsChanged = previousExpiration != expiration || _lastNotifiedDays != remainingDays;
        _lastNotifiedDays = remainingDays;
        if (previous == next)
        {
            if (detailsChanged) RaiseStateChanged();
            return;
        }
        switch (next)
        {
            case AppLicenseState.FullLicense:
                _logger.LogInformation("许可状态迁移：{Previous} → {State}", previous, next);
                break;
            case AppLicenseState.TrialActive:
                _logger.LogInformation("许可状态迁移：{Previous} → {State}，{Expiration} 到期（剩 {Days} 天）",
                    previous, next, expiration, TrialRemainingDays);
                break;
            case AppLicenseState.LicenseInactive:
                _logger.LogInformation("许可状态迁移：{Previous} → {State}，受限功能：{Features}", previous, next, RestrictedFeatures);
                break;
            default:
                _logger.LogInformation("许可状态迁移：{Previous} → {State}（Store 状态未知，按受限处理）", previous, next);
                break;
        }
        RaiseStateChanged();
    }

    private void RaiseStateChanged()
    {
        // .NET 9+ 的枚举器不创建 GetInvocationList 数组，并保留逐订阅者异常隔离。
        foreach (Action handler in Delegate.EnumerateInvocationList(StateChanged))
        {
            try { handler(); }
            catch (Exception ex) { _logger.LogWarning(ex, "许可状态订阅者失败"); }
        }
    }

    /// <summary>在 UI 线程停止时钟并解除 Store 订阅；在途查询完成后不再发布状态。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_refreshTimer is { } refresh) { refresh.Stop(); refresh.Tick -= OnRefreshTick; }
        if (_trialClockTimer is { } clock) { clock.Stop(); clock.Tick -= OnTrialClockTick; }
        if (_context is { } context) context.OfflineLicensesChanged -= OnOfflineLicensesChanged;
        StateChanged = null;
    }
}
