using WinUIMusicPlayer.Utils;
using BassPlayerIpc.Shared;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.ViewModel;
using WinUIMusicPlayer.ViewModel.Controls;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
var store = new CurvePresetService();
var original = new CurvePreset("Reference", CorrectionCurve.Flat);
await store.SaveAsync([original]);
var ipc = new IpcService();
var database = new MusicDatabaseService();
var responseLicense = new LicenseService();
WinUIMusicPlayer.App.Services = new Dictionary<Type, object>
{
    [typeof(IpcService)] = ipc,
    [typeof(LicenseService)] = responseLicense
};
var vm = new ConvolutionCurveViewModel(ipc, store, database, new LicenseService());
var beforeOpen = AppSettings.Dsp;
await vm.OpenAsync();
Check(AppSettings.Dsp == beforeOpen, "Opening must not enable or rewrite convolution.");
vm.SelectedPreset = vm.Presets[0];
Check(!vm.CanUpdatePreset && vm.CanDeletePreset, "Unchanged preset cannot be updated.");
vm.MovePoint(0, 20, 3);
Check(AppSettings.Dsp.CurvePoints == vm.Draft.CurvePoints && AppSettings.Dsp.ConvolutionEnabled,
    "Curve edits must immediately update the shared DSP settings without Apply.");
Check(vm.SelectedPreset == original && vm.CanUpdatePreset, "Editing must retain the overwrite target.");
Check(vm.Draft.CurvePresetName == "", "Modified draft must not claim the saved preset still matches.");
Check(vm.CanApplyPreset, "A modified selected preset must be available for reapplication.");
vm.AutoPreamp = false;
vm.PreampDb = -4;
await vm.ApplyPresetCommand.ExecuteAsync(null);
Check(vm.Draft.CurvePoints == original.Points && AppSettings.Dsp.CurvePoints == original.Points
    && database.SavedDsp!.CurvePoints == original.Points && store.Saved[0] == original,
    "Apply preset must restore and publish stored points without overwriting the preset.");
Check(vm.PreampDb == -4 && !vm.AutoPreamp && !vm.CanApplyPreset && !vm.CanUpdatePreset,
    "Apply preset preserves preamp settings and disables redundant restore/update actions.");
Microsoft.UI.Dispatching.DispatcherQueueTimer.FirePending();
Check(AppSettings.Dsp.CurvePoints == original.Points, "Pending drag must not overwrite the restored preset.");
vm.MovePoint(0, 20, 3);
await vm.UpdatePresetCommand.ExecuteAsync(null);
Check(store.Saved.Count == 1 && store.Saved[0].Name == "Reference"
    && store.Saved[0].Points == vm.Draft.CurvePoints && !vm.CanUpdatePreset, "Update must replace, not append.");
Check(vm.Draft.CurvePresetName == "Reference", "Updated draft should match its preset.");

vm.MovePoint(0, 20, 4);
var stored = store.Saved[0];
store.FailSave = true;
await vm.UpdatePresetCommand.ExecuteAsync(null);
Check(vm.HasError && vm.CanUpdatePreset && vm.Presets[0] == stored, "Failed update must preserve stored state.");
await vm.DeletePresetCommand.ExecuteAsync(null);
Check(vm.HasError && vm.Presets.Count == 1 && vm.SelectedPreset != null, "Failed delete must preserve selection.");
store.FailSave = false;
store.SaveGate = new();
var update = vm.UpdatePresetCommand.ExecuteAsync(null);
Check(vm.IsPresetBusy && !vm.CanSavePreset && !vm.CanDeletePreset, "Serialize preset mutations.");
Check(!vm.CanApplyPreset, "Restore must be unavailable while the preset is being overwritten.");
store.SaveGate.SetResult();
await update;
store.SaveGate = null;
string draft = vm.Draft.CurvePoints;
await vm.DeletePresetCommand.ExecuteAsync(null);
Check(store.Saved.Count == 0 && vm.Presets.Count == 0 && vm.SelectedPreset == null,
    "Delete must persist and remove selection.");
Check(vm.Draft.CurvePoints == draft && vm.Draft.CurvePresetName == "", "Delete must retain the working curve.");
Check(!vm.CanApplyPreset, "A deleted preset cannot be reapplied.");
vm.PresetName = "New";
await vm.SavePresetCommand.ExecuteAsync(null);
Check(vm.Presets.Count == 1 && vm.SelectedPreset?.Name == "New", "Save new preset still works.");
await vm.SavePresetCommand.ExecuteAsync(null);
Check(vm.HasError && vm.Presets.Count == 1, "Duplicate names remain rejected.");
await vm.CloseAsync();
Check(database.SavedDsp == AppSettings.Dsp && ipc.LastPreview == null,
    "Closing must save live settings without an audition or rollback.");
