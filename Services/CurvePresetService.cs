using BassPlayerIpc.Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Services;

public sealed class CurvePresetService
{
    private static string PathName => Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, "ConvolutionCurves.json");
    public async Task<List<CurvePreset>> LoadAsync()
    {
        if (!File.Exists(PathName)) return [];
        if (new FileInfo(PathName).Length > 256 * 1024) throw new InvalidDataException();
        var presets = JsonSerializer.Deserialize(await File.ReadAllTextAsync(PathName), CurvePresetJsonContext.Default.ListCurvePreset) ?? [];
        if (presets.Count > 100) throw new InvalidDataException();
        foreach (var preset in presets)
        {
            if (preset == null || string.IsNullOrWhiteSpace(preset.Name) || preset.Name.Length > 80) throw new InvalidDataException();
            CorrectionCurve.Parse(preset.Points);
        }
        return presets.Select(p => p with { Points = CorrectionCurve.Encode(CorrectionCurve.Parse(p.Points)) }).ToList();
    }
    public async Task SaveAsync(List<CurvePreset> presets)
    {
        await File.WriteAllTextAsync(PathName + ".tmp", JsonSerializer.Serialize(presets, CurvePresetJsonContext.Default.ListCurvePreset));
        File.Move(PathName + ".tmp", PathName, true);
    }
}
