using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using WinUIMusicPlayer.Controls.Equalizer;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.View.SubView
{
    /// <summary>
    /// 均衡器对话框：预设下拉（内置 + 任意数量自定义）、保存/删除自定义预设、
    /// 导入/导出（.json，EqPreset v1 结构含预留 Q 值）。滑条编辑走 250ms 防抖提交
    /// （持久化 + IPC 全量同步），拖动过程不产生中间 IO。
    /// </summary>
    public sealed partial class EqualizerDialog : ContentDialog
    {
        private const string TagCustom = "Custom";
        private const string TagIdPrefix = "id:";
        private const int CommitDebounceMs = 250;

        private static readonly Dictionary<string, string> BuiltInResourceKeys = new()
        {
            ["Flat"] = "EqPresetFlat",
            ["Rock"] = "EqPresetRock",
            ["Pop"] = "EqPresetPop",
            ["Jazz"] = "EqPresetJazz",
            ["Classical"] = "EqPresetClassical",
            ["Electronic"] = "EqPresetElectronic",
            ["Vocal"] = "EqPresetVocal"
        };

        private readonly IReadOnlyDictionary<string, EqPreset> _builtIns = EqualizerHelper.CreateBuiltInPresets();
        private readonly DispatcherQueueTimer _commitTimer;
        private List<SaveEqualizerPreset> _customPresets = new();
        private bool _isSyncingUi;
        private bool _isLoaded;

        /// <summary>均衡器状态变更（防抖后）提交完成：订阅方负责 IPC 全量同步。</summary>
        public event EventHandler? EqualizerCommitted;

        public EqualizerDialog()
        {
            InitializeComponent();
            ToolTipService.SetToolTip(AddPresetButton, ToolUtils.GetString("EqAddPresetToolTip"));
            ToolTipService.SetToolTip(SavePresetButton, ToolUtils.GetString("EqSavePresetToolTip"));
            ToolTipService.SetToolTip(DeletePresetButton, ToolUtils.GetString("EqDeletePresetToolTip"));
            ToolTipService.SetToolTip(MoreOptionsButton, ToolUtils.GetString("EqMoreOptionsToolTip"));

            _commitTimer = DispatcherQueue.CreateTimer();
            _commitTimer.Interval = TimeSpan.FromMilliseconds(CommitDebounceMs);
            _commitTimer.IsRepeating = false;
            _commitTimer.Tick += (_, _) => CommitChanges();

            // Esc/系统关闭同样走持久化，防止挂起的调整丢失
            Closing += (_, _) =>
            {
                _commitTimer.Stop();
                if (_isLoaded) CommitChanges();
            };
            Equalizer.GainEdited += OnGainEdited;
            _ = InitializeAsync();
        }

        private async Task InitializeAsync()
        {
            try
            {
                _customPresets = await App.Services.GetRequiredService<MusicDatabaseService>().GetEqualizerPresets();
            }
            catch
            {
                _customPresets = new List<SaveEqualizerPreset>();
            }
            Equalizer.SetBands(AppSettings.EqualizerBands);
            RebuildPresetItems();
            ToggleSwitchEqualizer.IsOn = AppSettings.IsEqualizerEnabled;
            _isLoaded = true;
            UpdatePresetButtonState();
        }

        #region 预设下拉

        private void RebuildPresetItems(string? selectTag = null)
        {
            _isSyncingUi = true;
            try
            {
                ComboBoxPresets.Items.Clear();
                ComboBoxPresets.Items.Add(MakeComboItem(TagCustom, ToolUtils.GetString("EqPresetCustom")));
                foreach (string key in EqualizerHelper.BuiltInKeys)
                {
                    ComboBoxPresets.Items.Add(MakeComboItem(key, ToolUtils.GetString(BuiltInResourceKeys[key])));
                }
                foreach (SaveEqualizerPreset row in _customPresets)
                {
                    ComboBoxPresets.Items.Add(MakeComboItem(IdTag(row.Id), row.Name));
                }
                SelectTag(selectTag ?? ResolveTagFromStateName());
            }
            finally
            {
                _isSyncingUi = false;
            }
        }

        private static ComboBoxItem MakeComboItem(string tag, string content)
        {
            return new ComboBoxItem { Tag = tag, Content = content };
        }

        private void SelectTag(string tag)
        {
            foreach (object item in ComboBoxPresets.Items)
            {
                if (item is ComboBoxItem comboItem && comboItem.Tag?.ToString() == tag)
                {
                    ComboBoxPresets.SelectedItem = comboItem;
                    return;
                }
            }
        }

        private string GetSelectedTag()
        {
            return ComboBoxPresets.SelectedItem is ComboBoxItem item ? item.Tag?.ToString() ?? TagCustom : TagCustom;
        }

        /// <summary>由持久化的预设名（AppSettings.EqualizerPreset）反查下拉项 Tag。</summary>
        private string ResolveTagFromStateName()
        {
            string name = AppSettings.EqualizerPreset;
            if (string.IsNullOrEmpty(name) || name == TagCustom) return TagCustom;
            foreach (SaveEqualizerPreset row in _customPresets)
            {
                if (row.Name == name) return IdTag(row.Id);
            }
            foreach (string key in EqualizerHelper.BuiltInKeys)
            {
                if (key == name) return key;
            }
            return "Flat";
        }

        private string ResolveStateName(string tag)
        {
            if (tag == TagCustom) return TagCustom;
            if (TryParseIdTag(tag, out int id) && FindCustom(id) is { } row) return row.Name;
            return BuiltInResourceKeys.ContainsKey(tag) ? tag : "Flat";
        }

        private static string IdTag(int id) => $"{TagIdPrefix}{id}";

        private static bool TryParseIdTag(string tag, out int id)
        {
            id = 0;
            return tag.StartsWith(TagIdPrefix, StringComparison.Ordinal)
                && int.TryParse(tag.AsSpan(TagIdPrefix.Length), out id);
        }

        private SaveEqualizerPreset? FindCustom(int id)
        {
            return _customPresets.FirstOrDefault(p => p.Id == id);
        }

        private void UpdatePresetButtonState()
        {
            bool isCustomSelected = _isLoaded && TryParseIdTag(GetSelectedTag(), out _);
            SavePresetButton.IsEnabled = isCustomSelected;
            DeletePresetButton.IsEnabled = isCustomSelected;
        }

        private async void ComboBoxPresets_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isSyncingUi || !_isLoaded) return;
            string tag = GetSelectedTag();
            UpdatePresetButtonState();
            if (tag == TagCustom) return; // “自定义”仅表示手动调整中，不改动当前增益

            _isSyncingUi = true;
            try
            {
                EqPreset preset;
                if (TryParseIdTag(tag, out int presetId))
                {
                    if (FindCustom(presetId) is not { } row) return;
                    preset = EqualizerHelper.Parse(row.EqualizerStr, row.Name) ?? EqualizerHelper.Normalize(null);
                }
                else
                {
                    preset = _builtIns.TryGetValue(tag, out EqPreset? builtIn) ? builtIn : EqualizerHelper.Normalize(null);
                }
                ApplyPreset(preset);
                AppSettings.EqualizerPreset = ResolveStateName(tag);
                CommitChanges();
            }
            finally
            {
                _isSyncingUi = false;
            }
        }

        /// <summary>把预设增益写入运行时频段状态并刷新滑条（数组引用不变，IPC 侧始终读同一状态）。</summary>
        private void ApplyPreset(EqPreset preset)
        {
            EqBand[] bands = AppSettings.EqualizerBands;
            for (int i = 0; i < bands.Length && i < preset.Bands.Count; i++)
            {
                bands[i].GainDb = preset.Bands[i].GainDb;
                bands[i].Q = preset.Bands[i].Q;
            }
            Equalizer.SetBands(bands);
        }

        private string SerializeCurrent(string name)
        {
            return EqualizerHelper.Serialize(EqualizerHelper.Snapshot(name, AppSettings.EqualizerBands));
        }

        #endregion

        #region 编辑与提交（防抖）

        private void OnGainEdited(object? sender, int bandIndex)
        {
            if (!_isLoaded || _isSyncingUi) return;
            if (AppSettings.EqualizerPreset != TagCustom)
            {
                // 拖动即脱离预设：切到“自定义”项（不改动增益），防抖结束后统一提交
                AppSettings.EqualizerPreset = TagCustom;
                _isSyncingUi = true;
                SelectTag(TagCustom);
                UpdatePresetButtonState();
                _isSyncingUi = false;
            }
            _commitTimer.Stop();
            _commitTimer.Start();
        }

        /// <summary>提交当前均衡器状态：持久化 + 通知订阅方做 IPC 全量同步。</summary>
        private void CommitChanges()
        {
            AppSettings.EqualizerStr = SerializeCurrent(AppSettings.EqualizerPreset);
            _ = App.Services.GetRequiredService<MusicDatabaseService>().SaveEqualizerSettingAsync();
            AppSettings.OnEqUpdated();
            EqualizerCommitted?.Invoke(this, EventArgs.Empty);
        }

        private async void ToggleSwitchEqualizer_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isSyncingUi) return;

            AppSettings.IsEqualizerEnabled = ToggleSwitchEqualizer.IsOn;

            // Await the server's real applied state: the server rejects the switch in
            // some modes (e.g. DSD over exclusive output), in which case roll the UI
            // switch back so the displayed state matches reality.
            bool? real = await App.Services.GetRequiredService<BassPlayerCommandService>().UpdateEqStateAsync();
            if (real is bool r && r != AppSettings.IsEqualizerEnabled)
            {
                AppSettings.IsEqualizerEnabled = r;
                _isSyncingUi = true;
                ToggleSwitchEqualizer.IsOn = r;
                _isSyncingUi = false;
            }
            if (_isLoaded)
            {
                _ = App.Services.GetRequiredService<MusicDatabaseService>().SaveEqualizerSettingAsync();
            }
        }

        #endregion

        #region 自定义预设增删

        /// <summary>新增：把当前均衡器另存为一个新的自定义预设（同名时询问覆盖）。</summary>
        private async void AddPresetButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_isLoaded) return;
            string name = await DialogHelper.ShowInputAsync(XamlRoot, "EqPresetNameTitle", BuildDefaultNewName());
            if (string.IsNullOrWhiteSpace(name)) return;
            name = name.Trim();

            var db = App.Services.GetRequiredService<MusicDatabaseService>();
            SaveEqualizerPreset? target = _customPresets.FirstOrDefault(p => p.Name == name);
            if (target is not null)
            {
                if (!await DialogHelper.ShowConfirmAsync(XamlRoot, "EqOverwritePreset")) return;
                target.EqualizerStr = SerializeCurrent(name);
                await db.UpdateEqualizerPreset(target);
            }
            else
            {
                target = new SaveEqualizerPreset { Name = name, EqualizerStr = SerializeCurrent(name) };
                target.Id = await db.InsertEqualizerPreset(target);
                _customPresets.Add(target);
            }
            AppSettings.EqualizerPreset = name;
            RebuildPresetItems(IdTag(target.Id));
            UpdatePresetButtonState();
            CommitChanges();
        }

        /// <summary>保存：覆盖写入当前选中的自定义预设（仅选中自定义预设时可用）。</summary>
        private async void SavePresetButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_isLoaded) return;
            if (!TryParseIdTag(GetSelectedTag(), out int id) || FindCustom(id) is not { } row) return;
            row.EqualizerStr = SerializeCurrent(row.Name);
            await App.Services.GetRequiredService<MusicDatabaseService>().UpdateEqualizerPreset(row);
        }

        private string BuildDefaultNewName()
        {
            return ComboBoxPresets.SelectedItem is ComboBoxItem { Content: string display } && !string.IsNullOrWhiteSpace(display)
                ? display
                : ToolUtils.GetString("EqPresetCustom");
        }

        private async void DeletePresetButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_isLoaded) return;
            if (!TryParseIdTag(GetSelectedTag(), out int id) || FindCustom(id) is not { } row) return;
            if (!await DialogHelper.ShowConfirmAsync(XamlRoot, "EqDeletePresetConfirm")) return;

            await App.Services.GetRequiredService<MusicDatabaseService>().DeleteEqualizerPreset(row.Id);
            _customPresets.Remove(row);

            ApplyPreset(_builtIns["Flat"]);
            AppSettings.EqualizerPreset = "Flat";
            RebuildPresetItems("Flat");
            UpdatePresetButtonState();
            CommitChanges();
        }

        #endregion

        #region 导入 / 导出

        private async void ImportPresetItem_Click(object sender, RoutedEventArgs e)
        {
            if (!_isLoaded) return;
            try
            {
                var picker = new FileOpenPicker(App.MainWindow.AppWindow.Id);
                picker.FileTypeFilter.Add(".json");
                var picked = await picker.PickSingleFileAsync();
                if (picked is null) return;

                string json = await File.ReadAllTextAsync(picked.Path);
                string fallbackName = Path.GetFileNameWithoutExtension(picked.Path);
                EqPreset? preset = EqualizerHelper.Parse(json, fallbackName);
                if (preset is null)
                {
                    await DialogHelper.ShowConfirmAsync(XamlRoot, "EqImportFailed");
                    return;
                }

                string name = string.IsNullOrWhiteSpace(preset.Name) ? fallbackName : preset.Name.Trim();
                if (name.Length == 0) name = ToolUtils.GetString("EqPresetCustom");

                var db = App.Services.GetRequiredService<MusicDatabaseService>();
                SaveEqualizerPreset? target = _customPresets.FirstOrDefault(p => p.Name == name);
                if (target is not null)
                {
                    target.EqualizerStr = EqualizerHelper.Serialize(preset);
                    await db.UpdateEqualizerPreset(target);
                }
                else
                {
                    target = new SaveEqualizerPreset { Name = name, EqualizerStr = EqualizerHelper.Serialize(preset) };
                    target.Id = await db.InsertEqualizerPreset(target);
                    _customPresets.Add(target);
                }

                ApplyPreset(preset);
                AppSettings.EqualizerPreset = name;
                RebuildPresetItems(IdTag(target.Id));
                UpdatePresetButtonState();
                CommitChanges();
            }
            catch
            {
                await DialogHelper.ShowConfirmAsync(XamlRoot, "EqImportFailed");
            }
        }

        private async void ExportPresetItem_Click(object sender, RoutedEventArgs e)
        {
            if (!_isLoaded) return;
            try
            {
                string name = AppSettings.EqualizerPreset == TagCustom
                    ? ToolUtils.GetString("EqPresetCustom")
                    : AppSettings.EqualizerPreset;
                var picker = new FileSavePicker(App.MainWindow.AppWindow.Id)
                {
                    SuggestedFileName = SanitizeFileName(name)
                };
                picker.FileTypeChoices.Add("JSON", new List<string> { ".json" });
                var picked = await picker.PickSaveFileAsync();
                if (picked is null) return;

                await File.WriteAllTextAsync(picked.Path, SerializeCurrent(name));
            }
            catch
            {
                await DialogHelper.ShowConfirmAsync(XamlRoot, "EqExportFailed");
            }
        }

        private static string SanitizeFileName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '_');
            }
            return name;
        }

        #endregion

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Hide();
        }
    }
}