Console.WriteLine("PASS: live edits, preset update/delete/save-as, persistence and error recovery.");

var global = AppSettings.Dsp;
var a = new CurvePreset("Headphones", "20,-6;20000,-6");
var b = new CurvePreset("Speakers", "20,3;20000,3");
await store.SaveAsync([a, b]);
AppSettings.DeviceCorrections = new DeviceCorrections { Enabled = true, Bindings = [
    new DeviceCorrection { DeviceId = "a", Settings = global with { CurvePoints = a.Points, CurvePresetName = a.Name } },
    new DeviceCorrection { DeviceId = "b", Settings = global with { CurvePoints = b.Points, CurvePresetName = b.Name } }
] };
ipc.ChangeOutput("a");
var following = new ConvolutionCurveViewModel(ipc, store, database, new LicenseService());
await following.OpenAsync();
Check(following.Draft.CurvePoints == a.Points, "Open on actual binding.");
DspSettings Effective() => AppSettings.ResolveResponseSettings(ipc.CurrentDspState?.State.OutputDeviceId,
    ipc.CurrentDspState?.State.OutputGeneration ?? 0);
var bindingsVm = new DspSettingsViewModel(ipc, database);
bindingsVm.BeginCorrectionEditing(() => following.Draft, following.LoadCorrectionDraft, following.RefreshOutputCorrection);
following.SelectedPreset = following.Presets.First(p => p.Name == b.Name);
Check(Effective().CurvePoints == b.Points && AppSettings.DeviceCorrections.Find("a")!.Settings.CurvePoints == a.Points,
    "Switching presets must play the new curve without overwriting A's saved binding.");
await following.FlushAsync();
Check(ipc.LastPreview!.CurvePoints == b.Points, "Live correction must reach playback.");
await bindingsVm.LoadCorrectionCommand.ExecuteAsync(null);
Check(Effective().CurvePoints == a.Points && following.SelectedPreset?.Name == a.Name,
    "Load correction must restore the saved A curve after manually selecting B's preset.");
following.MovePoint(0, 20, -4);
var editedA = Effective();
Check(AppSettings.DeviceCorrections.Find("a")!.Settings.CurvePoints == a.Points,
    "Dragging must not overwrite the saved correction.");
await bindingsVm.BindCorrectionCommand.ExecuteAsync(null);
Check(AppSettings.DeviceCorrections.Find("a")!.Settings.CurvePoints == editedA.CurvePoints
    && database.SavedCorrections!.Find("a")!.Settings.CurvePoints == editedA.CurvePoints,
    "Explicit Update binding must persist the live curve.");
following.MovePoint(0, 20, -9);
await bindingsVm.LoadCorrectionCommand.ExecuteAsync(null);
Check(Effective().CurvePoints == editedA.CurvePoints, "Load must restore the explicitly updated binding.");
ipc.ChangeOutput("b");
Check(following.Draft.CurvePoints == b.Points, "Switch to B's saved binding.");
following.MovePoint(0, 20, 5);
await following.ApplyPresetCommand.ExecuteAsync(null);
Check(Effective().CurvePoints == b.Points && AppSettings.DeviceCorrections.Find("a")!.Settings.CurvePoints == editedA.CurvePoints,
    "Apply preset restores only the current curve, without changing another device's binding.");
following.MovePoint(0, 20, 5);
await following.FlushAsync();
Check(await following.CloseAsync() && Effective().CurvePoints == following.Draft.CurvePoints,
    "Closing must keep the live curve active without saving the binding.");
following = new ConvolutionCurveViewModel(ipc, store, database, new LicenseService());
await following.OpenAsync();
Check(following.Draft.CurvePoints == Effective().CurvePoints && following.Draft.CurvePoints != b.Points,
    "Reopening the editor must show the still-active live curve.");
