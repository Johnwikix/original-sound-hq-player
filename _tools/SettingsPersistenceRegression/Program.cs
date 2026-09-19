using BassPlayerIpc.Shared;
using System.Text.Json;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
static async Task Reject<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}.");
}

string directory = Path.Combine(Path.GetTempPath(), "audio-settings-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    const string legacyJson = """
        {"Dsp":{"ConvolutionEnabled":true,"ConvolutionSource":1,"CurvePoints":"20,-4;20000,-4","AutoPreamp":false,"HeadroomDb":-7},
         "OutputMode":"WasapiExclusiveEvent","Latency":137,"BassOutputDeviceId":9,"WasapiEndpointId":"endpoint-a",
         "BassASIODeviceId":3,"DeviceFriendlyName":"USB DAC","IsFadeEnabled":true,"IsDopEnabled":true,
         "ExperimentalSurround51":true,"ExperimentalAtmosPassthrough":true,"DsdGain":12,"DsdPcmFreq":176400,
         "AppTheme":"Dark","AppWidth":1450,"DefualtEntry":"song","PlayOrPauseShortcut":["Ctrl","P"]}
        """;
    var defaults = JsonSerializer.Deserialize("{}", SettingsJsonContext.Default.SaveSettings)!;
    Check(defaults.IsHoverScrollEnabled, "Existing settings without hover-scroll key must keep hover enabled.");
    defaults.IsHoverScrollEnabled = false;
    var hoverRoundTrip = JsonSerializer.Deserialize(JsonSerializer.Serialize(defaults, SettingsJsonContext.Default.SaveSettings), SettingsJsonContext.Default.SaveSettings)!;
    Check(!hoverRoundTrip.IsHoverScrollEnabled, "Disabled hover-scroll must survive saving and restarting.");
    Console.WriteLine("PASS: hover-scroll default and disabled setting round-trip.");
    var general = JsonSerializer.Deserialize(legacyJson, SettingsJsonContext.Default.SaveSettings)!;
    var legacy = general.ReadLegacyAudioPreferences();
    Check(legacy.OutputMode == "WasapiExclusiveEvent" && legacy.Latency == 137 && legacy.BassOutputDeviceId == 9
        && legacy.WasapiEndpointId == "endpoint-a" && legacy.BassASIODeviceId == 3 && legacy.DeviceFriendlyName == "USB DAC"
        && legacy.IsFadeEnabled && legacy.IsDopEnabled && legacy.ExperimentalSurround51 && legacy.ExperimentalAtmosPassthrough
        && legacy.DsdGain == 12 && legacy.DsdPcmFreq == 176400 && legacy.Dsp.HeadroomDb == -7
        && legacy.Dsp.ConvolutionEnabled && legacy.Dsp.CurvePoints == "20,-4;20000,-4", "Audio migration lost a field.");
    Check(new SaveSettings().ReadLegacyAudioPreferences() == new AudioPreferences(), "Missing legacy keys changed defaults.");

    string path = Path.Combine(directory, "AudioSettings.json");
    var store = new AudioSettingsStore(path);
    Check(await store.LoadAsync(legacy) == legacy, "Initial migration changed audio preferences.");
    Check(general.HasLegacyAudioPreferences(), "Legacy fields were removed before committing.");
    general.ClearLegacyAudioPreferences();
    string cleaned = JsonSerializer.Serialize(general, SettingsJsonContext.Default.SaveSettings);
    using (var document = JsonDocument.Parse(cleaned))
    {
        string[] audioKeys = ["Dsp", "OutputMode", "Latency", "BassOutputDeviceId", "WasapiEndpointId", "BassASIODeviceId",
            "DeviceFriendlyName", "IsFadeEnabled", "IsDopEnabled", "ExperimentalSurround51", "ExperimentalAtmosPassthrough", "DsdGain", "DsdPcmFreq"];
        foreach (string key in audioKeys) Check(!document.RootElement.TryGetProperty(key, out _), $"General settings still write {key}.");
    }
    var restoredGeneral = JsonSerializer.Deserialize(cleaned, SettingsJsonContext.Default.SaveSettings)!;
    Check(restoredGeneral.AppTheme == "Dark" && restoredGeneral.AppWidth == 1450 && restoredGeneral.DefaultEntry == "song"
        && restoredGeneral.PlayOrPauseShortcut.SequenceEqual(new[] { "Ctrl", "P" }), "Audio migration changed unrelated preferences.");
    Check(await new AudioSettingsStore(path).LoadAsync(new()) == legacy, "Restart remigrated over saved audio preferences.");
    Console.WriteLine("PASS: every audio field migrates once; general settings retain UI preferences and stop writing audio keys.");

    var atmosPreferences = legacy with { AtmosEndpointId = "hdmi-dedicated", OutputMode = "ASIO" };
    await store.SaveAsync(atmosPreferences);
    var restoredAtmos = await new AudioSettingsStore(path).LoadAsync(new());
    Check(restoredAtmos.AtmosEndpointId == "hdmi-dedicated" && restoredAtmos.OutputMode == "ASIO"
        && restoredAtmos.ExperimentalAtmosPassthrough, "Dedicated HDMI did not persist independently of ordinary ASIO output.");
    Check(legacy.AtmosEndpointId == null, "Old preferences unexpectedly selected a dedicated endpoint.");
    await store.SaveAsync(legacy);
    Console.WriteLine("PASS: dedicated Atmos HDMI persists without changing ordinary output; old settings keep current endpoint.");

    byte[] committed = await File.ReadAllBytesAsync(path);
    using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
    {
        await store.SaveAsync(legacy with { }); // Must not touch a locked file for unchanged preferences.
        await Reject<IOException>(() => store.SaveAsync(legacy with { Latency = 250 }));
    }
    Check((await File.ReadAllBytesAsync(path)).SequenceEqual(committed), "Failed save damaged the committed file.");
    await store.SaveAsync(legacy with { Latency = 250 });
    Check((await new AudioSettingsStore(path).LoadAsync(new())).Latency == 250, "Retry did not persist the pending change.");
    Check(!File.Exists(path + ".bak") && Directory.GetFiles(directory, "*.tmp").Length == 0, "Save left routine backup or temporary files.");
    Console.WriteLine("PASS: unchanged snapshots skip IO; failed writes preserve data and retry succeeds without backups.");

    // The save gate must preserve caller order and keep the final full snapshot intact.
    var requests = Enumerable.Range(300, 12).Select(value => store.SaveAsync(legacy with { Latency = value })).ToArray();
    await Task.WhenAll(requests);
    Check((await new AudioSettingsStore(path).LoadAsync(new())).Latency == 311, "Concurrent saves lost the final snapshot.");
    Console.WriteLine("PASS: concurrent saves retain the final complete snapshot.");

    await File.WriteAllTextAsync(path, "{broken");
    var invalid = new AudioSettingsStore(path);
    Check(await invalid.LoadAsync(legacy) == new AudioPreferences(), "Corrupt file remigrated legacy preferences instead of defaults.");
    Check(invalid.ResetToDefaults && Directory.GetFiles(directory, "AudioSettings.json.corrupt-*").Any(p => File.ReadAllText(p) == "{broken"), "Corrupt input was not preserved.");
    await invalid.SaveAsync(legacy with { Latency = 450 });
    Check((await new AudioSettingsStore(path).LoadAsync(new())).Latency == 450, "Recovered store remained read-only across restart.");
    Check(!File.Exists(path + ".bak"), "Recovery produced a routine backup.");

    // Future schemas must be preserved even when their shape omits current required fields.
    const string future = "{\"SchemaVersion\":2}";
    await File.WriteAllTextAsync(path, future);
    await Reject<NotSupportedException>(() => invalid.LoadAsync(legacy));
    await Reject<InvalidOperationException>(() => invalid.SaveAsync(legacy));
    Check(await File.ReadAllTextAsync(path) == future, "Future schema was reset.");
    Console.WriteLine("PASS: corruption resets to writable defaults; future schemas remain protected.");

    await File.WriteAllTextAsync(path, "{broken");
    using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        await Reject<IOException>(() => invalid.LoadAsync(legacy));
        await Reject<InvalidOperationException>(() => invalid.SaveAsync(legacy));
        Check(await File.ReadAllTextAsync(path) == "{broken", "Failed default commit removed the source and permitted remigration.");
    }
    Check(await invalid.LoadAsync(legacy) == new AudioPreferences(), "Retry did not recover defaults.");
    using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
    {
        await Reject<IOException>(() => invalid.LoadAsync(legacy));
        await Reject<InvalidOperationException>(() => invalid.SaveAsync(legacy));
    }
    Check(await invalid.LoadAsync(legacy) == new AudioPreferences(), "Read retry after unlocking changed preferences.");
    Task<AudioPreferences> retryRead;
    using (var transientLock = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
    {
        retryRead = invalid.LoadAsync(legacy);
        Check(!retryRead.IsCompleted, "Sharing violation was not given a retry window.");
    }
    Check(await retryRead == new AudioPreferences(), "Transient sharing violation did not recover on the same load.");
    Console.WriteLine("PASS: failed recovery and persistent IO locks preserve the source; later reload succeeds.");

    string settingsPath = Path.Combine(directory, "Settings.json");
    async Task<SaveSettings> LoadGeneral() => await AtomicSettingsFile.LoadAsync(settingsPath,
        SettingsJsonContext.Default.SaveSettings, static () => new(), static () => new());
    await LoadGeneral();
    await AtomicSettingsFile.WriteAsync(settingsPath, JsonSerializer.SerializeToUtf8Bytes(general, SettingsJsonContext.Default.SaveSettings));
    Check(!File.Exists(settingsPath + ".bak"), "General settings still produce backups.");
    await File.WriteAllTextAsync(settingsPath + ".bak", legacyJson);
    await File.WriteAllTextAsync(settingsPath, "{broken");
    Check((await LoadGeneral()).AppTheme == new SaveSettings().AppTheme, "General settings read a stale backup instead of defaults.");
    await AtomicSettingsFile.WriteAsync(settingsPath, JsonSerializer.SerializeToUtf8Bytes(general, SettingsJsonContext.Default.SaveSettings));
    Check((await LoadGeneral()).AppTheme == "Dark", "General settings did not become writable after recovery.");
    Console.WriteLine("PASS: general settings use the same no-backup recovery path and ignore legacy backups.");
}
finally
{
    foreach (string file in Directory.GetFiles(directory)) File.Delete(file);
    Directory.Delete(directory);
}
