using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using WinUIMusicPlayer.Services.AudioAnalysis;
using WinUIMusicPlayer.ViewModel.Pages;
using WinUIMusicPlayer.View.SubView.Settings;
internal static class Program
{
    [STAThread] static void Main()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(initialization=>{SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));_ = new TestApp();});
    }
}
internal sealed partial class TestApp : Application, IXamlMetadataProvider
{
    private readonly IXamlMetadataProvider[] providers = [new Microsoft.UI.Xaml.XamlTypeInfo.XamlControlsXamlMetaDataProvider(), new CommunityToolkit.WinUI.Controls.SettingsControlsRns.CommunityToolkit_WinUI_Controls_SettingsControls_XamlTypeInfo.XamlMetaDataProvider(), (IXamlMetadataProvider)Activator.CreateInstance(typeof(Program).Assembly.GetTypes().Single(t=>t.FullName!.EndsWith(".XamlMetaDataProvider")))!];
    public IXamlType GetXamlType(Type t)=>providers.Select(x=>x.GetXamlType(t)).FirstOrDefault(x=>x is not null)!;
    public IXamlType GetXamlType(string t)=>providers.Select(x=>x.GetXamlType(t)).FirstOrDefault(x=>x is not null)!;
    public XmlnsDefinition[] GetXmlnsDefinitions()=>[];
    private async Task CheckDiagnostics(Window window, StackPanel panel, string resultFile)
    {
        string directory=Path.Combine(AppContext.BaseDirectory,"layout-diagnostics",Guid.NewGuid().ToString("N"));
        WinUIMusicPlayer.Services.UiLayoutDiagnostics.Initialize(directory,()=>window.Content,_=>{});
        var cycle=new LayoutCycleProbe{Name="DeliberateLayoutCycle",Width=100,Height=20};
        WinUIMusicPlayer.Services.UiLayoutDiagnostics.Track(panel);
        WinUIMusicPlayer.Services.UiLayoutDiagnostics.Track(cycle);
        try
        {
            var size=new Windows.Foundation.Size(100,20);
            WinUIMusicPlayer.Services.UiLayoutDiagnostics.Measure(cycle,size);
            long allocated=GC.GetAllocatedBytesForCurrentThread();
            for(int i=0;i<10000;i++)WinUIMusicPlayer.Services.UiLayoutDiagnostics.Measure(cycle,size);
            allocated=GC.GetAllocatedBytesForCurrentThread()-allocated;
            panel.Children.Insert(0,cycle);
            for(int i=0;i<16;i++)
            {
                cycle.Width=i%2==0?100:101;
                panel.UpdateLayout();
                await Task.Delay(20);
            }
            // Exercise capture on a real WinUI tree. The exception is injected;
            // this does not claim to reproduce the user's native layout failure.
            WinUIMusicPlayer.Services.UiLayoutDiagnostics.Capture(new LayoutCycleException("Injected layout diagnostic regression"));
            await WinUIMusicPlayer.Services.UiLayoutDiagnostics.StopAsync();
            string? report=null;
            for(int i=0;i<200;i++)
            {
                report=Directory.Exists(directory)?Directory.EnumerateFiles(directory,"Layout-*.log").FirstOrDefault():null;
                if(report is not null && new FileInfo(report).Length>0)break;
                await Task.Delay(25);
            }
            string detail=File.ReadAllText(report??throw new Exception("No diagnostic file written."));
            if(!detail.Contains("DeliberateLayoutCycle")||!detail.Contains("Measure")||!detail.Contains("Path=")||!detail.Contains("Visible tree:"))throw new Exception("Missing component or tree evidence.");
            File.WriteAllText(resultFile,$"PASS: injected LayoutCycleException on a real WinUI tree produced component name, parent path, size history and tree in {report}. 10000 warmed record calls allocated {allocated} B.");
        }
        finally {panel.Children.Remove(cycle); }
    }
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        string file=Path.Combine(AppContext.BaseDirectory,"result.txt");
        File.WriteAllText(file+".started",string.Join("|",Environment.GetCommandLineArgs()));
        Window? window=null;
        try
        {
            Resources.MergedDictionaries.Add(new XamlControlsResources());
            var source=new Source();
            var vm=new AnalysisSettingsViewModel(source,NullLogger<AnalysisSettingsViewModel>.Instance);
            WinUIMusicPlayer.App.Services=new ServiceCollection().AddSingleton(vm).BuildServiceProvider();
            var control=new AudioAnalysisSettingsControl();
            vm.Start(DispatcherQueue.GetForCurrentThread());
            bool wrongThread=false;
            vm.PropertyChanged+=(_,_)=>wrongThread|=DispatcherQueue.GetForCurrentThread()?.HasThreadAccess!=true;
            var panel=new StackPanel{Spacing=4,Margin=new Thickness(18,0,18,36)};
            panel.Children.Add(new TextBlock{Text="Advanced settings",Margin=new Thickness(0,16,0,8)});
            panel.Children.Add(control);
            for(int i=0;i<12;i++)panel.Children.Add(new CommunityToolkit.WinUI.Controls.SettingsCard{Header="Other setting "+i,Content=new ToggleSwitch()});
            var scroll=new ScrollViewer{Content=panel,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
            window=new Window{Content=scroll};window.AppWindow.Resize(new(700,360));window.AppWindow.Move(new(-30000,-30000));window.Activate();
            if(Environment.GetCommandLineArgs().Contains("--diagnostic-cycle"))
            {
                await CheckDiagnostics(window, panel, file);
                await vm.StopAsync();
                return;
            }
            ((CommunityToolkit.WinUI.Controls.SettingsExpander)control.FindName("AudioAnalysisExpander")).IsExpanded=true;
            await Task.Delay(250);
            await Task.Run(()=>source.Publish(new(true,true,2,10,8,0,"Real task","Running")));
            await Task.Delay(150);
            var progress=(ProgressBar)control.FindName("AnalysisProgress");
            var status=(TextBlock)control.FindName("AnalysisStatus");
            var current=(TextBlock)control.FindName("AnalysisCurrent");
            if(progress.Maximum!=10 || progress.Value!=2 || !status.Text.Contains("queued 8") || !current.Text.Contains("Real task") || wrongThread)throw new Exception("Background progress did not bind on the UI thread.");
            if(Environment.GetCommandLineArgs().Contains("--layout-stress"))
            {
                Application.Current.DebugSettings.LayoutCycleTracingLevel=LayoutCycleTracingLevel.High;
                for(int step=0;step<240;step++)
                {
                    window.AppWindow.Resize(new(450+(step%31)*23,300+(step%7)*90));
                    ((CommunityToolkit.WinUI.Controls.SettingsExpander)control.FindName("AudioAnalysisExpander")).IsExpanded=step%5!=0;
                    source.Publish(new(true,true,step,300,16,0,new string('W',step%3==0?300:10),"Running"));
                    await Task.Delay(10);
                }
                File.WriteAllText(file,"PASS: 240 nested scrolling settings layout changes, expansion and progress updates.");
                await vm.StopAsync();
                return;
            }
            var toggle=(ToggleSwitch)control.FindName("AnalysisEnabled");
            toggle.IsOn=false;
            toggle.IsOn=true;
            await Task.Delay(200);
            if(!source.Progress.Enabled || !vm.Enabled)throw new Exception("Rapid toggle lost the final enable intent.");
            toggle.IsOn=false;
            await Task.Delay(150);
            if(source.Progress.Enabled || !current.Text.Contains("disabled"))throw new Exception("Toggle did not update shared state.");
            await vm.StopAsync();
            string before=current.Text;
            await Task.Run(()=>source.Publish(new(true,true,5,10,5,0,"Late task","Running")));
            await Task.Delay(100);
            if(current.Text!=before)throw new Exception("Stopped VM accepted late progress.");
            File.WriteAllText(file,"PASS: actual SettingsExpander, toggle persistence boundary, queued/completed/current bindings, UI dispatch, and late notification unsubscribe.");
        }
        catch(Exception ex){File.WriteAllText(file,"FAIL: "+ex);}
        finally {window?.Close();Exit();}
    }
}

internal sealed partial class LayoutCycleProbe:Panel
{
    protected override Windows.Foundation.Size MeasureOverride(Windows.Foundation.Size availableSize)
    {
        WinUIMusicPlayer.Services.UiLayoutDiagnostics.Measure(this,availableSize);
        return new(100,20);
    }
}