await following.ApplyPresetCommand.ExecuteAsync(null);
ipc.ChangeOutput("a");
Check(following.Draft.CurvePoints == editedA.CurvePoints, "A -> B -> A must restore A's saved binding.");
ipc.ChangeOutput("b");
Check(following.Draft.CurvePoints == b.Points, "Returning to B discards its unbound live changes.");
following.MovePoint(0, 20, 8);
ipc.ChangeOutput("b", 100);
Check(following.Points[0].GainDb == 8 && ipc.LastPreview!.CurvePoints == following.Draft.CurvePoints, "A rebuilt output must receive the custom draft with its new generation.");
await following.ApplyPresetCommand.ExecuteAsync(null);
Check(AppSettings.Dsp.CurvePoints == global.CurvePoints, "Device editing must retain the global curve.");
ipc.ChangeOutput("unbound");
Check(!Effective().ConvolutionEnabled, "Unbound output initially bypasses convolution.");
following.MovePoint(0, 20, -2);
Check(AppSettings.DeviceCorrections.Find("unbound") == null && Effective().CurvePoints == following.Draft.CurvePoints,
    "Unbound-device editing is live only and must not create a saved binding.");
ipc.ChangeOutput("");
Check(following.CanEdit && following.IsOfflineEditing, "No endpoint must allow a live session draft.");
AppSettings.DeviceCorrections = AppSettings.DeviceCorrections with { Enabled = false };
Check(following.Draft.CurvePoints == global.CurvePoints, "Leaving device mode restores global settings.");
following.MovePoint(0, 20, 7);
var globalEdit = AppSettings.Dsp;
ipc.ChangeOutput("b");
Check(following.Draft.CurvePoints == globalEdit.CurvePoints, "Global edits survive output switching.");
await following.CloseAsync();
Check(database.SavedDsp == globalEdit, "Close persists the final global edit.");
Console.WriteLine("PASS: load/update binding commands, live-only device edits, reopen, output generations and global mode.");

// Retry is an error recovery action, not an ordinary Apply button.
bindingsVm.EndCorrectionEditing();
Check(!bindingsVm.ShowRetryCorrection && !bindingsVm.CanRetryCorrection, "Healthy bindings must hide Retry.");
database.FailSave = true;
await bindingsVm.BindCorrectionCommand.ExecuteAsync(null);
Check(bindingsVm.ShowRetryCorrection && bindingsVm.CanRetryCorrection, "A failed binding save must expose Retry.");
database.FailSave = false;
await bindingsVm.RetryCorrectionCommand.ExecuteAsync(null);
Check(!bindingsVm.ShowRetryCorrection && !bindingsVm.CanRetryCorrection, "A successful save retry must hide Retry.");
ipc.SetSyncFailed(true);
Check(bindingsVm.ShowRetryCorrection && bindingsVm.CanRetryCorrection, "An IPC failure must expose Retry.");
await bindingsVm.RetryCorrectionCommand.ExecuteAsync(null);
Check(!bindingsVm.ShowRetryCorrection && !bindingsVm.CanRetryCorrection, "Successful synchronization hides Retry.");
Console.WriteLine("PASS: retry visibility and actual retry commands for save/synchronization failures.");

// Exercise both production response VMs, with real FIR and EQ math.
var eqResponse = new FrequencyResponseViewModel();
var convolutionResponse = new FrequencyResponseViewModel();
eqResponse.Load(); convolutionResponse.Load();
async Task CheckResponses(string reason)
{
    for (int attempt = 0; attempt < 200; attempt++)
    {
        if (eqResponse.Status != "ResponsePreparing" && convolutionResponse.Status != "ResponsePreparing") break;
        await Task.Delay(20);
    }
    Check(eqResponse.Current != null && convolutionResponse.Current != null, reason + ": response generation failed");
    Check(eqResponse.Current!.Combined[0].SequenceEqual(convolutionResponse.Current!.Combined[0]), reason + ": curves differ");
}
await CheckResponses("initial");
var before = eqResponse.Current!.Combined[0].ToArray();
AppSettings.IsEqualizerEnabled = true;
AppSettings.EqualizerBands[0].GainDb = 8;
AppSettings.OnEqUpdated();
await CheckResponses("EQ edit");
Check(!before.SequenceEqual(eqResponse.Current!.Combined[0]), "EQ edit must refresh both curves.");
before = eqResponse.Current!.Combined[0].ToArray();
AppSettings.EqualizerBands[0].Q = 3;
AppSettings.OnEqUpdated();
await CheckResponses("EQ Q edit");
Check(!before.SequenceEqual(eqResponse.Current!.Combined[0]), "Q edit must refresh both curves.");
var live = new ConvolutionCurveViewModel(ipc, store, database, new LicenseService());
await live.OpenAsync();
before = eqResponse.Current!.Combined[0].ToArray();
live.MovePoint(0, 20, -9);
await CheckResponses("convolution edit");
Check(!before.SequenceEqual(eqResponse.Current!.Combined[0]), "Convolution edit must refresh the EQ curve without Apply.");
before = eqResponse.Current!.Combined[0].ToArray();
live.AutoPreamp = false;
live.PreampDb = -11;
await CheckResponses("preamp edit");
Check(!before.SequenceEqual(eqResponse.Current!.Combined[0]), "Preamp changes must refresh both curves.");
AppSettings.DeviceCorrections = AppSettings.DeviceCorrections with { Enabled = true };
ipc.ChangeOutput("a");
await CheckResponses("device A");
Check(AppSettings.ResolveResponseSettings("a").CurvePoints == editedA.CurvePoints, "Response uses A's binding.");
before = eqResponse.Current!.Combined[0].ToArray();
live.MovePoint(0, 20, -10);
await CheckResponses("live device correction");
Check(!before.SequenceEqual(eqResponse.Current!.Combined[0])
    && AppSettings.DeviceCorrections.Find("a")!.Settings.CurvePoints == editedA.CurvePoints,
    "Both response VMs must show live device edits while the saved binding remains intact.");
