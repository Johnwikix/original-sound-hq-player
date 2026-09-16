using System.Text.Json;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.ViewModel;

static void Check(bool condition, string reason)
{
    if (!condition) throw new Exception(reason);
}

string directory = Path.Combine(Path.GetTempPath(), "music-agreement-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    string path = Path.Combine(directory, "agreement.json");
    var store = new AgreementAcceptanceStore(path);
    string version = AgreementAcceptanceStore.CurrentVersion;
    Check(!await store.HasAcceptedAsync(version), "New install must require explicit acceptance.");
    var vm = await UserAgreementViewModel.LoadAsync(store);
    Check(vm.Version == version && vm.Document.Id == "terms" && vm.Languages.Length == 2, "Packaged terms/version must be available offline.");
    Check(!File.Exists(path), "Reading documents must never record acceptance.");
    vm.DocumentIndex = 1;
    vm.LanguageIndex = 1;
    Check(vm.Document.Id == "privacy" && vm.Document.Title == "Privacy Policy", "Language changes retain selected document and update text.");
    vm.DocumentIndex = -1;
    vm.LanguageIndex = -1;
    Check(vm.Document.Id == "privacy", "Transient ComboBox deselection must not invalidate the document.");
    Check(await vm.AcceptAsync(), "Explicit acceptance must persist.");
    var receipt = JsonSerializer.Deserialize(await File.ReadAllTextAsync(path), LegalJsonContext.Default.AgreementReceipt)!;
    Check(receipt.Version == version && receipt.Language == "en" && receipt.AcceptedAtUtc.Offset == TimeSpan.Zero,
        "Receipt must identify version, language and UTC time.");
    Check(await new AgreementAcceptanceStore(path).HasAcceptedAsync(version), "Acceptance must survive reopening the app.");
    Check(!await store.HasAcceptedAsync("future-material-version"), "Material terms revision must require a new acceptance.");
    Check(Directory.GetFiles(directory, "*.tmp").Length == 0, "Atomic acceptance must not leave temporary files.");
    Console.WriteLine("PASS: offline documents, explicit acceptance, language selection, persistence and version changes.");

    await File.WriteAllTextAsync(path, "{broken");
    Check(!await store.HasAcceptedAsync(version), "Corrupt receipt must not bypass agreement.");
    await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new AgreementReceipt(version, "en", default), LegalJsonContext.Default.AgreementReceipt));
    Check(!await store.HasAcceptedAsync(version), "Incomplete receipt must not bypass agreement.");
    Check(await vm.AcceptAsync() && await store.HasAcceptedAsync(version), "Explicit acceptance must recover a corrupt/incomplete receipt.");
    string blockedPath = Path.Combine(directory, "blocked.json");
    Directory.CreateDirectory(blockedPath);
    var blockedVm = await UserAgreementViewModel.LoadAsync(new AgreementAcceptanceStore(blockedPath));
    Check(!await blockedVm.AcceptAsync() && blockedVm.HasError && !blockedVm.IsSaving, "Write failure must keep dialog open and expose a retryable error.");
    Directory.Delete(blockedPath);
    Check(await blockedVm.AcceptAsync() && !blockedVm.HasError, "Retry must work after storage failure is fixed.");
    Console.WriteLine("PASS: missing/corrupt receipts fail closed, failed writes cannot accept, retry succeeds.");
}
finally { Directory.Delete(directory, recursive: true); }
