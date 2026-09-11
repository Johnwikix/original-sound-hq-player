using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Windows.Storage.Pickers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using WinUIMusicPlayer.Controls.Equalizer;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.View.SubView
{
    /// <summary>
    /// 均衡器对话框：预设下拉（内置 + 任意数量自定义）、保存/删除自定义预设、
    /// 导入/导出（.json，EqPreset v1 结构含每段 Q 值）。增益/Q 编辑走 250ms 防抖提交
    /// （持久化 + IPC 全量同步），拖动过程不产生中间 IO。
    /// </summary>
    public sealed partial class EqualizerDialog : ContentDialog
    {
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
        private readonly DispatcherQueueTimer _stateTimer;
        private bool _dialogOpen, _refreshingState, _eqCommitInFlight;
        private int _stateGeneration;
        private List<SaveEqualizerPreset> _customPresets = new();
        private bool _isSyncingUi;
        private bool _isLoaded;
        /// <summary>滑条修改待写回的自定义预设 Id（防抖提交时落盘）。</summary>
        private int? _pendingDirtyCustomId;

        /// <summary>均衡器状态变更（防抖后）提交完成：订阅方负责 IPC 全量同步。</summary>
        public event EventHandler? EqualizerCommitted;

        public EqualizerDialog()
        {
            InitializeComponent();
            ToolTipService.SetToolTip(AddPresetButton, ToolUtils.GetString("EqAddPresetToolTip"));
            ToolTipService.SetToolTip(SavePresetButton, ToolUtils.GetString("EqSavePresetToolTip"));
            ToolTipService.SetToolTip(DeletePresetButton, ToolUtils.GetString("EqDeletePresetToolTip"));
            ToolTipService.SetToolTip(MoreOptionsButton, ToolUtils.GetString("EqMoreOptionsToolTip"));

            // Flyout 内文案（GetString 不支持 x:Uid 的属性后缀键，统一代码赋值）
            AddPresetFlyoutTitle.Text = ToolUtils.GetString("EqPresetNameTitle");
            AddPresetOverwriteHint.Text = ToolUtils.GetString("EqOverwritePreset");
            AddPresetConfirmButton.Content = ToolUtils.GetString("PrimaryButton");
            DeletePresetConfirmText.Text = ToolUtils.GetString("EqDeletePresetConfirm");
            DeletePresetConfirmButton.Content = ToolUtils.GetString("EqDeleteAction");

            _commitTimer = DispatcherQueue.CreateTimer();
            _commitTimer.Interval = TimeSpan.FromMilliseconds(CommitDebounceMs);
            _commitTimer.IsRepeating = false;
            _commitTimer.Tick += (_, _) => CommitChanges();
            ToggleSwitchEqualizer.IsEnabled = false;
            _stateTimer = DispatcherQueue.CreateTimer();
            _stateTimer.Interval = TimeSpan.FromMilliseconds(500);
            _stateTimer.Tick += async (_, _) => await RefreshPlaybackStateAsync();
            Opened += async (_, _) =>
            {
                _dialogOpen = true;
                _stateGeneration++;
                _isLoaded = false;
                _isSyncingUi = true;
                ToggleSwitchEqualizer.IsEnabled = false;
                ToggleSwitchEqualizer.IsOn = false;
                _isSyncingUi = false;
                await InitializeAsync();
                if (!_dialogOpen) return;
                _stateTimer.Start();
                await RefreshPlaybackStateAsync();
            };
            Closed += (_, _) => { _dialogOpen = false; _stateGeneration++; _stateTimer.Stop(); };

            // Esc/系统关闭同样走持久化，防止挂起的调整丢失
            Closing += (_, _) =>
            {
                _commitTimer.Stop();
                if (_isLoaded) CommitChanges();
            };
            Equalizer.BandEdited += OnBandEdited;
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
            _isLoaded = true;
            UpdatePresetButtonState();
            await RefreshPlaybackStateAsync();
        }

        /// <summary>按真实会话刷新显示；旁路仅影响界面，不覆盖保存的 EQ 偏好。</summary>
        private async Task RefreshPlaybackStateAsync()
        {
            if (!_dialogOpen || !_isLoaded || _refreshingState || _eqCommitInFlight) return;
            _refreshingState = true;
            int generation = _stateGeneration;
            try
            {
                var state = await App.Services.GetRequiredService<IpcService>().GetDspStateAsync();
                if (!_dialogOpen || _eqCommitInFlight || generation != _stateGeneration) return;
                _isSyncingUi = true;
                bool available = state is { RenderKind: 0, Channels: <= 2 };
                ToggleSwitchEqualizer.IsEnabled = available;
                ToggleSwitchEqualizer.IsOn = available && state!.Value.EqualizerActive;
                DspAvailabilityBar.IsOpen = !available;
                DspAvailabilityBar.Message = ToolUtils.GetString(state == null ? "DspStateUnavailable"
                    : state.Value.RenderKind != 0 ? "DspBitstreamBypass" : "DspStereoOnly");
            }
            finally { _isSyncingUi = false; _refreshingState = false; }
        }

        #region 预设下拉

        private void RebuildPresetItems(string? selectTag = null)
        {
            _isSyncingUi = true;
            try
            {
                ComboBoxPresets.Items.Clear();
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
            return ComboBoxPresets.SelectedItem is ComboBoxItem item ? item.Tag?.ToString() ?? "Flat" : "Flat";
        }

        /// <summary>由持久化的预设名（AppSettings.EqualizerPreset）反查下拉项 Tag。</summary>
        private string ResolveTagFromStateName()
        {
            string name = AppSettings.EqualizerPreset;
            if (string.IsNullOrEmpty(name)) return "Flat";
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
            // 唯一会用新预设覆盖增益的入口：先把挂起的自定义预设修改落盘，防止串写
            FlushPendingCustomEdit();
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

        private void OnBandEdited(object? sender, int bandIndex)
        {
            if (!_isLoaded || _isSyncingUi) return;

            // 选中的是自定义预设：修改自动写回该预设（防抖落盘），下拉选项保持不变；
            // 选中的是内置预设：仅更新当前状态，内置预设不被隐式覆盖
            if (TryParseIdTag(GetSelectedTag(), out int id) && FindCustom(id) is not null)
            {
                _pendingDirtyCustomId = id;
            }
            _commitTimer.Stop();
            _commitTimer.Start();
        }

        /// <summary>提交当前均衡器状态：持久化 + 通知订阅方做 IPC 全量同步。</summary>
        private void CommitChanges()
        {
            PersistAll();
            AppSettings.OnEqUpdated();
            EqualizerCommitted?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>持久化：状态行（SaveEqualizer）+ 挂起的自定义预设行自动写回。</summary>
        private void PersistAll()
        {
            AppSettings.EqualizerStr = SerializeCurrent(AppSettings.EqualizerPreset);
            _ = App.Services.GetRequiredService<MusicDatabaseService>().SaveEqualizerSettingAsync();
            FlushPendingCustomEdit();
        }

        /// <summary>把挂起的滑条修改写回来源自定义预设行（以当前增益为准）。</summary>
        private void FlushPendingCustomEdit()
        {
            if (_pendingDirtyCustomId is int id)
            {
                _pendingDirtyCustomId = null;
                if (FindCustom(id) is { } row)
                {
                    row.EqualizerStr = SerializeCurrent(row.Name);
                    _ = App.Services.GetRequiredService<MusicDatabaseService>().UpdateEqualizerPreset(row);
                }
            }
        }

        private async void ToggleSwitchEqualizer_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isSyncingUi || !_isLoaded) return;

            AppSettings.IsEqualizerEnabled = ToggleSwitchEqualizer.IsOn;
            _stateGeneration++;
            _eqCommitInFlight = true;
            ToggleSwitchEqualizer.IsEnabled = false;
            try
            {
                await App.Services.GetRequiredService<BassPlayerCommandService>().UpdateEqStateAsync();
                PersistAll();
            }
            finally { _eqCommitInFlight = false; }
            await RefreshPlaybackStateAsync();
        }

        #endregion

        #region 自定义预设增删

        #region 新增（Flyout 输入名称：对话框打开期间不能再叠加 ContentDialog）

        private void AddPresetFlyout_Opening(object? sender, object e)
        {
            AddPresetNameBox.Text = BuildDefaultNewName();
            UpdateAddPresetHint();
            DispatcherQueue.TryEnqueue(() =>
            {
                AddPresetNameBox.Focus(FocusState.Programmatic);
                AddPresetNameBox.SelectAll();
            });
        }

        private void AddPresetNameBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateAddPresetHint();
        }

        private void UpdateAddPresetHint()
        {
            string name = AddPresetNameBox.Text.Trim();
            bool exists = name.Length > 0 && _customPresets.Any(p => p.Name == name);
            AddPresetOverwriteHint.Visibility = exists ? Visibility.Visible : Visibility.Collapsed;
        }

        private void AddPresetNameBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                e.Handled = true;
                _ = CommitAddPresetFromFlyout();
            }
        }

        private async void AddPresetConfirmButton_Click(object sender, RoutedEventArgs e)
        {
            await CommitAddPresetFromFlyout();
        }

        private async Task CommitAddPresetFromFlyout()
        {
            string name = AddPresetNameBox.Text.Trim();
            if (name.Length == 0) return;
            AddPresetFlyout.Hide();

            var db = App.Services.GetRequiredService<MusicDatabaseService>();
            SaveEqualizerPreset? target = _customPresets.FirstOrDefault(p => p.Name == name);
            if (target is not null)
            {
                target.EqualizerStr = SerializeCurrent(name);
                await db.UpdateEqualizerPreset(target);
            }
            else
            {
                target = new SaveEqualizerPreset { Name = name, EqualizerStr = SerializeCurrent(name) };
                // sqlite-net 插入后自动回填自增 Id，不能用 InsertAsync 的返回值（受影响行数）覆盖
                await db.InsertEqualizerPreset(target);
                _customPresets.Add(target);
            }
            AppSettings.EqualizerPreset = name;
            RebuildPresetItems(IdTag(target.Id));
            UpdatePresetButtonState();
            CommitChanges();
        }

        #endregion

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

        #region 删除（Flyout 确认）

        private void DeletePresetFlyout_Opening(object? sender, object e)
        {
            DeletePresetTargetText.Text = TryParseIdTag(GetSelectedTag(), out int id) && FindCustom(id) is { } row
                ? row.Name
                : string.Empty;
        }

        private async void DeletePresetConfirmButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_isLoaded) return;
            if (!TryParseIdTag(GetSelectedTag(), out int id) || FindCustom(id) is not { } row) return;
            DeletePresetFlyout.Hide();

            await App.Services.GetRequiredService<MusicDatabaseService>().DeleteEqualizerPreset(row.Id);
            _customPresets.Remove(row);

            ApplyPreset(_builtIns["Flat"]);
            AppSettings.EqualizerPreset = "Flat";
            RebuildPresetItems("Flat");
            UpdatePresetButtonState();
            CommitChanges();
        }

        #endregion

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
                    ShowEqError("EqImportFailed");
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
                    // sqlite-net 插入后自动回填自增 Id，不能用 InsertAsync 的返回值（受影响行数）覆盖
                    await db.InsertEqualizerPreset(target);
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
                ShowEqError("EqImportFailed");
            }
        }

        private async void ExportPresetItem_Click(object sender, RoutedEventArgs e)
        {
            if (!_isLoaded) return;
            try
            {
                string name = AppSettings.EqualizerPreset;
                if (string.IsNullOrEmpty(name)) name = ToolUtils.GetString("EqPresetCustom");
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
                ShowEqError("EqExportFailed");
            }
        }

        /// <summary>对话框内错误提示（InfoBar）——打开期间无法弹出第二个 ContentDialog。</summary>
        private void ShowEqError(string messageKey)
        {
            EqNotifyBar.Title = ToolUtils.GetString(messageKey);
            EqNotifyBar.IsOpen = true;
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
