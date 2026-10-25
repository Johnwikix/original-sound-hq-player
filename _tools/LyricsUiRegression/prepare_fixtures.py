"""Refresh compiled test fragments directly from shipping XAML before building the UI harness."""
from pathlib import Path
import xml.etree.ElementTree as ET

here = Path(__file__).resolve().parent
repo = here.parent.parent
ns = "{http://schemas.microsoft.com/winfx/2006/xaml/presentation}"
xn = "{http://schemas.microsoft.com/winfx/2006/xaml}"
for prefix, uri in [("", ns[1:-1]), ("x", xn[1:-1]), ("controls", "using:CommunityToolkit.WinUI.Controls")]:
    ET.register_namespace(prefix, uri)
pivot = ET.parse(repo / "View/SubView/MusicDetailsWindow.xaml").find(".//" + ns + "Pivot")
assert pivot is not None and len(pivot.findall(ns + "PivotItem")) == 2
pivot.attrib.pop("Grid.Column", None)
cards = [e for e in ET.parse(repo / "View/SubView/Settings/LyricsSettingsControl.xaml").iter()
         if e.attrib.get(xn + "Uid") in ("LyricsFilePriority", "LyricsSourcePriority")]
assert len(cards) == 2
for index, box in enumerate(pivot.iter(ns + "TextBox")):
    box.set("AutomationProperties.AutomationId", "OriginalInput" if index == 0 else "TranslationInput")
resources = {d.attrib["name"]: d.findtext("value") for d in
             ET.parse(repo / "Strings/zh-CN/Resources.resw").getroot().findall("data")}
for element in [*cards, *pivot.iter()]:
    uid = element.attrib.pop(xn + "Uid", None)
    if uid:
        for prop in ["Header", "Description", "Text"]:
            if uid + "." + prop in resources:
                element.set(prop, resources[uid + "." + prop])
markup = '''<UserControl x:Class="LyricsUiRegression.TestPanel" xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" xmlns:tool="using:WinUIMusicPlayer.Utils" xmlns:controls="using:CommunityToolkit.WinUI.Controls"><StackPanel Padding="20" Spacing="8">'''
markup += "".join(ET.tostring(card, encoding="unicode") for card in cards) + ET.tostring(pivot, encoding="unicode")
markup += '''<Button Content="Save test draft" AutomationProperties.AutomationId="SaveDraft" Click="Save_Click"/><TextBlock x:Name="Status" AutomationProperties.AutomationId="Status"/><TextBlock Text="{x:Bind LyricsEditor.Error, Mode=OneWay}"/></StackPanel></UserControl>'''
(here / "TestPanel.xaml").write_text(markup, encoding="utf-8")
