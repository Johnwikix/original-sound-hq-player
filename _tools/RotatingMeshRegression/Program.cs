using System.Numerics;
using AnimatedWin2dControls.Shaders.Background;

static Vector2 Warp(RotatingMeshWarp.MeshData mesh, Vector2 uv, float mix)
{
    float x = Math.Clamp(uv.X, 0, 1) * (mesh.Columns - 1);
    float y = Math.Clamp(1 - uv.Y, 0, 1) * (mesh.Rows - 1);
    int c = Math.Min((int)x, mesh.Columns - 2), r = Math.Min((int)y, mesh.Rows - 2);
    Vector2 At(int row, int col) => Vector2.Lerp(mesh.From[row * mesh.Columns + col], mesh.To[row * mesh.Columns + col], mix);
    return Vector2.Lerp(Vector2.Lerp(At(r,c),At(r,c+1),x-c),Vector2.Lerp(At(r+1,c),At(r+1,c+1),x-c),y-r);
}
for (int layout = 0; layout < 2; layout++)
for (int preset = 0; preset < (layout == 0 ? 5 : 4); preset++)
{
    var mesh = RotatingMeshWarp.Create(preset,layout == 1);
    int failed = 0, samples = 0;
    for (int phase = 0; phase <= 10; phase++)
    for (int y = 0; y < 32; y++)
    for (int x = 0; x < 32; x++)
    {
        Vector2 screen = new((x+.5f)/32,(y+.5f)/32);
        Vector2 ndc = new(2*screen.X-1,1-2*screen.Y), uv = screen;
        for (int i=0;i<26;i++) uv = Vector2.Clamp(uv+(ndc-Warp(mesh,uv,phase/10f))*.7f,Vector2.Zero,Vector2.One);
        if (Vector2.Distance(Warp(mesh,uv,phase/10f),ndc) > .01f) failed++;
        samples++;
    }
    Console.WriteLine($"Legacy solver, {(layout==0?"landscape":"portrait")} {preset}: residual > .01 NDC in {failed}/{samples} samples ({100f*failed/samples:F1}%)");
}

int verified = 0;
for (int layout=0;layout<2;layout++)
for (int preset=0;preset<(layout==0?5:4);preset++)
{
    var mesh=RotatingMeshWarp.Create(preset,layout==1);
    var bins=RotatingMeshSpatialIndex.Create(mesh);
    for (int phase=0;phase<=32;phase++)
    for (int row=0;row<mesh.Rows-1;row++)
    for (int col=0;col<mesh.Columns-1;col++)
    {
        int a=row*mesh.Columns+col,b=a+1,c=a+mesh.Columns,d=c+1;
        Check(a,c,d); Check(d,b,a);
        void Check(int ia,int ib,int ic)
        {
            Vector2 At(int i)=>Vector2.Lerp(mesh.From[i],mesh.To[i],phase/32f);
            Vector2 point=(At(ia)+At(ib)+At(ic))/3;
            if (point.X < -1 || point.X > 1 || point.Y < -1 || point.Y > 1) return;
            int tx=Math.Clamp((int)MathF.Floor((point.X+1)*16),0,31),ty=Math.Clamp((int)MathF.Floor((point.Y+1)*16),0,31);
            int offset=(ty*32+tx)*bins.Width*4,count=(int)bins.Texels[offset];
            bool found=false;
            for(int j=1;j<=count;j++) if(bins.Texels[offset+j*4]==a) found=true;
            if(!found) throw new Exception($"Spatial index omitted cell {a}, layout {layout}, preset {preset}, phase {phase}");
            verified++;
        }
    }
}
Console.WriteLine($"PASS: spatial index contains all {verified} tested triangle centroids across 33 phases and all presets.");
