using System.ComponentModel;
using System.Xml.Linq;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Markup;

/// <summary>Exercises the shipping enhancement card hierarchy with real WinUI controls.</summary>
internal static class SpectralSettingsRegression
{
    internal static async Task RunAsync(Panel host)
    {
        XNamespace controls="using:CommunityToolkit.WinUI.Controls";
        XNamespace presentation="http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var document=XDocument.Load(Path.Combine(AppContext.BaseDirectory,"Fixtures","GeneralSettingsControl.xaml"));
        var source=document.Descendants(controls+"SettingsCard").Single(e=>((string?)e.Attribute("Header"))?.Contains("SpectralEnhancementTitle")==true);
        var markup=new XElement(presentation+"Grid",new XAttribute("xmlns",presentation.NamespaceName),
            new XAttribute(XNamespace.Xmlns+"controls",controls.NamespaceName),new XElement(source));
        foreach(var element in markup.DescendantsAndSelf())
            foreach(var attribute in element.Attributes().ToArray())
                if(attribute.Value.StartsWith("{x:Bind"))
                {
                    if(attribute.Name.LocalName=="SelectedIndex")attribute.Remove();
                    else attribute.Value=attribute.Value.Contains("Description")?"Mono/stereo PCM up to 48 kHz; shared device rate or exclusive 2×.":
                        attribute.Value.Contains("Off")?"Off":attribute.Value.Contains("Lossy')")?"Lossy songs only":
                        attribute.Value.Contains("All")?"Lossy + lossless up to 48 kHz":"Spectral enhancement";
                }
        for(int saved=0;saved<3;saved++)
        {
            var grid=(Grid)XamlReader.Load(markup.ToString());
            var card=(SettingsCard)grid.Children[0];
            var combo=(ComboBox)card.Content;
            var preference=new Preference { Selection=saved };
            combo.DataContext=preference;
            combo.SetBinding(ComboBox.SelectedIndexProperty,new Binding {Path=new PropertyPath(nameof(Preference.Selection)),Mode=BindingMode.TwoWay});
            host.Children.Add(grid);
            try
            {
                await Task.Delay(50);
                grid.UpdateLayout();
                if(!card.IsLoaded||card.ActualHeight<=0||combo.Items.Count!=3||combo.SelectedIndex!=saved||preference.Selection!=saved)
                    throw new Exception($"Enhancement card failed to restore choice {saved}");
                for(int choice=0;choice<3;choice++)
                {
                    combo.SelectedIndex=choice;
                    if(preference.Selection!=choice)throw new Exception("Enhancement choice did not reach preference");
                }
            }
            finally {host.Children.Remove(grid);}
        }
    }
    private sealed class Preference : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        public int Selection {get;set {if(field==value)return;field=value;PropertyChanged?.Invoke(this,new(nameof(Selection)));}}
    }
}