responseLicense.SetRestricted(LicenseFeature.Convolution);
await CheckResponses("restricted live device correction");
Check(eqResponse.Current!.Fir.SelectMany(x => x).All(x => x == 0), "A live device correction must not bypass the response license gate.");
responseLicense.SetRestricted(LicenseFeature.None);
await CheckResponses("restored live device correction");
await live.CloseAsync();
await CheckResponses("closed live device editor");
Check(Effective().CurvePoints != editedA.CurvePoints, "Closing must retain the audible live curve for EQ.");
live = new ConvolutionCurveViewModel(ipc, store, database, new LicenseService());
await live.OpenAsync();
live.LoadCorrectionDraft(AppSettings.DeviceCorrections.Find("a")!.Settings);
await live.FlushAsync();
await CheckResponses("reload saved device correction");
Check(Effective().CurvePoints == editedA.CurvePoints && ipc.LastPreview!.CurvePoints == editedA.CurvePoints,
    "Loading the saved binding updates both plots and playback.");
ipc.ChangeOutput("missing");
await CheckResponses("unbound bypass");
Check(eqResponse.Current!.Fir.SelectMany(x => x).All(x => x == 0), "Unbound response must have no FIR contribution.");
AppSettings.DeviceCorrections = AppSettings.DeviceCorrections with { Enabled = false };
live.MovePoint(0, 20, 1); live.MovePoint(0, 20, 2); live.MovePoint(0, 20, 3);
await CheckResponses("latest edit wins");
await live.CloseAsync();
await CheckResponses("licensed response before expiration");
var savedResponseSettings = AppSettings.Dsp;
var licensedFir = eqResponse.Current!.Fir[0].ToArray();
Check(licensedFir.Any(x => Math.Abs(x) > 0.01), "Expiration test must start with an audible convolution curve.");
responseLicense.SetRestricted(LicenseFeature.Convolution);
await CheckResponses("trial expired with cached convolution");
Check(eqResponse.Current!.Fir.SelectMany(x => x).All(x => x == 0), "Expired trial must remove cached convolution from both response plots.");
Check(eqResponse.Current.Eq.Any(x => Math.Abs(x) > 0.01)
    && eqResponse.Current.Combined[0].Zip(eqResponse.Current.Eq).All(x => Math.Abs(x.First - x.Second - savedResponseSettings.HeadroomDb) < 1e-9),
    "Expiration must preserve free EQ and manual preamp contributions.");
Check(AppSettings.Dsp == savedResponseSettings, "Response gating must not overwrite saved preferences.");
eqResponse.Unload(); convolutionResponse.Unload();
eqResponse.Load(); convolutionResponse.Load();
await CheckResponses("open response after expiration");
Check(eqResponse.Current!.Fir.SelectMany(x => x).All(x => x == 0), "Opening an expired response must keep convolution bypassed.");
responseLicense.SetRestricted(LicenseFeature.None);
await CheckResponses("license restored");
Check(eqResponse.Current!.Fir[0].SequenceEqual(licensedFir), "Restoring a license must restore the saved convolution response.");
responseLicense.SetRestricted(LicenseFeature.Convolution);
responseLicense.SetRestricted(LicenseFeature.None);
responseLicense.SetRestricted(LicenseFeature.Convolution);
await CheckResponses("rapid license changes");
Check(eqResponse.Current!.Fir.SelectMany(x => x).All(x => x == 0), "A superseded refresh must not restore restricted convolution.");
AppSettings.Dsp = savedResponseSettings with { AutoPreamp = true };
await CheckResponses("restricted automatic preamp");
var eqOnlyPreamp = ResponseMath.AutoPreampDb(AppSettings.EqualizerBands
    .Select(b => PeakCoefficients.Create(b.FrequencyHz, (float)b.GainDb, (float)b.Q, eqResponse.Current!.Rate)).ToArray(),
    null, eqResponse.Current!.Rate);
