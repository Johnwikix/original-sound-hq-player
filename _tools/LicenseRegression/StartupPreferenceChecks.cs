using BassPlayerIpc.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Dispatching;
using Windows.Services.Store;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.State;
using WinUIMusicPlayer.ViewModel;

/// <summary>验证真实输出 ViewModel、许可、快照捕获和文件保存边界的启动与恢复顺序。</summary>
internal static class StartupPreferenceChecks
{
    public static async Task RunAsync()
    {
        string directory = Path.Combine(Environment.GetEnvironmentVariable("LICENSE_REGRESSION_ROOT") ?? Path.GetTempPath(), "license-preferences-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            // Each feature is independently saved on/off, across full, trial, inactive and failed queries.
            for (int outcome = 0; outcome < 4; outcome++)
                for (int flags = 0; flags < 8; flags++)
                    await CheckStartupAsync(Path.Combine(directory, $"{outcome}-{flags}.json"), flags, outcome);
            for (int flags = 0; flags < 8; flags++)
                await CheckExpirationPersistenceAsync(Path.Combine(directory, $"expired-{flags}.json"), flags);
        }
        finally { Directory.Delete(directory, recursive: true); }
        Console.WriteLine("PASS: 32 startup + 8 trial-expiration cases preserve every saved output flag through gated edits, production capture/save/reload, offline expiration and purchase recovery.");
    }

