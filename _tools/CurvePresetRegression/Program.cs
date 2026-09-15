using WinUIMusicPlayer.Utils;
using BassPlayerIpc.Shared;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.ViewModel;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
var store = new CurvePresetService();
var original = new CurvePreset("Reference", CorrectionCurve.Flat);
await store.SaveAsync([original]);
var ipc = new IpcService();
var database = new MusicDatabaseService();
WinUIMusicPlayer.App.Services = ipc;
var vm = new ConvolutionCurveViewModel(ipc, store, database);
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
store.SaveGate.SetResult();
await update;
store.SaveGate = null;
string draft = vm.Draft.CurvePoints;
await vm.DeletePresetCommand.ExecuteAsync(null);
Check(store.Saved.Count == 0 && vm.Presets.Count == 0 && vm.SelectedPreset == null,
    "Delete must persist and remove selection.");
Check(vm.Draft.CurvePoints == draft && vm.Draft.CurvePresetName == "", "Delete must retain the working curve.");
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
var following = new ConvolutionCurveViewModel(ipc, store, database);
await following.OpenAsync();
Check(following.Draft.CurvePoints == a.Points, "Open on actual binding.");
following.MovePoint(0, 20, -4);
var editedA = AppSettings.DeviceCorrections.Find("a")!.Settings;
ipc.ChangeOutput("b");
Check(following.Draft.CurvePoints == b.Points, "Switch to B's binding.");
following.MovePoint(0, 20, 5);
var editedB = AppSettings.DeviceCorrections.Find("b")!.Settings;
await following.FlushAsync();
Check(database.SavedCorrections!.Find("a")!.Settings == editedA
    && database.SavedCorrections.Find("b")!.Settings == editedB, "Rapid switching must save both endpoints.");
ipc.ChangeOutput("a");
Check(following.Draft.CurvePoints == editedA.CurvePoints, "A -> B -> A must retain A edits without Apply.");
Check(AppSettings.Dsp.CurvePoints == global.CurvePoints, "Device editing must retain the global curve.");
ipc.ChangeOutput("unbound");
Check(!AppSettings.ResolveResponseSettings("unbound").ConvolutionEnabled,
    "Unbound output bypasses convolution until the user edits it.");
following.MovePoint(0, 20, -2);
Check(AppSettings.DeviceCorrections.Find("unbound")!.Settings.CurvePoints == following.Draft.CurvePoints,
    "Editing an unbound output creates its own live binding.");
ipc.ChangeOutput("");
Check(!following.CanEdit, "No endpoint must not write a device binding.");
AppSettings.DeviceCorrections = AppSettings.DeviceCorrections with { Enabled = false };
Check(following.Draft.CurvePoints == global.CurvePoints, "Leaving device mode restores global settings.");
following.MovePoint(0, 20, 7);
var globalEdit = AppSettings.Dsp;
ipc.ChangeOutput("b");
Check(following.Draft.CurvePoints == globalEdit.CurvePoints, "Global edits survive output switching.");
await following.CloseAsync();
Check(database.SavedDsp == globalEdit, "Close persists the final global edit.");
Console.WriteLine("PASS: immediate device bindings, A/B/A switching, unbound/no output and global mode.");

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
var live = new ConvolutionCurveViewModel(ipc, store, database);
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
ipc.ChangeOutput("missing");
await CheckResponses("unbound bypass");
Check(eqResponse.Current!.Fir.SelectMany(x => x).All(x => x == 0), "Unbound response must have no FIR contribution.");
AppSettings.DeviceCorrections = AppSettings.DeviceCorrections with { Enabled = false };
live.MovePoint(0, 20, 1); live.MovePoint(0, 20, 2); live.MovePoint(0, 20, 3);
await CheckResponses("latest edit wins");
await live.CloseAsync();
eqResponse.Unload(); convolutionResponse.Unload();
Console.WriteLine("PASS: both real response VMs refresh on EQ, convolution, device binding and rapid edits.");

var saving = new ConvolutionCurveViewModel(ipc, store, database);
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

var offline = new ConvolutionCurveViewModel(ipc, store, database);
await offline.OpenAsync();
offline.MovePoint(0, 20, -7);
ipc.FailPublish = true;
Check(await offline.CloseAsync() && database.SavedDsp!.CurvePoints == offline.Draft.CurvePoints,
    "Offline playback must not prevent saving or closing; IPC owns synchronization retries.");
ipc.FailPublish = false;
Console.WriteLine("PASS: offline playback still saves live edits and permits closing.");
