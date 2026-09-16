using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Windows.ApplicationModel;
using Windows.Services.Store;

namespace WinUIMusicPlayer.Services;

/// <summary>许可状态；受限 = Store 确认试用已到期。</summary>
public enum AppLicenseState
{
    /// <summary>非 Store 渠道或查询失败：不限制任何功能（fail-open）。</summary>
    StoreUnavailable,
    /// <summary>已购完整版。</summary>
    FullLicense,
    /// <summary>试用期内。</summary>
    TrialActive,
    /// <summary>试用已到期：限制非 EQ 的 DSP 与 DSD 位流。</summary>
    TrialExpired
}

/// <summary>Store 试用许可状态源：到期后限制非 EQ 的 DSP 与 DSD 位流；
/// 非 Store 渠道和查询失败一律 fail-open 不限制，判定与迁移过程全部写入日志。</summary>
public sealed class LicenseService
{
    private const string ProductId = "9NFW1RPPT999";
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(30);

    private readonly ILogger<LicenseService> _logger;
    private readonly DispatcherQueue? _queue;
    private StoreContext? _context;
    private string? _productStoreId;
    private Timer? _refreshTimer;
    private int _refreshing;

    public AppLicenseState State { get; private set; } = AppLicenseState.StoreUnavailable;
    /// <summary>试用到期时间；仅 <see cref="AppLicenseState.TrialActive"/> 时有意义。</summary>
    public DateTimeOffset? TrialExpiration { get; private set; }
    /// <summary>是否限制非 EQ 的 DSP 与 DSD 位流。</summary>
    public bool IsRestricted => State == AppLicenseState.TrialExpired;
    /// <summary>试用剩余天数（不足一天按 1 计）；非试用期内为 null。</summary>
    public int? TrialRemainingDays => State == AppLicenseState.TrialActive && TrialExpiration is { } expiration
        ? Math.Max(1, (int)Math.Ceiling((expiration - DateTimeOffset.Now).TotalDays))
        : null;
    /// <summary>许可状态变化后在 UI 线程触发。</summary>
    public event Action? StateChanged;

    public LicenseService(ILogger<LicenseService> logger)
    {
        _logger = logger;
        _queue = DispatcherQueue.GetForCurrentThread();
    }

    /// <summary>启动时判定许可状态；必须在首次向播放进程推送设置前完成。</summary>
    public async Task InitializeAsync()
    {
        #if DEBUG
        if (TryApplyDebugOverride()) return;
        #endif
        if (Package.Current.SignatureKind != PackageSignatureKind.Store)
        {
            _logger.LogInformation("包签名类型为 {Kind}，非 Store 渠道，许可门控不生效（fail-open）", Package.Current.SignatureKind);
            return;
        }
        try
        {
            _context = StoreContext.GetDefault();
            _context.OfflineLicensesChanged += (_, _) => _ = RefreshAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "StoreContext 初始化失败，许可门控不生效（fail-open）");
            return;
        }
        await RefreshAsync();
        // 时间型试用到期依赖本地许可缓存的时间判定：定时重查覆盖应用长期运行的场景。
        _refreshTimer = new Timer(_ => _ = RefreshAsync(), null, RefreshInterval, RefreshInterval);
    }

    #if DEBUG
    /// <summary>本地验证门控的 DEBUG 逃生门：读取 Documents\OriginalSoundPlayer\license.debug 文件
    /// （内容 full/trial/expired，删除即恢复真实判定）。MsixPackage 调试激活不传递
    /// launchSettings 环境变量，文件方式不依赖启动机制。</summary>
    private bool TryApplyDebugOverride()
    {
        string? source = ReadLicenseDebugFile();
        if (string.IsNullOrWhiteSpace(source)) return false;
        switch (source)
        {
            case "full":
                _logger.LogInformation("DEBUG 覆盖许可状态：完整版");
                ApplyState(AppLicenseState.FullLicense, null);
                return true;
            case "trial":
                var trialEnd = DateTimeOffset.Now.AddDays(3);
                _logger.LogInformation("DEBUG 覆盖许可状态：试用中（{Expiration} 到期）", trialEnd);
                ApplyState(AppLicenseState.TrialActive, trialEnd);
                return true;
            case "expired":
                _logger.LogInformation("DEBUG 覆盖许可状态：试用已到期");
                ApplyState(AppLicenseState.TrialExpired, null);
                return true;
            default:
                _logger.LogWarning("DEBUG 许可覆盖值无效：{Value}（可用：full/trial/expired）", source);
                return false;
        }
    }

    private static string? ReadLicenseDebugFile()
    {
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
    #endif

    /// <summary>重查许可；失败时记录日志并保持当前状态（初始状态即 fail-open 的 StoreUnavailable）。</summary>
    public async Task RefreshAsync()
    {
        if (_context == null) return;
        if (Interlocked.Exchange(ref _refreshing, 1) == 1) return;
        try
        {
            StoreAppLicense license = await _context.GetAppLicenseAsync();
            if (!license.IsActive)
            {
                ApplyState(AppLicenseState.TrialExpired, null);
                return;
            }
            if (license.IsTrial) ApplyState(AppLicenseState.TrialActive, license.ExpirationDate);
            else ApplyState(AppLicenseState.FullLicense, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Store 许可查询失败，保持当前状态（{State}，fail-open）", State);
        }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    /// <summary>弹出 Store 购买对话框；无法取得 StoreId 时回退打开商店页面。完成后强制刷新许可。</summary>
    public async Task PurchaseAsync()
    {
        try
        {
            var context = _context ??= StoreContext.GetDefault();
            if (App.MainWindow is { } window)
                WinRT.Interop.InitializeWithWindow.Initialize(context, WinRT.Interop.WindowNative.GetWindowHandle(window));
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
        if (next == State)
        {
            TrialExpiration = expiration;
            return;
        }
        State = next;
        TrialExpiration = expiration;
        switch (next)
        {
            case AppLicenseState.FullLicense:
                _logger.LogInformation("许可状态迁移：{Previous} → 完整版", previous);
                break;
            case AppLicenseState.TrialActive:
                _logger.LogInformation("许可状态迁移：{Previous} → 试用中，{Expiration} 到期（剩 {Days} 天）",
                    previous, expiration, TrialRemainingDays);
                break;
            case AppLicenseState.TrialExpired:
                _logger.LogInformation("许可状态迁移：{Previous} → 试用已到期，非 EQ 的 DSP 与 DSD 位流已限制", previous);
                break;
            default:
                _logger.LogInformation("许可状态迁移：{Previous} → Store 不可用（fail-open）", previous);
                break;
        }
        RaiseStateChanged();
    }

    private void RaiseStateChanged()
    {
        if (StateChanged is not { } handlers) return;
        if (_queue is { } queue && !queue.HasThreadAccess)
        {
            queue.TryEnqueue(() =>
            {
                foreach (Action handler in handlers.GetInvocationList())
                {
                    try { handler(); }
                    catch (Exception ex) { _logger.LogWarning(ex, "许可状态订阅者失败"); }
                }
            });
            return;
        }
        foreach (Action handler in handlers.GetInvocationList())
        {
            try { handler(); }
            catch (Exception ex) { _logger.LogWarning(ex, "许可状态订阅者失败"); }
        }
    }
}
