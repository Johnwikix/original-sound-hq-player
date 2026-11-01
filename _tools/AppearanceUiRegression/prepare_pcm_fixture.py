from pathlib import Path
import xml.etree.ElementTree as ET

here = Path(__file__).resolve().parent
root = here.parent.parent
ns = "{http://schemas.microsoft.com/winfx/2006/xaml/presentation}"
x = "{http://schemas.microsoft.com/winfx/2006/xaml}"
c = "{using:CommunityToolkit.WinUI.Controls}"
ET.register_namespace("", ns[1:-1])
ET.register_namespace("x", x[1:-1])
ET.register_namespace("controls", c[1:-1])
source = ET.parse(root / "View/SubView/Settings/DspSettingsControl.xaml")
keys = {"ResampleModeIndex": "ResampleMode", "ResampleTargetIndex": "ResampleTarget",
        "NormalizeLoudness": "Normalize", "CompressorEnabled": "Compressor", "TruePeakLimiterEnabled": "Limiter", "TruePeakCeilingDb": "Ceiling",
        "CrossfeedIndex": "CrossfeedPreset", "CrossfeedAmount": "CrossfeedAmount", "CrossfeedCutoffHz": "CrossfeedCutoff"}
resources = {e.get("name"): e.findtext("value") for e in
             ET.parse(root / "Strings/en/Resources.resw").getroot().findall("data")}
panel = ET.Element(ns + "StackPanel", {"Spacing": "4"})
for card in source.iter(c + "SettingsCard"):
    found = None
    for item in card.iter():
        for attribute in ("SelectedIndex", "IsOn", "Value"):
            value = item.get(attribute, "")
            for key, name in keys.items():
                if "ViewModel." + key + "," in value:
                    item.set(x + "Name", name)
                    found = name
    if found:
        card.set(x + "Name", found + "Card")
        for item in card.iter():
            uid = item.attrib.pop(x + "Uid", None)
            if uid:
                item.set("Content", resources[uid + ".Content"])
        panel.append(card)
assert len(panel) == len(keys), "Shipping PCM controls changed; update the fixture mapping."
ET.indent(panel, space="    ")
markup = ('<UserControl x:Class="AppearanceUiRegression.PcmControl" '
          'xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" '
          'xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" '
          'xmlns:tool="using:WinUIMusicPlayer.Utils"><ScrollViewer>\n')
markup += ET.tostring(panel, encoding="unicode") + "\n</ScrollViewer></UserControl>\n"
(here / "PcmControl.xaml").write_bytes(markup.replace("\r\n", "\n").replace("\n", "\r\n").encode("utf-8"))
