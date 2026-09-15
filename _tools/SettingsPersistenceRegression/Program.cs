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
    await Reject<JsonException>(() => invalid.LoadAsync(legacy));
    await Reject<InvalidOperationException>(() => invalid.SaveAsync(legacy));
    Check(await File.ReadAllTextAsync(path) == "{broken", "Corrupt file was overwritten with legacy defaults.");
    await File.WriteAllTextAsync(path, "{\"SchemaVersion\":2,\"Revision\":1,\"Preferences\":{}}");
    await Reject<NotSupportedException>(() => invalid.LoadAsync(legacy));
    await Reject<InvalidOperationException>(() => invalid.SaveAsync(legacy));
    Console.WriteLine("PASS: corrupt and unknown-version files are preserved, with subsequent writes blocked.");
}
finally
{
    foreach (string file in Directory.GetFiles(directory)) File.Delete(file);
    Directory.Delete(directory);
}