Check(Math.Abs(eqResponse.Current.AutoGain - eqOnlyPreamp) < 1e-9,
    "Automatic preamp must exclude cached restricted FIR and still compensate EQ.");
AppSettings.Dsp = savedResponseSettings with { AutoPreamp = null, AutoConvolutionHeadroom = true };
await CheckResponses("restricted legacy convolution headroom");
Check(eqResponse.Current!.AutoGain == 0, "Legacy automatic convolution headroom must ignore a restricted cached FIR.");
AppSettings.Dsp = savedResponseSettings;
await CheckResponses("restore saved preferences");
eqResponse.Unload(); convolutionResponse.Unload();
responseLicense.SetRestricted(LicenseFeature.None);
Console.WriteLine("PASS: both real response VMs refresh on EQ, convolution, device binding and rapid edits.");
Console.WriteLine("PASS: response license expiration, cached FIR bypass, free EQ/preamp, reopen and license restoration.");

var saving = new ConvolutionCurveViewModel(ipc, store, database, new LicenseService());
await saving.OpenAsync();
saving.MovePoint(0, 20, 4);
database.SaveGate = new();
var pending = saving.FlushAsync();
saving.MovePoint(0, 20, 6);
database.SaveGate.SetResult();
await pending;
database.SaveGate = null;
Check(database.SavedDsp!.CurvePoints == saving.Draft.CurvePoints, "In-flight save must drain newer edits.");
saving.MovePoint(0, 20, 8);
database.FailSave = true;
Check(!await saving.CloseAsync() && saving.HasError, "Failed save remains visible and retryable.");
database.FailSave = false;
Check(await saving.CloseAsync() && database.SavedDsp!.CurvePoints == saving.Draft.CurvePoints,
    "Retry close persists the latest curve.");
Console.WriteLine("PASS: slow saves preserve newer edits; failed saves can be retried on close.");

var offline = new ConvolutionCurveViewModel(ipc, store, database, new LicenseService());
await offline.OpenAsync();
offline.MovePoint(0, 20, -7);
ipc.FailPublish = true;
Check(await offline.CloseAsync() && database.SavedDsp!.CurvePoints == offline.Draft.CurvePoints,
    "Offline playback must not prevent saving or closing; IPC owns synchronization retries.");
ipc.FailPublish = false;
Console.WriteLine("PASS: offline playback still saves live edits and permits closing.");

// A delayed preset overwrite must not steal selection from the newly active device.
await store.SaveAsync([a, b]);
AppSettings.DeviceCorrections = new DeviceCorrections { Enabled = true, Bindings = [
    new DeviceCorrection { DeviceId = "a", Settings = AppSettings.Dsp with { CurvePoints = a.Points, CurvePresetName = a.Name } },
    new DeviceCorrection { DeviceId = "b", Settings = AppSettings.Dsp with { CurvePoints = b.Points, CurvePresetName = b.Name } }
] };
ipc.ChangeOutput("a");
var switching = new ConvolutionCurveViewModel(ipc, store, database, new LicenseService());
await switching.OpenAsync();
switching.MovePoint(0, 20, -8);
store.SaveGate = new();
var overwrite = switching.UpdatePresetCommand.ExecuteAsync(null);
ipc.ChangeOutput("b");
switching.SelectedPreset = switching.Presets.First(p => p.Name == b.Name);
switching.MovePoint(0, 20, 9);
store.SaveGate.SetResult();
await overwrite;
store.SaveGate = null;
Check(switching.SelectedPreset?.Name == b.Name && switching.CanApplyPreset,
    "A delayed overwrite on A must preserve B's preset selection and restore action.");
var preservedA = AppSettings.DeviceCorrections.Find("a")!.Settings;
await switching.ApplyPresetCommand.ExecuteAsync(null);
Check(Effective().CurvePoints == b.Points
    && AppSettings.DeviceCorrections.Find("a")!.Settings == preservedA,
    "After the delayed save, Apply must restore B, never A's preset.");
await switching.CloseAsync();
Console.WriteLine("PASS: delayed preset saves cannot change the active device's restore target.");

