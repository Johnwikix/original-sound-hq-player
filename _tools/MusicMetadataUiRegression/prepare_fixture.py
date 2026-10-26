"""Extract shipping loading/content bindings and optional tags without changing binding modes."""
from pathlib import Path
import xml.etree.ElementTree as ET

here = Path(__file__).resolve().parent
root = here.parent.parent
ns = "{http://schemas.microsoft.com/winfx/2006/xaml/presentation}"
xn = "{http://schemas.microsoft.com/winfx/2006/xaml}"
ET.register_namespace("", ns[1:-1]); ET.register_namespace("x", xn[1:-1])
source = ET.parse(root / "View/SubView/MusicDetailsWindow.xaml")
root_grid = source.getroot().find(ns + "Grid")
loading = next(e for e in root_grid if "BoolToVisibility(IsLoading)" in e.get("Visibility", ""))
content = next(e for e in root_grid if "BoolToNVisibility(IsLoading)" in e.get("Visibility", ""))
area = next(e for e in source.iter(ns + "Expander") if e.get(xn + "Uid") == "MusicAdditionalTags")
assert len(list(area.iter(ns + "TextBox"))) == 6
resources = {e.get("name"): e.findtext("value") for e in ET.parse(root / "Strings/zh-CN/Resources.resw").getroot().findall("data")}
for e in [*area.iter(), *loading.iter()]:
    uid = e.attrib.pop(xn + "Uid", None)
    if uid:
        for prop in ("Header", "PlaceholderText", "Text"):
            if uid + "." + prop in resources: e.set(prop, resources[uid + "." + prop])
        if uid != "LoadingGrid":
            e.set(xn + "Name", "OptionalTags" if uid == "MusicAdditionalTags" else uid.removeprefix("MusicTag") + "Input")
loading.set(xn + "Name", "LoadingOverlay")
editor = ET.Element(ns + "Grid", {xn + "Name": "EditorContent", "Visibility": content.get("Visibility")})
editor.append(area)
markup = '<UserControl x:Class="MusicMetadataUiRegression.MetadataPanel" xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"><Grid Padding="20">'
markup += ET.tostring(loading, encoding="unicode") + ET.tostring(editor, encoding="unicode") + '</Grid></UserControl>'
(here / "MetadataPanel.xaml").write_bytes(markup.replace("\r\n", "\n").replace("\n", "\r\n").encode("utf-8"))
