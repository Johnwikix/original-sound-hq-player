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
var vm = new ConvolutionCurveViewModel(ipc, store);
await vm.OpenAsync();
vm.SelectedPreset = vm.Presets[0];
Check(!vm.CanUpdatePreset && vm.CanDeletePreset, "Unchanged preset cannot be updated.");
vm.MovePoint(0, 20, 3);
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
var globalBeforeLoad = AppSettings.Dsp;
vm.LoadCorrectionDraft(new DspSettings { ConvolutionSource = ConvolutionSource.Curve,
    CurvePoints = "20,-6;20000,-6" });
Check(vm.Draft.CurvePoints == "20,-6;20000,-6" && vm.SelectedPreset == null,
    "Loading a device correction must replace the dialog draft and clear an unmatched preset.");
Check(AppSettings.Dsp == globalBeforeLoad, "Loading a binding must not commit global settings.");
vm.SelectedPreset = vm.Presets[0];
Check(vm.Draft.CurvePoints == vm.Presets[0].Points && vm.Draft.CurvePresetName == vm.Presets[0].Name,
    "After loading a binding, selecting a preset must supply the latest binding draft.");
string curveBeforeWaveLoad = vm.Draft.CurvePoints;
vm.LoadCorrectionDraft(new DspSettings { ConvolutionSource = ConvolutionSource.Wave, ImpulsePath = "test.wav" });
Check(vm.Draft.CurvePoints == curveBeforeWaveLoad, "Wave bindings must not silently turn into curves.");
vm.Close(false);
Console.WriteLine("PASS: select/edit/update/delete, failure recovery, concurrent commands, draft retention, and save-as.");

var a = new CurvePreset("Headphones", "20,-6;20000,-6");
var b = new CurvePreset("Speakers", "20,3;20000,3");
await store.SaveAsync([a, b]);
AppSettings.DeviceCorrections = new DeviceCorrections { Enabled = true, Bindings = [
    new DeviceCorrection { DeviceId = "a", Settings = new DspSettings { ConvolutionSource = ConvolutionSource.Curve, CurvePoints = a.Points, CurvePresetName = a.Name } },
    new DeviceCorrection { DeviceId = "b", Settings = new DspSettings { ConvolutionSource = ConvolutionSource.Curve, CurvePoints = b.Points, CurvePresetName = b.Name } }
] };
ipc.ChangeOutput("a");
var following = new ConvolutionCurveViewModel(ipc, store);
await following.OpenAsync();
Check(following.Draft.CurvePoints == a.Points && following.SelectedPreset?.Name == a.Name,
    "Opening the dialog must show the actual device binding, not the global draft.");
ipc.ChangeOutput("b");
Check(following.Draft.CurvePoints == b.Points && following.SelectedPreset?.Name == b.Name,
    "Hotplug must switch curve and preset without manually loading.");
following.SelectedPreset = following.Presets[0];
ipc.ChangeOutput("b");
Check(following.Draft.CurvePoints == a.Points, "An ordinary same-device notification must not overwrite edits.");
ipc.ChangeOutput("a");
ipc.ChangeOutput("b");
Check(following.Draft.CurvePoints == b.Points && ipc.LastPreview == null && ipc.Restores >= 3,
    "A real device transition must cancel the old audition and follow the binding.");
Check(AppSettings.Dsp == globalBeforeLoad, "Automatic following must not overwrite global preferences.");
ipc.ChangeOutput("unbound");
Check(following.Draft.CurvePoints == CorrectionCurve.Flat && following.SelectedPreset == null,
    "Unbound output must not display the previous device curve.");
AppSettings.DeviceCorrections = AppSettings.DeviceCorrections with { Enabled = false };
following.RefreshOutputCorrection();
following.Close(false);
Console.WriteLine("PASS: dialog opens on actual binding, hotplug switches curve/preset, same-device edits survive, and old audition is cancelled.");
