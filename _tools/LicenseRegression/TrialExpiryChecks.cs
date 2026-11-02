using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Dispatching;
using Windows.Services.Store;
using WinUIMusicPlayer.Services;

/// <summary>验证本地试用到期、迟到缓存、查询失败和重新购买之间的许可状态迁移。</summary>
internal static class TrialExpiryChecks
{
    public static async Task RunAsync()
    {
        var clock = new TestClock();
        var expiration = clock.GetUtcNow().AddMinutes(1);
        var trial = new StoreAppLicense { IsActive = true, IsTrial = true, ExpirationDate = expiration };
        var context = StoreContext.Current = new() { Query = () => Task.FromResult(trial) };
        using var license = new LicenseService(NullLogger<LicenseService>.Instance, clock);
        int changes = 0;
        bool wrongThread = false;
        license.StateChanged += () => { changes++; wrongThread |= !DispatcherQueue.Current.HasThreadAccess; };
        await license.InitializeAsync();
        var timer = DispatcherQueue.Current.Timers.Last(t => t.Interval == TimeSpan.FromMinutes(1));
        int before = changes;
        int queries = context.Queries;
        clock.Advance(TimeSpan.FromSeconds(59));
        timer.Fire();
        Require(license.State == AppLicenseState.TrialActive && changes == before, "Trial expired before its deadline");

        var pending = new TaskCompletionSource<StoreAppLicense>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Query = () => pending.Task;
        Task refresh = license.RefreshAsync();
        clock.Advance(TimeSpan.FromSeconds(1));
        timer.Fire();
        Require(license.State == AppLicenseState.LicenseInactive && license.IsRestricted && changes == before + 1,
            "Known trial deadline did not restrict locally while Store query was pending");
        Require(context.Queries == queries + 1, "Trial clock performed Store IO");
        timer.Fire();
        Require(changes == before + 1, "Expired trial emitted repeated notifications");
        pending.SetResult(trial);
        await refresh;
        Require(license.IsRestricted && changes == before + 1, "Late expired cache resurrected trial access");

        context.Query = () => Task.FromException<StoreAppLicense>(new IOException("Store offline"));
        await Task.Run(license.RefreshAsync);
        Require(license.IsRestricted, "Failure restored expired trial access");
        var renewed = new StoreAppLicense { IsActive = true, IsTrial = true, ExpirationDate = clock.GetUtcNow().AddDays(1) };
        context.Query = () => Task.FromResult(renewed);
        await license.RefreshAsync();
        Require(!license.IsRestricted, "Valid renewed trial did not unlock");
        clock.Advance(TimeSpan.FromDays(1));
        context.Query = () => Task.FromException<StoreAppLicense>(new IOException("Failure after deadline, before timer tick"));
        await license.RefreshAsync();
        Require(license.IsRestricted, "Failed lookup retained a known expired trial until another timer tick");
        context.Query = () => Task.FromResult(new StoreAppLicense { IsActive = true });
        await license.PurchaseAsync();
        Require(license.State == AppLicenseState.FullLicense && !license.IsRestricted, "Purchase after expiration did not unlock");
        clock.Advance(TimeSpan.FromDays(10));
        context.Query = () => Task.FromException<StoreAppLicense>(new IOException("Full license offline"));
        timer.Fire();
        await license.RefreshAsync();
        Require(license.State == AppLicenseState.FullLicense && !license.IsRestricted, "Offline lookup revoked confirmed full license");
        Require(!wrongThread, "Expiration or purchase notification left UI thread");

        // The initial cache can already be expired even when Store still reports IsActive.
        context = StoreContext.Current = new() { Query = () => Task.FromResult(trial) };
        using var cached = new LicenseService(NullLogger<LicenseService>.Instance, clock);
        await cached.InitializeAsync();
        Require(cached.State == AppLicenseState.LicenseInactive && cached.IsRestricted, "Expired initial cache unlocked features");
        var future = new StoreAppLicense { IsActive = true, IsTrial = true, ExpirationDate = clock.GetUtcNow().AddMinutes(1) };
        context = StoreContext.Current = new() { Query = () => Task.FromResult(future) };
        using var closing = new LicenseService(NullLogger<LicenseService>.Instance, clock);
        await closing.InitializeAsync();
        var failedLate = new TaskCompletionSource<StoreAppLicense>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Query = () => failedLate.Task;
        Task closingRefresh = closing.RefreshAsync();
        clock.Advance(TimeSpan.FromMinutes(1));
        closing.Dispose();
        failedLate.SetException(new IOException("Query failed after shutdown"));
        await closingRefresh;
        Require(closing.State == AppLicenseState.TrialActive, "Failure path published expiration after disposal");
        Console.WriteLine("PASS: exact trial deadline, pending/late cached results, failed/background lookup, renewed trial, purchase, full-license retention, expired initial cache and late failure after disposal.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