// An already open editor must follow license changes, not just the entry button.
var license = new LicenseService();
var licensedEditor = new ConvolutionCurveViewModel(ipc, store, database, license);
await licensedEditor.OpenAsync();
var savedBeforeRestriction = AppSettings.Dsp;
license.SetRestricted(LicenseFeature.Convolution);
Check(!licensedEditor.CanEdit && !licensedEditor.CanSavePreset && !licensedEditor.CanDeletePreset
    && !licensedEditor.AddPointCommand.CanExecute(null), "Open editor did not lock after license change.");
licensedEditor.MovePoint(0, 20, 11);
Check(AppSettings.Dsp == savedBeforeRestriction, "Locked editor changed saved preferences.");
license.SetRestricted(LicenseFeature.None);
Check(licensedEditor.CanEdit && licensedEditor.AddPointCommand.CanExecute(null), "Editor did not unlock after license recovery.");
license.SetRestricted(LicenseFeature.Preamp);
Check(licensedEditor.CanEdit && !licensedEditor.PreampEditable && !licensedEditor.ManualGainEnabled,
    "Adding only preamp to the policy must leave curve editing available.");
double preservedGain = AppSettings.Dsp.HeadroomDb;
licensedEditor.PreampDb = preservedGain + 1;
Check(AppSettings.Dsp.HeadroomDb == preservedGain, "Curve editor bypassed the configurable preamp gate.");
await licensedEditor.CloseAsync();
bindingsVm.ConvolutionRestricted = true;
bool modeBeforeRestriction = bindingsVm.DeviceCorrectionEnabled;
bindingsVm.DeviceCorrectionEnabled = !modeBeforeRestriction;
Check(bindingsVm.DeviceCorrectionEnabled == modeBeforeRestriction && !bindingsVm.CanChangeCorrectionMode
    && !bindingsVm.BindCorrectionCommand.CanExecute(null), "Device binding bypassed the convolution gate.");
Console.WriteLine("PASS: live convolution editor and device bindings follow the feature gate without changing saved settings.");

// Use the production correction output publisher even while no dialog is open.
AppSettings.DeviceCorrections = AppSettings.DeviceCorrections with { Enabled = false };
AppSettings.DeviceCorrections = new DeviceCorrections { Enabled = true, Bindings = [
    new DeviceCorrection { DeviceId = "cold-a", Settings = AppSettings.Dsp with { CurvePoints = a.Points, CurvePresetName = a.Name } },
    new DeviceCorrection { DeviceId = "cold-b", Settings = AppSettings.Dsp with { CurvePoints = b.Points, CurvePresetName = b.Name } }
] };
var coldStore = new CurvePresetService();
await coldStore.SaveAsync([a, b]);
var coldIpc = new IpcService();
var coldDatabase = new MusicDatabaseService();
var coldLicense = new LicenseService();
var cold = new ConvolutionCurveViewModel(coldIpc, coldStore, coldDatabase, coldLicense);
await cold.OpenAsync();
cold.SelectedPreset = cold.Presets.First(p => p.Name == a.Name);
cold.MovePoint(0, 20, -11);
var customCurve = cold.Draft.CurvePoints;
var globalCurve = AppSettings.Dsp.CurvePoints;
Check(cold.CanEdit && cold.IsOfflineEditing && AppSettings.IsCustomCorrectionDraft, "Cold start must create a live session draft.");
await cold.CloseAsync();
Check((coldDatabase.SavedDsp == null || coldDatabase.SavedDsp.CurvePoints == globalCurve) && coldDatabase.SavedCorrections == null,
    "Closing must not persist the custom curve as global settings or device bindings.");
coldIpc.ChangeOutput("cold-a");
Check(coldIpc.LastPreview?.CurvePoints == customCurve && coldIpc.PreviewDeviceId == "cold-a",
    "First playback with the dialog closed must publish the unsaved draft.");
cold = new ConvolutionCurveViewModel(coldIpc, coldStore, coldDatabase, coldLicense);
await cold.OpenAsync();
Check(cold.Draft.CurvePoints == customCurve && cold.SelectedPreset?.Name == a.Name && cold.CanApplyPreset,
    "Reopening after first playback must preserve both draft and its reapply target.");
