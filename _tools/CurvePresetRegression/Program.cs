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
vm.Close(false);
Console.WriteLine("PASS: select/edit/update/delete, failure recovery, concurrent commands, draft retention, and save-as.");
