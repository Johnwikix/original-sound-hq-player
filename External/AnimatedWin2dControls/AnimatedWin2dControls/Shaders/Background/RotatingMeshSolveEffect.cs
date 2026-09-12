using ComputeSharp;
using ComputeSharp.D2D1;

namespace AnimatedWin2dControls.Shaders.Background;

/// <summary>
/// Inverts the original PinchVertex triangles, including their overdraw order.
/// Integer resource-texture loads allow a dynamic candidate loop without implicit
/// derivatives. No iterative solver or interpolation across folded UV sheets is used.
/// </summary>
[D2DInputCount(0)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
[D2DGeneratedPixelShaderDescriptor]
public readonly partial struct RotatingMeshSolveEffect : ID2D1PixelShader
{
    [D2DResourceTextureIndex(0)]
    private readonly D2D1ResourceTexture2D<float4> vertices;
    [D2DResourceTextureIndex(1)]
    private readonly D2D1ResourceTexture2D<float4> cells;
    private readonly float2 dispatchSize;
    private readonly float pinchMix;
    private readonly int meshRows;
    private readonly int meshColumns;
    private readonly float textureScale;
    private readonly float textureOffset;

    public RotatingMeshSolveEffect(float2 dispatchSize, float pinchMix, int meshRows,
        int meshColumns, float textureScale, float textureOffset)
    {
        this.dispatchSize = dispatchSize;
        this.pinchMix = pinchMix;
        this.meshRows = meshRows;
        this.meshColumns = meshColumns;
        this.textureScale = textureScale;
        this.textureOffset = textureOffset;
    }

    public float4 Execute()
    {
        float2 uv = D2D.GetScenePosition().XY / dispatchSize;
        float2 point = new float2(uv.X * 2f - 1f, 1f - uv.Y * 2f);
        int tileX = Hlsl.Clamp((int)Hlsl.Floor((point.X + 1f) * 16f), 0, 31);
        int tileY = Hlsl.Clamp((int)Hlsl.Floor((point.Y + 1f) * 16f), 0, 31);
        int tile = tileY * 32 + tileX;
        int count = (int)cells[0, tile].X;
        for (int candidate = count; candidate > 0; candidate--)
        {
            int index = (int)cells[candidate, tile].X;
            int row = (int)((uint)index / (uint)meshColumns);
            int column = index - row * meshColumns;
            float2 a = Vertex(column, row);
            float2 b = Vertex(column + 1, row);
            float2 c = Vertex(column, row + 1);
            float2 d = Vertex(column + 1, row + 1);
            // Reverse index-buffer order: triangle (d,b,a) is drawn last.
            float3 weights = Barycentric(point, d, b, a);
            float2 local = new float2(weights.X + weights.Y, weights.X);
            if (weights.Z == 0f)
            {
                weights = Barycentric(point, a, c, d);
                local = new float2(1f - weights.X - weights.Y, 1f - weights.X);
            }
            if (weights.Z > 0f)
            {
                uv = new float2((column + local.X) / (meshColumns - 1),
                    1f - (row + local.Y) / (meshRows - 1));
                uv = uv * textureScale + textureOffset;
                break;
            }
        }
        // Uncovered pixels use the fullscreen treated layer, without the mesh crop.
        return new float4(uv.X, uv.Y, 0f, 1f);
    }

    private float2 Vertex(int column, int row)
    {
        float4 vertex = vertices[column, row];
        return Hlsl.Lerp(vertex.XY, vertex.ZW, pinchMix);
    }

    // Returns (weightA, weightB, hit). Degenerate triangles cover no pixels.
    private static float3 Barycentric(float2 p, float2 a, float2 b, float2 c)
    {
        float2 ab = b - a, ac = c - a, ap = p - a;
        float determinant = ab.X * ac.Y - ab.Y * ac.X;
        if (Hlsl.Abs(determinant) < 1e-12f) return float3.Zero;
        float wb = (ap.X * ac.Y - ap.Y * ac.X) / determinant;
        float wc = (ab.X * ap.Y - ab.Y * ap.X) / determinant;
        float wa = 1f - wb - wc;
        return new float3(wa, wb, wa >= 0f && wb >= 0f && wc >= 0f ? 1f : 0f);
    }
}