Microsoft.UI.Dispatching.DispatcherQueue.DeferCallbacks = true;
coldIpc.ChangeOutput("cold-b");
coldIpc.ChangeOutput("cold-a", 500);
Microsoft.UI.Dispatching.DispatcherQueue.Drain();
Microsoft.UI.Dispatching.DispatcherQueue.DeferCallbacks = false;
Check(cold.Draft.CurvePoints == customCurve && coldIpc.LastPreview?.CurvePoints == customCurve
    && coldIpc.PreviewGeneration == 500 && !cold.IsOfflineEditing,
    "Custom draft must audition across switches and output rebuilds despite delayed UI notifications.");
cold.MovePoint(0, 20, -9);
await cold.FlushAsync();
Check(coldIpc.LastPreview?.CurvePoints == cold.Draft.CurvePoints && coldIpc.PreviewDeviceId == "cold-a",
    "Editing after a switch must affect the current audio output.");
await cold.ApplyPresetCommand.ExecuteAsync(null);
Check(!AppSettings.IsCustomCorrectionDraft && coldIpc.LastPreview?.CurvePoints == a.Points,
    "Reapplying the preset must immediately audition it and exit custom mode.");
coldIpc.ChangeOutput("cold-b");
Check(cold.Draft.CurvePoints == b.Points && coldIpc.LastPreview == null,
    "After reapply, switching devices must restore saved bindings without restarting or toggling.");
cold.MovePoint(0, 20, 7);
cold.MovePoint(0, 20, 3);
Check(!cold.CanUpdatePreset && cold.CanApplyPreset, "A draft identical to its preset must still allow exiting custom mode.");
await cold.ApplyPresetCommand.ExecuteAsync(null);
coldIpc.ChangeOutput("cold-a");
Check(cold.Draft.CurvePoints == a.Points, "Reapplying an identical preset must restore automatic binding.");
cold.MovePoint(0, 20, 9);
cold.SelectedPreset = cold.Presets.First(p => p.Name == b.Name);
await cold.FlushAsync();
Check(!AppSettings.IsCustomCorrectionDraft && coldIpc.LastPreview?.CurvePoints == b.Points,
    "Selecting another preset also exits custom mode and auditions it immediately.");
coldIpc.ChangeOutput("cold-b");
coldIpc.ChangeOutput("cold-a");
Check(cold.Draft.CurvePoints == a.Points, "Preset selection must leave subsequent automatic binding intact.");
var coldBindings = new DspSettingsViewModel(coldIpc, coldDatabase);
coldBindings.BeginCorrectionEditing(() => cold.Draft, cold.LoadCorrectionDraft, cold.RefreshOutputCorrection);
cold.MovePoint(0, 20, 4);
coldBindings.SelectedCorrectionDevice = new BassOutputDevice { EndpointId = "cold-b", Name = "Speakers" };
await coldBindings.BindCorrectionCommand.ExecuteAsync(null);
Check(AppSettings.IsCustomCorrectionDraft && coldDatabase.SavedCorrections!.Find("cold-b")!.Settings.CurvePoints == cold.Draft.CurvePoints,
    "Saving a binding stores a snapshot but does not implicitly apply a preset.");
await coldBindings.LoadCorrectionCommand.ExecuteAsync(null);
await cold.FlushAsync();
Check(!AppSettings.IsCustomCorrectionDraft && coldIpc.LastPreview?.CurvePoints == cold.Draft.CurvePoints,
    "Explicitly loading a saved binding must exit custom mode and audition on the current output.");
cold.MovePoint(0, 20, 10);
AppSettings.DeviceCorrections = AppSettings.DeviceCorrections with { Enabled = false };
AppSettings.DeviceCorrections = AppSettings.DeviceCorrections with { Enabled = true };
Check(!AppSettings.IsCustomCorrectionDraft && cold.Draft.CurvePoints == a.Points,
    "Toggling binding mode clears the session draft and restores the actual binding.");
coldBindings.EndCorrectionEditing();
await cold.CloseAsync();

// An offline preset applies once to the first output, then follows normal device bindings.
AppSettings.DeviceCorrections = AppSettings.DeviceCorrections with { Enabled = false };
AppSettings.DeviceCorrections = AppSettings.DeviceCorrections with { Enabled = true };
coldIpc.ChangeOutput("");
cold = new ConvolutionCurveViewModel(coldIpc, coldStore, coldDatabase, coldLicense);
await cold.OpenAsync();
cold.SelectedPreset = cold.Presets.First(p => p.Name == b.Name);
await cold.CloseAsync();
coldIpc.ChangeOutput("cold-a");
Check(coldIpc.LastPreview?.CurvePoints == b.Points && !AppSettings.IsCustomCorrectionDraft,
    "Offline preset must be applied to the first real output without requiring an open editor.");
