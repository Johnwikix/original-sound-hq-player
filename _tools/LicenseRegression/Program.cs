using Microsoft.Extensions.Logging.Abstractions;
using BassPlayerIpc.Shared;
using Microsoft.UI.Dispatching;
using Windows.ApplicationModel;
using Windows.Services.Store;
using WinUIMusicPlayer.Services;

var ui = new TestUiContext();
SynchronizationContext.SetSynchronizationContext(ui);
ui.Run(RunTestsAsync());

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static async Task RunTestsAsync()
{
    var clock = new TestClock();
    var trial = new StoreAppLicense { IsActive = true, IsTrial = true, ExpirationDate = clock.GetUtcNow().AddDays(3) };
    var full = new StoreAppLicense { IsActive = true };
    var inactive = new StoreAppLicense();
    var context = StoreContext.Current = new();
    context.Query = () => Task.FromResult(trial);
    using var service = new LicenseService(NullLogger<LicenseService>.Instance, clock);
    int notifications = 0;
    service.StateChanged += () =>
    {
        Check(DispatcherQueue.Current.HasThreadAccess, "Notification left UI thread");
        notifications++;
    };
    await service.InitializeAsync();
    var preferences = new IpcSetting { IsDopEnabled = true, ExperimentalSurround51 = true,
        ExperimentalAtmosPassthrough = true, IsEqualizerEnabled = true, Volume = 0.75f,
        OutputMode = "WasapiExclusiveEvent" };
    var effective = preferences;
    service.ApplyOutputRestrictions(ref effective);
    Check(effective.IsDopEnabled && effective.ExperimentalSurround51 && effective.ExperimentalAtmosPassthrough,
        "Active trial restricted advanced output");
    Check(context.WindowInitialized && context.Queries == 1, "First query did not use initialized context");
    Check(service.CanPurchase && !service.IsRestricted && service.TrialRemainingDays == 3, "Trial purchase eligibility");
    await service.RefreshAsync();
    Check(context.Queries == 2 && notifications == 1, "Synchronous cache completion stuck or emitted duplicate notification");
    Console.WriteLine("PASS: HWND before query, synchronous completion, trial purchase eligibility.");

    // An old query remains pending while both the Store event and purchase completion request refreshes.
    var oldQuery = new TaskCompletionSource<StoreAppLicense>();
    var freshQuery = new TaskCompletionSource<StoreAppLicense>();
    context.Query = () => oldQuery.Task;
    Task backgroundRefresh = service.RefreshAsync();
    context.Purchase = () => { context.Query = () => freshQuery.Task; context.RaiseChanged(); };
    Task purchase = service.PurchaseAsync();
    Check(!purchase.IsCompleted, "Purchase did not wait for post-purchase refresh");
    oldQuery.SetResult(inactive);
    await Task.Yield();
    Check(context.Queries == 4 && !purchase.IsCompleted, "Overlapping requests were dropped or not coalesced");
    freshQuery.SetResult(full);
    await Task.WhenAll(backgroundRefresh, purchase);
    Check(service.State == AppLicenseState.FullLicense && !service.CanPurchase && !service.IsRestricted, "Purchase retained stale restriction");
    effective = preferences;
    service.ApplyOutputRestrictions(ref effective);
    Check(effective.IsDopEnabled && effective.ExperimentalSurround51 && effective.ExperimentalAtmosPassthrough,
        "Purchase did not restore all advanced output preferences");
    Console.WriteLine("PASS: in-flight old license + Store event + purchase coalesce, await fresh result and unlock.");

    context.Query = () => Task.FromResult(inactive);
    await Task.Run(service.RefreshAsync);
    Check(service.IsRestricted && service.CanPurchase, "Background refresh or inactive license failed");
    Check(service.IsFeatureRestricted(LicenseFeature.Convolution)
        && !service.IsFeatureRestricted(LicenseFeature.LoudnessNormalization | LicenseFeature.Preamp | LicenseFeature.Crossfeed),
        "Inactive license must only gate convolution within DSP");
    var dsp = new DspSettings { IsEnabled = true, ConvolutionEnabled = true, ImpulsePath = "saved.wav",
        CurvePresetName = "saved", NormalizeLoudness = true, TargetLufs = -16, AutoPreamp = true,
        HeadroomDb = -4, Balance = .3, SwapChannels = true, Mono = true, Crossfeed = .4, StereoWidth = 1.2 };
    var gatedDsp = LicensePolicy.ApplyDspRestrictions(dsp, service.RestrictedFeatures);
    Check(gatedDsp == dsp with { ConvolutionEnabled = false }, "Convolution gate changed a free DSP setting");
    Check(dsp.ConvolutionEnabled && dsp.ImpulsePath == "saved.wav", "Gate overwrote persisted convolution preferences");
    var reset = LicensePolicy.PreserveRestrictedPreferences(new(), dsp, service.RestrictedFeatures);
    Check(reset.ConvolutionEnabled && reset.ImpulsePath == dsp.ImpulsePath && reset.CurvePresetName == dsp.CurvePresetName
        && !reset.NormalizeLoudness && reset.Balance == 0 && reset.StereoWidth == 1,
        "Reset must reset free effects while retaining locked convolution preferences");
    Check(ReferenceEquals(dsp, LicensePolicy.ApplyDspRestrictions(dsp, LicenseFeature.None)),
        "Unlocked DSP should retain its original snapshot");
    foreach (var feature in new[] { LicenseFeature.LoudnessNormalization, LicenseFeature.Preamp, LicenseFeature.Balance,
        LicenseFeature.ChannelSwap, LicenseFeature.Mono, LicenseFeature.Crossfeed, LicenseFeature.StereoWidth })
    {
        var effectiveDsp = LicensePolicy.ApplyDspRestrictions(dsp, feature);
        Check(effectiveDsp.ConvolutionEnabled && effectiveDsp != dsp, "Adding a basic DSP feature must not disable convolution");
        Check(LicensePolicy.PreserveRestrictedPreferences(effectiveDsp, dsp, feature) == dsp,
            "Every configurable DSP group must preserve its saved values");
    }
    Console.WriteLine("PASS: convolution-only DSP overlay, free effects, reset preservation, configurable feature groups.");
    effective = preferences;
    service.ApplyOutputRestrictions(ref effective);
    Check(!effective.IsDopEnabled && !effective.ExperimentalSurround51 && !effective.ExperimentalAtmosPassthrough,
        "Inactive license did not gate all advanced output modes");
    Check(preferences.IsDopEnabled && preferences.ExperimentalSurround51 && preferences.ExperimentalAtmosPassthrough
        && effective.IsEqualizerEnabled && effective.Volume == preferences.Volume && effective.OutputMode == preferences.OutputMode,
        "Output gate changed saved preferences or unrelated playback settings");
    var onlyDsd = preferences;
    LicensePolicy.ApplyOutputRestrictions(ref onlyDsd, LicenseFeature.DsdBitstream);
    Check(!onlyDsd.IsDopEnabled && onlyDsd.ExperimentalSurround51 && onlyDsd.ExperimentalAtmosPassthrough,
        "Removing output features from the policy must restore them independently");
    Console.WriteLine("PASS: DSD/5.1/Atmos output gate, full/trial restoration and preference preservation.");
    context.Query = () => throw new IOException("Store unavailable");
    await service.RefreshAsync();
    Check(service.IsRestricted, "Query failure changed known restriction");
    context.Query = () => Task.FromResult(trial);
    await service.RefreshAsync();
    int before = notifications, queries = context.Queries;
    clock.Advance(TimeSpan.FromDays(1));
    var timer = DispatcherQueue.Current.Timers.Single(t => t.Interval == TimeSpan.FromMinutes(1));
    timer.Fire();
    Check(service.TrialRemainingDays == 2 && notifications == before + 1 && context.Queries == queries, "Countdown did not update independently of Store/DSP");
    timer.Fire();
    Check(notifications == before + 1, "Unchanged countdown emitted duplicate event");
    context.Query = () => Task.FromResult(new StoreAppLicense { IsActive = true, IsTrial = true, ExpirationDate = trial.ExpirationDate.AddDays(1) });
    await service.RefreshAsync();
    Check(notifications == before + 2 && service.TrialRemainingDays == 3, "Changed trial expiration was not published");
    Console.WriteLine("PASS: background marshaling, failure retention, countdown without IO, expiration notification.");

    var late = new TaskCompletionSource<StoreAppLicense>();
    context.Query = () => late.Task;
    Task inFlight = service.RefreshAsync();
    service.Dispose();
    before = notifications;
    late.SetResult(inactive);
    await inFlight;
    Check(context.Subscribers == 0 && DispatcherQueue.Current.Timers.All(t => !t.Running), "Dispose retained callbacks");
    Check(notifications == before && service.State == AppLicenseState.TrialActive, "Late result published after disposal");
    Console.WriteLine("PASS: disposal stops timers/unsubscribes and ignores in-flight results.");

    context = StoreContext.Current = new();
    context.Query = () => throw new IOException("First query unavailable");
    using var unavailable = new LicenseService(NullLogger<LicenseService>.Instance, clock);
    await unavailable.InitializeAsync();
    Check(unavailable.State == AppLicenseState.StoreUnavailable && !unavailable.IsRestricted, "Initial failure did not fail open");
    unavailable.Dispose();
    Package.Current.SignatureKind = PackageSignatureKind.Developer;
    context = StoreContext.Current = new();
    using var developer = new LicenseService(NullLogger<LicenseService>.Instance, clock);
    await developer.InitializeAsync();
    Check(context.Queries == 0 && !developer.CanPurchase && !developer.IsRestricted, "Developer package contacted Store or became restricted");
    Console.WriteLine("PASS: initial failure and non-Store package policy.");
}

sealed class TestClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 9, 16, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan delta) => _now += delta;
}
