using AnimatedWin2dControls.Enums;
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
    Check(!defaults.PlaybackHdrEnabled && defaults.PlaybackHdrReferenceWhiteNits == 300 && defaults.PlaybackHdrPeakNits == 600,
        "Missing HDR keys must preserve SDR and the default brightness values.");
    var legacyHdr = JsonSerializer.Deserialize("""{"PlaybackHdrEnabled":true,"PlaybackHdrPeakNits":420}""", SettingsJsonContext.Default.SaveSettings)!;
    Check(legacyHdr.PlaybackHdrEnabled && legacyHdr.PlaybackHdrPeakNits == 420 && legacyHdr.PlaybackHdrReferenceWhiteNits == 300, "Legacy peak was lost.");
    var previousPlaybackSettings = JsonSerializer.Deserialize(
        """{"IsWin2dAnimatedText":false,"IsHoverScrollEnabled":false,"EnableAdvancedLyricsEffect":false,"PlaybackHdrPeakNits":530,"GlowAmount":7.5,"PlaybackHdrGlowBoostPercent":75,"PlaybackHdrHaloStrengthPercent":225}""",
        SettingsJsonContext.Default.SaveSettings)!;
    using var migratedPlaybackSettings = JsonDocument.Parse(JsonSerializer.Serialize(previousPlaybackSettings, SettingsJsonContext.Default.SaveSettings));
    Check(previousPlaybackSettings.PlaybackHdrPeakNits == 530 && previousPlaybackSettings.GlowAmount == 7.5
        && !migratedPlaybackSettings.RootElement.TryGetProperty("IsWin2dAnimatedText", out _)
        && !migratedPlaybackSettings.RootElement.TryGetProperty("IsHoverScrollEnabled", out _)
        && !migratedPlaybackSettings.RootElement.TryGetProperty("EnableAdvancedLyricsEffect", out _)
        && !migratedPlaybackSettings.RootElement.TryGetProperty("PlaybackHdrGlowBoostPercent", out _)
        && !migratedPlaybackSettings.RootElement.TryGetProperty("PlaybackHdrHaloStrengthPercent", out _),
        "Old playback settings must preserve the common glow control and omit retired keys on save.");
    var hdrSettings = new SaveSettings { PlaybackHdrEnabled = true, PlaybackHdrReferenceWhiteNits = 350, PlaybackHdrPeakNits = 950, GlowAmount = 6.4 };
    var hdrCopy = JsonSerializer.Deserialize(JsonSerializer.Serialize(hdrSettings, SettingsJsonContext.Default.SaveSettings),
        SettingsJsonContext.Default.SaveSettings)!;
    Check(hdrCopy.PlaybackHdrEnabled && hdrCopy.PlaybackHdrReferenceWhiteNits == 350
        && hdrCopy.PlaybackHdrPeakNits == 950 && hdrCopy.GlowAmount == 6.4, "HDR brightness and the shared glow setting did not survive restart.");
    Console.WriteLine("PASS: retired HDR glow keys are ignored and the shared glow setting survives restart.");
    var firstStartupPath = Path.Combine(directory, "FirstStartup.json");
    var firstStartup = await AtomicSettingsFile.LoadAsync(firstStartupPath, SettingsJsonContext.Default.SaveSettings,
        SaveSettings.CreateForNewUser, SaveSettings.CreateForNewUser);
    Check(firstStartup.DefaultEntry == "Home" && File.Exists(firstStartupPath), "First-run factory persists the Home startup entry.");
    var oldStartupPath = Path.Combine(directory, "ExistingStartup.json");
    await File.WriteAllTextAsync(oldStartupPath, "{}");
    var oldStartup = await AtomicSettingsFile.LoadAsync(oldStartupPath, SettingsJsonContext.Default.SaveSettings,
        SaveSettings.CreateForNewUser, SaveSettings.CreateForNewUser);
    Check(oldStartup.DefaultEntry == "AddFolder", "Existing files retain their old default even when the missing-file factory selects Home.");
    Check(SaveSettings.CreateForNewUser().DefaultEntry == "Home", "First startup must use Home.");
    Check(defaults.DefaultEntry == "AddFolder", "Existing settings without the old entry key retain their previous default.");
    foreach (string entry in new[] { "AddFolder", "MusicBrowse", "PlayLists", "Stats", "Home" })
    {
        var savedEntry = JsonSerializer.Deserialize("{\"DefualtEntry\":\"" + entry + "\"}", SettingsJsonContext.Default.SaveSettings)!;
        var persistedEntry = JsonSerializer.Deserialize(JsonSerializer.Serialize(savedEntry, SettingsJsonContext.Default.SaveSettings), SettingsJsonContext.Default.SaveSettings)!;
        Check(persistedEntry.DefaultEntry == entry, "Existing startup choice survives restart: " + entry);
    }
    Check(!defaults.PreferDatabaseLyrics && defaults.LocalLyricsFormatOrder == "krc,qrc,lrc,ttml",
        "Old settings must retain files-first lyrics and the previous format order.");
    var lyricsSettings = new SaveSettings { PreferDatabaseLyrics = true, LocalLyricsFormatOrder = "ttml,lrc,qrc,krc", DesktopLyricsTaskbarLyricsWidth = 1024, IsDesktopLyricsPronunciationEnabled = false };
    var lyricsRoundTrip = JsonSerializer.Deserialize(JsonSerializer.Serialize(lyricsSettings, SettingsJsonContext.Default.SaveSettings), SettingsJsonContext.Default.SaveSettings)!;
    Check(lyricsRoundTrip.PreferDatabaseLyrics && lyricsRoundTrip.LocalLyricsFormatOrder == lyricsSettings.LocalLyricsFormatOrder
        && lyricsRoundTrip.DesktopLyricsTaskbarLyricsWidth == 1024 && !lyricsRoundTrip.IsDesktopLyricsPronunciationEnabled,
        "Lyrics source, format priorities, taskbar lyrics width, and desktop pronunciation must survive restart.");
    Console.WriteLine("PASS: lyrics priority compatibility defaults and JSON persistence.");
    Check(!defaults.IsFanLyricsEnabled && defaults.FanLyricsAngle == 30
        && !defaults.Is3DLyricsEnabled && !defaults.IsAuto3DLyricsEnabled && defaults.Lyrics3DXAngle == 0
        && defaults.Lyrics3DYAngle == 0 && defaults.Lyrics3DZAngle == 0 && defaults.Lyrics3DDepth == 800
        && defaults.IsLyricsEdgeFeatheringEffectEnabled && defaults.WordByWordEffectMode == WordByWordEffectMode.Auto,
        "Existing profiles must get the default lyrics effects.");
    lyricsSettings.IsFanLyricsEnabled = true;
    lyricsSettings.FanLyricsAngle = -40;
    lyricsSettings.Is3DLyricsEnabled = true;
    lyricsSettings.IsAuto3DLyricsEnabled = true;
    lyricsSettings.Lyrics3DXAngle = 10;
    lyricsSettings.Lyrics3DYAngle = -20;
    lyricsSettings.Lyrics3DZAngle = 30;
    lyricsSettings.Lyrics3DDepth = 400;
    lyricsSettings.IsLyricsEdgeFeatheringEffectEnabled = false;
    lyricsSettings.WordByWordEffectMode = WordByWordEffectMode.Never;
    lyricsRoundTrip = JsonSerializer.Deserialize(JsonSerializer.Serialize(lyricsSettings, SettingsJsonContext.Default.SaveSettings), SettingsJsonContext.Default.SaveSettings)!;
    Check(lyricsRoundTrip.IsFanLyricsEnabled && lyricsRoundTrip.FanLyricsAngle == -40
        && lyricsRoundTrip.Is3DLyricsEnabled && lyricsRoundTrip.IsAuto3DLyricsEnabled && lyricsRoundTrip.Lyrics3DXAngle == 10
        && lyricsRoundTrip.Lyrics3DYAngle == -20 && lyricsRoundTrip.Lyrics3DZAngle == 30 && lyricsRoundTrip.Lyrics3DDepth == 400
        && !lyricsRoundTrip.IsLyricsEdgeFeatheringEffectEnabled && lyricsRoundTrip.WordByWordEffectMode == WordByWordEffectMode.Never,
        "Lyrics effects must survive restart.");

    Check(defaults.WindowBackgroundImagePath == string.Empty && defaults.WindowBackgroundBlurAmount == 20,
        "Old settings must keep the original backdrop without an image.");
    var imageSettings = new SaveSettings { WindowBackgroundImagePath = @"C:\Pictures\背景.png", WindowBackgroundBlurAmount = 37 };
    var imageRoundTrip = JsonSerializer.Deserialize(JsonSerializer.Serialize(imageSettings, SettingsJsonContext.Default.SaveSettings), SettingsJsonContext.Default.SaveSettings)!;
    Check(imageRoundTrip.WindowBackgroundImagePath == imageSettings.WindowBackgroundImagePath && imageRoundTrip.WindowBackgroundBlurAmount == 37,
        "Custom background path and blur must survive restart.");
    Check(!defaults.PlaybackSpectrum.Enabled && defaults.PlaybackSpectrum.Count == 32
        && defaults.PlaybackSpectrum.Placement == AnimatedWin2dControls.Controls.SpectrumPlacement.Bottom
        && defaults.PlaybackSpectrum.Sensitivity == 100 && defaults.PlaybackSpectrum.Opacity == 50
        && defaults.PlaybackSpectrum.Glow, "Older settings must use the spectrum defaults.");
    var partialSpectrum = JsonSerializer.Deserialize("""{"PlaybackSpectrum":{"Enabled":true,"Parallax":true}}""",
        SettingsJsonContext.Default.SaveSettings)!;
    Check(partialSpectrum.PlaybackSpectrum.Enabled && partialSpectrum.PlaybackSpectrum.Opacity == 50
        && partialSpectrum.PlaybackSpectrum.Glow,
        "Settings with the retired parallax option must preserve the toggle and load current defaults.");
    using var migratedSpectrum = JsonDocument.Parse(JsonSerializer.Serialize(partialSpectrum, SettingsJsonContext.Default.SaveSettings));
    Check(!migratedSpectrum.RootElement.GetProperty("PlaybackSpectrum").TryGetProperty("Parallax", out _),
        "The retired parallax field must be omitted after saving.");
    var spectrum = new SaveSettings
    {
        PlaybackSpectrum = new()
        {
            Enabled = true, Count = 128, DelayMs = 220, CustomArgb = 0xff287654,
            Opacity = 100, Glow = false
        }
    };
    var restoredSpectrum = JsonSerializer.Deserialize(JsonSerializer.Serialize(spectrum, SettingsJsonContext.Default.SaveSettings),
        SettingsJsonContext.Default.SaveSettings)!;
    Check(restoredSpectrum.PlaybackSpectrum == spectrum.PlaybackSpectrum, "Spectrum settings did not survive restart.");
    Console.WriteLine("PASS: background compatibility defaults and image/blur/spectrum persistence.");
    var general = JsonSerializer.Deserialize(legacyJson, SettingsJsonContext.Default.SaveSettings)!;
    var legacy = general.ReadLegacyAudioPreferences();
    Check(legacy.OutputMode == "WasapiExclusiveEvent" && legacy.Latency == 137 && legacy.BassOutputDeviceId == 9
        && legacy.WasapiEndpointId == "endpoint-a" && legacy.BassASIODeviceId == 3 && legacy.DeviceFriendlyName == "USB DAC"
        && legacy.IsFadeEnabled && legacy.IsDopEnabled && legacy.ExperimentalSurround51 && legacy.ExperimentalAtmosPassthrough
        && legacy.DsdGain == 12 && legacy.DsdPcmFreq == 176400 && legacy.Dsp.HeadroomDb == -7
        && legacy.Dsp.ConvolutionEnabled && legacy.Dsp.CurvePoints == "20,-4;20000,-4", "Audio migration lost a field.");
    Check(new SaveSettings().ReadLegacyAudioPreferences() == new AudioPreferences(), "Missing legacy keys changed defaults.");

    string playbackPath = Path.Combine(directory, "PlaybackEffects.json");
    await File.WriteAllTextAsync(playbackPath, """{"SchemaVersion":1,"Revision":1,"Preferences":{"Dsp":{"GaplessPlayback":false},"IsFadeEnabled":true}}""");
    var playbackStore = new AudioSettingsStore(playbackPath);
    var oldTransitions = await playbackStore.LoadAsync(new());
    Check(!oldTransitions.Dsp.GaplessPlayback && oldTransitions.Dsp.TransitionStyle == TrackTransitionStyle.Gapless
        && oldTransitions.Dsp.CrossfadeMilliseconds == 1000 && oldTransitions.IsFadeEnabled,
        "Old settings did not retain Normal and manual fades with an independent default duration.");

    var playbackPreferences = new AudioPreferences
    {
        SpectralEnhancement = SpectralEnhancementMode.LossyAndLowRateLossless,
        Dsp = new()
        {
            GaplessPlayback = false, TransitionStyle = TrackTransitionStyle.Smart, CrossfadeMilliseconds = 1300,
            PlaybackRate = 1.25, CompressorEnabled = true, CompressorThresholdDb = -22,
            CompressorRatio = 3.5, CompressorMakeupDb = 8
        }
    };
    await playbackStore.SaveAsync(playbackPreferences);
    Check(await new AudioSettingsStore(playbackPath).LoadAsync(new()) == playbackPreferences,
        "Playback effects and retained transition duration did not survive restarting the store.");
    using (var saved = JsonDocument.Parse(await File.ReadAllTextAsync(playbackPath)))
    {
        var dsp = saved.RootElement.GetProperty("Preferences").GetProperty("Dsp");
        Check(dsp.GetProperty("CrossfadeMilliseconds").GetInt32() == 1300 && !dsp.TryGetProperty("CrossfadeSeconds", out _),
            "New settings must write the millisecond duration once.");
    }
    Check(legacy.Dsp is { GaplessPlayback: true, TransitionStyle: TrackTransitionStyle.Gapless, CrossfadeMilliseconds: 1000,
        PlaybackRate: 1, CompressorEnabled: false }, "Old settings changed the automatic transition or playback effects.");
    Check(legacy.SpectralEnhancement == SpectralEnhancementMode.Off, "Legacy audio enabled enhancement.");
    foreach (int savedDuration in new[] { 1, 2, 3, 4, 5, 12 })
    {
        await File.WriteAllTextAsync(playbackPath,
            $$$"""{"SchemaVersion":1,"Revision":2,"Preferences":{"Dsp":{"TransitionStyle":2,"CrossfadeSeconds":{{{savedDuration}}} },"IsFadeEnabled":true}}""");
        var clamped = await new AudioSettingsStore(playbackPath).LoadAsync(new());
        Check(clamped.Dsp.TransitionStyle == TrackTransitionStyle.Smart && clamped.IsFadeEnabled
            && clamped.Dsp.CrossfadeMilliseconds == Math.Min(savedDuration * 1000, 3000),
            $"Old duration {savedDuration} did not convert to milliseconds with mode and manual fade intact.");
    }
    await File.WriteAllTextAsync(playbackPath, """{"SchemaVersion":1,"Revision":2,"Preferences":{"Dsp":{"TransitionStyle":999,"CrossfadeSeconds":999},"IsFadeEnabled":true}}""");
    var malformedTransition = await new AudioSettingsStore(playbackPath).LoadAsync(new());
    Check(malformedTransition.Dsp is { GaplessPlayback: true, TransitionStyle: TrackTransitionStyle.Gapless, CrossfadeMilliseconds: 3000 }
        && malformedTransition.IsFadeEnabled, "Out-of-range transitions reset unrelated audio preferences.");
    TestTransitionProtocol(playbackPreferences.Dsp);
    Console.WriteLine("PASS: independent manual fades, automatic mode and duration survive restart; older durations clamp and legacy DSP and preview packets decode.");

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