coldIpc.ChangeOutput("cold-b");
coldIpc.ChangeOutput("cold-a");
Check(AppSettings.ResolveResponseSettings("cold-a", coldIpc.CurrentDspState!.State.OutputGeneration).CurvePoints == a.Points,
    "Pending offline preset must not become a permanent cross-device override.");

// Every response graph resolves the same audible session draft.
WinUIMusicPlayer.App.Services = new Dictionary<Type, object>
{
    [typeof(IpcService)] = coldIpc,
    [typeof(LicenseService)] = coldLicense
};
var responseA = new FrequencyResponseViewModel();
var responseB = new FrequencyResponseViewModel();
AppSettings.SetCustomCorrectionDraft(AppSettings.Dsp with { CurvePoints = "20,8;20000,8", ConvolutionSource = ConvolutionSource.Curve });
responseA.Load(); responseB.Load();
async Task WaitForSessionResponses()
{
    for (int i = 0; i < 200 && (responseA.Status == "ResponsePreparing" || responseB.Status == "ResponsePreparing"); i++)
        await Task.Delay(20);
    Check(responseA.Current != null && responseB.Current != null, "Session response calculation failed.");
}
await WaitForSessionResponses();
Check(responseA.Current!.Fir[0].SequenceEqual(responseB.Current!.Fir[0]) && responseA.Current.Fir[0].Any(v => Math.Abs(v) > 1),
    "All plots must show the audible custom curve, not a separate detached preview.");
coldLicense.SetRestricted(LicenseFeature.Convolution);
await WaitForSessionResponses();
Check(responseA.Current!.Fir.SelectMany(c => c).All(v => v == 0), "Session drafts must respect license restrictions.");
responseA.Unload(); responseB.Unload();
Console.WriteLine("PASS: first playback with closed dialog, session draft live output following, reapply/selection recovery, binding reset and shared response.");

coldLicense.SetRestricted(LicenseFeature.None);
AppSettings.DeviceCorrections = AppSettings.DeviceCorrections with { Enabled = false };
AppSettings.DeviceCorrections = AppSettings.DeviceCorrections with { Enabled = true };
coldIpc.ChangeOutput("");
var delayedUi = new ConvolutionCurveViewModel(coldIpc, coldStore, coldDatabase, coldLicense);
await delayedUi.OpenAsync();
Microsoft.UI.Dispatching.DispatcherQueue.DeferCallbacks = true;
coldIpc.ChangeOutput("cold-a");
delayedUi.MovePoint(0, 20, -4);
await delayedUi.FlushAsync();
Check(coldIpc.LastPreview?.CurvePoints == delayedUi.Draft.CurvePoints,
    "A stale no-output UI flag must not suppress audition after the actual output has appeared.");
Microsoft.UI.Dispatching.DispatcherQueue.Drain();
Microsoft.UI.Dispatching.DispatcherQueue.DeferCallbacks = false;
int previewsBeforeFeedback = coldIpc.PreviewCount;
coldIpc.ChangeOutput("cold-a", coldIpc.CurrentDspState!.State.OutputGeneration);
Check(coldIpc.PreviewCount == previewsBeforeFeedback, "Ordinary DSP feedback must not create a preview notification loop.");
await delayedUi.CloseAsync();
Console.WriteLine("PASS: first-output/UI interleaving and no repeated preview on ordinary DSP notifications.");

// Closing an editor is a real barrier, including a commit that started before shutdown.
var shutdownDatabase = new MusicDatabaseService();
var shutdownEditor = new ConvolutionCurveViewModel(ipc, store, shutdownDatabase, new LicenseService());
await shutdownEditor.OpenAsync();
shutdownEditor.MovePoint(0, 20, 3);
shutdownDatabase.SaveGate = new();
var shutdownWrite = shutdownEditor.FlushAsync();
var shutdownBarrier = shutdownEditor.StopAsync();
Check(!shutdownBarrier.IsCompleted, "Editor stop must wait for the actual settings write.");
shutdownDatabase.SaveGate.SetResult();
await shutdownWrite;
await shutdownBarrier;
Check(!shutdownEditor.CanEdit, "Stopped editor must reject new edits.");
var stoppingResponse = new FrequencyResponseViewModel();
stoppingResponse.Load();
await stoppingResponse.StopAsync();
Check(stoppingResponse.Current is null, "Frequency response stop must drain work and release its preview.");
Console.WriteLine("PASS: real curve shutdown waits for slow save; stopped editing rejected; frequency task and cache drained.");
