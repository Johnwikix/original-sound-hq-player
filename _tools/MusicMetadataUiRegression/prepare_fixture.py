"""Extract the shipping optional-tag area, preserving its compiled bindings and layout."""
from pathlib import Path
import xml.etree.ElementTree as ET

here = Path(__file__).resolve().parent
root = here.parent.parent
ns = "{http://schemas.microsoft.com/winfx/2006/xaml/presentation}"
xn = "{http://schemas.microsoft.com/winfx/2006/xaml}"
ET.register_namespace("", ns[1:-1]); ET.register_namespace("x", xn[1:-1])
source = ET.parse(root / "View/SubView/MusicDetailsWindow.xaml")
area = next(e for e in source.iter(ns + "Expander") if e.get(xn + "Uid") == "MusicAdditionalTags")
assert len(list(area.iter(ns + "TextBox"))) == 6
resources = {e.get("name"): e.findtext("value") for e in ET.parse(root / "Strings/zh-CN/Resources.resw").getroot().findall("data")}
for e in area.iter():
    uid = e.attrib.pop(xn + "Uid", None)
    if uid:
        for prop in ("Header", "PlaceholderText"):
            if uid + "." + prop in resources: e.set(prop, resources[uid + "." + prop])
        e.set(xn + "Name", "OptionalTags" if uid == "MusicAdditionalTags" else uid.removeprefix("MusicTag") + "Input")
markup = '<UserControl x:Class="MusicMetadataUiRegression.MetadataPanel" xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"><Grid Padding="20">'
markup += ET.tostring(area, encoding="unicode") + '</Grid></UserControl>'
(here / "MetadataPanel.xaml").write_bytes(markup.replace("\r\n", "\n").replace("\n", "\r\n").encode("utf-8"))