static void TestTransitionProtocol(DspSettings settings)
{
    const int legacyDspSize = 1654;
    byte[] settingsPayload = new byte[DspProtocol.SettingsSize];
    foreach (var style in Enum.GetValues<TrackTransitionStyle>())
    {
        var current = settings with { TransitionStyle = style };
        DspProtocol.WriteSettings(settingsPayload, current);
        Check(DspProtocol.ReadSettings(settingsPayload) == current, "Transition preference changed crossing IPC.");
    }
    settingsPayload[0] = 7;
    var legacySettings = settings with { TransitionStyle = TrackTransitionStyle.Gapless, CrossfadeMilliseconds = 1000 };
    Check(DspProtocol.ReadSettings(settingsPayload.AsSpan(0, legacyDspSize)) == legacySettings,
        "v7 must preserve the old toggle and DSP settings with legacy transition defaults.");
    settingsPayload[0] = 6;
    Check(DspProtocol.ReadSettings(settingsPayload.AsSpan(0, 1630)) == legacySettings,
        "v6 must preserve the old toggle and DSP settings with legacy transition defaults.");
    settingsPayload[0] = 5;
    Check(DspProtocol.ReadSettings(settingsPayload.AsSpan(0, 1596)) is
        { GaplessPlayback: true, TransitionStyle: TrackTransitionStyle.Gapless, CrossfadeMilliseconds: 1000 },
        "Settings predating the gapless toggle must use the default transition.");

    const int v8DspSize = legacyDspSize + 2;
    DspProtocol.WriteSettings(settingsPayload, settings);
    var v8 = settingsPayload.AsSpan(0, v8DspSize);
    v8[0] = 8;
    v8[^1] = 2;
    Check(DspProtocol.ReadSettings(v8) == (settings with { CrossfadeMilliseconds = 2000 }),
        "v8 duration did not convert from seconds.");
    v8[^1] = 12;
    Check(DspProtocol.ReadSettings(v8) == (settings with { CrossfadeMilliseconds = 3000 }),
        "Older v8 packets must clamp the duration while preserving other settings.");

    DspProtocol.WriteSettings(settingsPayload, settings with { TransitionStyle = (TrackTransitionStyle)258, CrossfadeMilliseconds = -1 });
    Check(DspProtocol.ReadSettings(settingsPayload) is { TransitionStyle: TrackTransitionStyle.Gapless, CrossfadeMilliseconds: 300 },
        "Invalid sender settings must normalize before conversion to bytes.");
    settingsPayload[legacyDspSize] = byte.MaxValue;
    settingsPayload[^2] = byte.MaxValue;
    settingsPayload[^1] = byte.MaxValue;
    Check(DspProtocol.ReadSettings(settingsPayload) is { TransitionStyle: TrackTransitionStyle.Gapless, CrossfadeMilliseconds: 3000 },
        "Invalid received settings must use a supported style and duration.");

    var preview = new DspPreview(7, 123, "output-a", false, settings);
    byte[] previewPayload = new byte[DspPreview.Size];
    preview.Write(previewPayload);
    Check(DspPreview.Read(previewPayload) == preview, "The v9 preview misplaced its output identity or settings.");

    byte[] v8Preview = new byte[DspPreview.Size - 1];
    previewPayload.AsSpan(0, v8DspSize).CopyTo(v8Preview);
    previewPayload.AsSpan(DspProtocol.SettingsSize).CopyTo(v8Preview.AsSpan(v8DspSize));
    v8Preview[0] = 8;
    v8Preview[v8DspSize - 1] = 2;
    Check(DspPreview.Read(v8Preview) == (preview with { Settings = settings with { CrossfadeMilliseconds = 2000 } }),
        "The v8 preview no longer decodes a seconds duration and its device identity.");

    byte[] legacyPreview = new byte[DspPreview.Size - 3];
    previewPayload.AsSpan(0, legacyDspSize).CopyTo(legacyPreview);
    previewPayload.AsSpan(DspProtocol.SettingsSize).CopyTo(legacyPreview.AsSpan(legacyDspSize));
    legacyPreview[0] = 7;
    Check(DspPreview.Read(legacyPreview) == (preview with { Settings = legacySettings }),
        "The v7 preview no longer decodes its full device identity and settings.");
}