    private static async Task CheckStartupAsync(string path, int flags, int outcome)
    {
        var store = new AudioSettingsStore(path);
        var saved = await store.LoadAsync(CreatePreferences(flags));
        var pending = new TaskCompletionSource<StoreAppLicense>(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = StoreContext.Current = new() { Query = () => pending.Task };
        using var license = new LicenseService(NullLogger<LicenseService>.Instance);
        var state = new AppState();
        var vm = new AppViewModel(license, state);
        var capture = new SettingsSnapshotFactory(state);
        AppSettings.Dsp = saved.Dsp;
        AppSettings.OutputMode = saved.OutputMode;
        bool wrongThread = false;
        var notified = new HashSet<string>();
        vm.PropertyChanged += (_, e) =>
        {
            wrongThread |= !DispatcherQueue.Current.HasThreadAccess;
            if (e.PropertyName is { } name) notified.Add(name);
        };

        // Production startup restores the file before starting the first Store lookup.
        vm.RestoreOutputPreferences(saved);
        AssertSaved(vm, saved, "restoration before Store lookup");
        Require(!saved.IsDopEnabled || notified.Contains(nameof(vm.IsDopEnabled)), "DSD binding missed restoration");
        Require(!saved.ExperimentalSurround51 || notified.Contains(nameof(vm.ExperimentalSurround51)), "5.1 binding missed restoration");
        Require(!saved.ExperimentalAtmosPassthrough || notified.Contains(nameof(vm.ExperimentalAtmosPassthrough)), "Atmos binding missed restoration");
        Task initialization = license.InitializeAsync();
        Require(!initialization.IsCompleted, "First query did not remain asynchronous");
        RejectEdits(vm, saved);
        AssertEffective(license, saved, restricted: true);
        await SaveAndReloadAsync(store, capture, saved, 301);

        notified.Clear();
        if (outcome == 3) pending.SetException(new IOException("First Store query failed"));
        else pending.SetResult(new StoreAppLicense
        {
            IsActive = outcome != 2,
            IsTrial = outcome == 1,
            ExpirationDate = DateTimeOffset.UtcNow.AddDays(3)
        });
        await initialization;
        bool restricted = outcome >= 2;
        Require(license.IsRestricted == restricted, "Unexpected query outcome");
        AssertSaved(vm, saved, "lookup completion");
        AssertEffective(license, saved, restricted);
        if (restricted) RejectEdits(vm, saved);
        else
        {
            Require(notified.Contains(nameof(vm.DsdBitstreamAllowed))
                && notified.Contains(nameof(vm.Surround51Allowed))
                && notified.Contains(nameof(vm.AtmosPassthroughAllowed)), "Unlock did not notify the UI");
            vm.IsDopEnabled = !saved.IsDopEnabled;
            vm.ExperimentalSurround51 = !saved.ExperimentalSurround51;
            vm.ExperimentalAtmosPassthrough = !saved.ExperimentalAtmosPassthrough;
            Require(vm.IsDopEnabled != saved.IsDopEnabled && vm.ExperimentalSurround51 != saved.ExperimentalSurround51
                && vm.ExperimentalAtmosPassthrough != saved.ExperimentalAtmosPassthrough, "Allowed user edits were rejected");
            vm.RestoreOutputPreferences(saved);
        }
        await SaveAndReloadAsync(store, capture, saved, 302);

        // Same shared command as the settings UI, including recovery from initial lookup failure.
        context.Query = () => Task.FromResult(new StoreAppLicense { IsActive = true });
        await vm.PurchaseLicenseCommand.ExecuteAsync(null);
        Require(!license.IsRestricted && !vm.CanPurchaseLicense, "Purchase did not recover the license");
        AssertSaved(vm, saved, "purchase recovery");
        AssertEffective(license, saved, restricted: false);
        await SaveAndReloadAsync(store, capture, saved, 303);
        Require(!wrongThread, "Preference or license binding notification left the UI thread");
    }

    private static async Task CheckExpirationPersistenceAsync(string path, int flags)
    {
        var store = new AudioSettingsStore(path);
        var saved = await store.LoadAsync(CreatePreferences(flags));
        var clock = new TestClock();
        var context = StoreContext.Current = new()
        {
            Query = () => Task.FromResult(new StoreAppLicense { IsActive = true, IsTrial = true,
                ExpirationDate = clock.GetUtcNow().AddMinutes(1) })
        };
        using var license = new LicenseService(NullLogger<LicenseService>.Instance, clock);
        var state = new AppState();
        var vm = new AppViewModel(license, state);
        var capture = new SettingsSnapshotFactory(state);
        AppSettings.Dsp = saved.Dsp;
        vm.RestoreOutputPreferences(saved);
        await license.InitializeAsync();
        Require(!license.IsRestricted, "Initial trial was not active");
        clock.Advance(TimeSpan.FromMinutes(1));
        DispatcherQueue.Current.Timers.Last(t => t.Interval == TimeSpan.FromMinutes(1)).Fire();
        context.Query = () => Task.FromException<StoreAppLicense>(new IOException("Expired trial offline"));
        await license.RefreshAsync();
        Require(license.IsRestricted && vm.LicenseRestricted, "Expiration did not publish to the real license ViewModel");
        RejectEdits(vm, saved);
        AssertEffective(license, saved, restricted: true);
        await SaveAndReloadAsync(store, capture, saved, 401);
        context.Query = () => Task.FromResult(new StoreAppLicense { IsActive = true });
        await vm.PurchaseLicenseCommand.ExecuteAsync(null);
        Require(!license.IsRestricted && !vm.LicenseRestricted, "Purchase did not publish to the real license ViewModel");
        AssertSaved(vm, saved, "purchase after offline expiration");
        AssertEffective(license, saved, restricted: false);
        await SaveAndReloadAsync(store, capture, saved, 402);
    }

    private static AudioPreferences CreatePreferences(int flags) => new()
    {
        IsDopEnabled = (flags & 1) != 0,
        ExperimentalSurround51 = (flags & 2) != 0,
        ExperimentalAtmosPassthrough = (flags & 4) != 0,
        AtmosEndpointId = "",
        Dsp = new DspSettings { IsEnabled = true, ConvolutionEnabled = true, ImpulsePath = "saved.wav", NormalizeLoudness = true }
    };

    private static void RejectEdits(AppViewModel vm, AudioPreferences saved)
    {
        vm.IsDopEnabled = !saved.IsDopEnabled;
        vm.ExperimentalSurround51 = !saved.ExperimentalSurround51;
        vm.ExperimentalAtmosPassthrough = !saved.ExperimentalAtmosPassthrough;
        AssertSaved(vm, saved, "rejected licensed edit (both on and off)");
    }

    private static void AssertSaved(AppViewModel vm, AudioPreferences saved, string phase)
        => Require(vm.IsDopEnabled == saved.IsDopEnabled && vm.ExperimentalSurround51 == saved.ExperimentalSurround51
            && vm.ExperimentalAtmosPassthrough == saved.ExperimentalAtmosPassthrough, $"Saved flags changed during {phase}");

    private static void AssertEffective(LicenseService license, AudioPreferences saved, bool restricted)
    {
        var output = new IpcSetting { IsDopEnabled = saved.IsDopEnabled, ExperimentalSurround51 = saved.ExperimentalSurround51,
            ExperimentalAtmosPassthrough = saved.ExperimentalAtmosPassthrough, IsEqualizerEnabled = true, Volume = .75f };
        license.ApplyOutputRestrictions(ref output);
        Require(output.IsDopEnabled == (!restricted && saved.IsDopEnabled)
            && output.ExperimentalSurround51 == (!restricted && saved.ExperimentalSurround51)
            && output.ExperimentalAtmosPassthrough == (!restricted && saved.ExperimentalAtmosPassthrough)
            && output.IsEqualizerEnabled && output.Volume == .75f, "Output overlay changed saved/free settings");
        var dsp = LicensePolicy.ApplyDspRestrictions(saved.Dsp, license.RestrictedFeatures);
        Require(dsp.ConvolutionEnabled == !restricted && dsp.NormalizeLoudness == saved.Dsp.NormalizeLoudness
            && saved.Dsp.ConvolutionEnabled && saved.Dsp.ImpulsePath == "saved.wav", "DSP overlay changed saved/free effects");
    }

    private static async Task SaveAndReloadAsync(AudioSettingsStore store, SettingsSnapshotFactory capture, AudioPreferences saved, int latency)
    {
        // Force a real write even when the three saved flags are unchanged.
        var snapshot = capture.CaptureAudio() with { Latency = latency };
        await store.SaveAsync(snapshot);
        var loaded = await store.LoadAsync(new());
        Require(loaded == saved with { Latency = latency }, "Capturing/saving a restricted session overwrote persisted preferences");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
