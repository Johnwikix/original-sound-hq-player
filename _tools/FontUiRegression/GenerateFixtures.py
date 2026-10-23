from pathlib import Path
import xml.etree.ElementTree as ET

root = Path(__file__).resolve().parents[2]
ns = {'xaml': 'http://schemas.microsoft.com/winfx/2006/xaml/presentation', 'controls': 'using:CommunityToolkit.WinUI.Controls'}
cards = []
for path, marker in [('GeneralSettingsControl.xaml', 'ApplicationFontComboBox'), ('LyricsSettingsControl.xaml', 'IndependentLyricsFontToggle'), ('LyricsSettingsControl.xaml', 'IndependentDesktopLyricsFontToggle')]:
    tree = ET.parse(root / 'View/SubView/Settings' / path)
    cards.append(next(card for card in tree.findall('.//controls:SettingsCard', ns) if marker in ET.tostring(card, encoding='unicode')))
ET.register_namespace('', ns['xaml'])
ET.register_namespace('x', 'http://schemas.microsoft.com/winfx/2006/xaml')
ET.register_namespace('controls', ns['controls'])
markup = ''.join(ET.tostring(card, encoding='unicode') for card in cards).replace('ViewModel.AppViewModel.', 'Preferences.')
# Keep the production hierarchy and compiled TwoWay/OneWay bindings; only replace the facade path.
text = """<UserControl x:Class="FontUiRegression.FontSettingsControl"
 xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
 xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
 xmlns:controls="using:CommunityToolkit.WinUI.Controls"
 xmlns:model="using:WinUIMusicPlayer.Model"
 xmlns:tool="using:WinUIMusicPlayer.Utils">
 <UserControl.Resources>
  <DataTemplate x:Key="FontComboboxTemplate" x:DataType="model:FontInfo">
   <TextBlock FontFamily="{x:Bind FontFamily}" Text="{x:Bind DisplayName}" />
  </DataTemplate>
 </UserControl.Resources>
 <StackPanel>""" + markup + '</StackPanel></UserControl>'
Path(__file__).with_name('FontSettingsControl.xaml').write_bytes(text.replace('\r\n', '\n').replace('\n', '\r\n').encode('utf-8'))
