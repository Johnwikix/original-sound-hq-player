using System.Numerics;
using System.Runtime.InteropServices;
using AnimatedWin2dControls.Shaders.Background;
using ComputeSharp;
using ComputeSharp.D2D1;
using ComputeSharp.D2D1.Interop;
using ComputeSharp.D2D1.WinUI;
using Microsoft.Graphics.Canvas;
using Windows.Graphics.DirectX;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var log = new StreamWriter(Path.Combine(AppContext.BaseDirectory, "shader-gpu-results.txt")) { AutoFlush = true };
        try { Run(log); }
        catch (Exception ex) { log.WriteLine(ex); Environment.ExitCode = 1; }
    }

    private static void Run(TextWriter log)
    {
        log.WriteLine($"Package: {Windows.ApplicationModel.Package.Current.Id.Name}");
        using var device = new CanvasDevice();
        int checkedPixels = 0, failed = 0, folded = 0;
        float maxError = 0;
        foreach (float dpi in new[] { 96f, 144f, 192f })
        for (int layout = 0; layout < 2; layout++)
        for (int preset = 0; preset < (layout == 0 ? 7 : 6); preset++)
        {
            bool portrait = layout == 1;
            int width = portrait ? 80 : 128, height = portrait ? 128 : 80;
            int presetCount = portrait ? 4 : 5;
            var mesh = preset < presetCount ? RotatingMeshWarp.Create(preset, portrait)
                : SyntheticMesh(preset == presetCount + 1);
            var spatial = RotatingMeshSpatialIndex.Create(mesh);
            float[] packed = new float[mesh.From.Length * 4];
            for (int i = 0; i < mesh.From.Length; i++)
            {
                packed[4*i] = mesh.From[i].X; packed[4*i+1] = mesh.From[i].Y;
                packed[4*i+2] = mesh.To[i].X; packed[4*i+3] = mesh.To[i].Y;
            }
            using var effect = new PixelShaderEffect<RotatingMeshSolveEffect>();
            effect.ResourceTextureManagers[0] = Resource(packed, mesh.Columns, mesh.Rows);
            effect.ResourceTextureManagers[1] = Resource(spatial.Texels, spatial.Width, 1024);
            using var target = new CanvasRenderTarget(device, width*96f/dpi, height*96f/dpi,
                dpi, DirectXPixelFormat.R32G32B32A32Float, CanvasAlphaMode.Premultiplied);
            int startFailures = failed;
            for (int phase = 0; phase <= 10; phase++)
            {
                float mix = phase / 10f, scale = portrait ? 1f : .8f, offset = (1-scale)/2;
                effect.ConstantBuffer = new RotatingMeshSolveEffect(new float2(width,height),mix,mesh.Rows,mesh.Columns,scale,offset);
                using (var ds = target.CreateDrawingSession()) { ds.Clear(Microsoft.UI.Colors.Transparent); ds.DrawImage(effect); }
                var actual = MemoryMarshal.Cast<byte,float>(target.GetPixelBytes());
                for (int y=0; y<height; y+=5)
                for (int x=0; x<width; x+=5)
                {
                    Vector2 screen = new((x+.5f)/width,(y+.5f)/height);
                    var expected = Scan(mesh,mix,screen,out int hits);
                    if (hits>0) expected = expected*scale+new Vector2(offset);
                    if (hits>1) folded++;
                    int pixel = (y*width+x)*4;
                    Vector2 got = new(actual[pixel],actual[pixel+1]);
                    float error = Vector2.Distance(expected,got);
                    maxError = Math.Max(error,maxError);
                    if (!float.IsFinite(error) || error > .0001f || actual[pixel+3] != 1)
                    {
                        if (failed < 10) log.WriteLine($"FAIL dpi={dpi} layout={layout} preset={preset} mix={mix} pixel={x},{y} expected={expected} got={got} alpha={actual[pixel+3]}");
                        failed++;
                    }
                    checkedPixels++;
                }
            }
            log.WriteLine($"dpi={dpi} {(portrait?"portrait":"landscape")} {preset}: {failed-startFailures} failures; max tile candidates={spatial.Width-1}");
        }
        log.WriteLine($"{(failed == 0 ? "PASS" : "FAIL")}: {checkedPixels} GPU pixels checked against exhaustive triangle rasterization; {folded} overlap samples; {failed} failures; max UV error={maxError:G6}");
        if (failed != 0) Environment.ExitCode = 1;
    }

    private static RotatingMeshWarp.MeshData SyntheticMesh(bool folded)
    {
        var from = new Vector2[9]; var to = new Vector2[9];
        for (int r=0;r<3;r++)
        for (int c=0;c<3;c++)
        {
            from[r*3+c]=new Vector2(folded ? new[] {-1f,1f,-.5f}[c] : c-1,r-1);
            to[r*3+c]=new Vector2(folded ? new[] {-1f,-.5f,1f}[c] : c-1,r-1);
        }
        return new(from,to,3,3);
    }

    private static D2D1ResourceTextureManager Resource(float[] data,int width,int height) => new(
        new uint[] {(uint)width,(uint)height},D2D1BufferPrecision.Float32,D2D1ChannelDepth.Four,
        D2D1Filter.MinMagMipPoint,new[] {D2D1ExtendMode.Clamp,D2D1ExtendMode.Clamp},
        MemoryMarshal.AsBytes(data.AsSpan()),new uint[] {(uint)width*16});

    // Independent oracle: scan every original triangle in forward index-buffer order,
    // using double precision barycentrics and replacing the previous hit on overdraw.
    private static Vector2 Scan(RotatingMeshWarp.MeshData mesh,float mix,Vector2 screen,out int hits)
    {
        Vector2 p = new(screen.X*2-1,1-screen.Y*2), result = screen;
        int hitCount = 0;
        for (int row=0;row<mesh.Rows-1;row++)
        for (int col=0;col<mesh.Columns-1;col++)
        {
            int a=row*mesh.Columns+col,b=a+1,c=a+mesh.Columns,d=c+1;
            Test(a,c,d); Test(d,b,a);
        }
        hits=hitCount;
        return result;
        void Test(int ia,int ib,int ic)
        {
            Vector2 a=Vector2.Lerp(mesh.From[ia],mesh.To[ia],mix),b=Vector2.Lerp(mesh.From[ib],mesh.To[ib],mix),c=Vector2.Lerp(mesh.From[ic],mesh.To[ic],mix);
            double abx=b.X-a.X,aby=b.Y-a.Y,acx=c.X-a.X,acy=c.Y-a.Y,px=p.X-a.X,py=p.Y-a.Y;
            double det=abx*acy-aby*acx;
            if (Math.Abs(det)<1e-12) return;
            double wb=(px*acy-py*acx)/det,wc=(abx*py-aby*px)/det,wa=1-wb-wc;
            if (wa<0 || wb<0 || wc<0) return;
            Vector2 Uv(int i)=>new((i%mesh.Columns)/(float)(mesh.Columns-1),1-(i/mesh.Columns)/(float)(mesh.Rows-1));
            result=(float)wa*Uv(ia)+(float)wb*Uv(ib)+(float)wc*Uv(ic);
            hitCount++;
        }
    }
}
