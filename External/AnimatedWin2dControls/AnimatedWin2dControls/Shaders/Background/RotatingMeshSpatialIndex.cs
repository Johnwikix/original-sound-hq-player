using System;
using System.Collections.Generic;
using System.Numerics;

namespace AnimatedWin2dControls.Shaders.Background;

/// <summary>
/// Conservative screen-space cell lists for every phase of a from/to mesh.
/// Vertices move linearly, so endpoint bounds enclose their entire motion.
/// Lists retain index-buffer order, including folded cells; positions are unchanged.
/// </summary>
public static class RotatingMeshSpatialIndex
{
    public const int TileCount = 32;
    // Each texture row is one tile: count followed by row-major cell vertex indices.
    public readonly record struct Data(float[] Texels, int Width);

    public static Data Create(RotatingMeshWarp.MeshData mesh)
    {
        var tiles = new List<int>[TileCount * TileCount];
        for (int i = 0; i < tiles.Length; i++) tiles[i] = new List<int>();
        for (int row = 0; row < mesh.Rows - 1; row++)
        for (int column = 0; column < mesh.Columns - 1; column++)
        {
            int index = row * mesh.Columns + column;
            Vector2 min = new(float.PositiveInfinity), max = new(float.NegativeInfinity);
            Include(index); Include(index + 1);
            Include(index + mesh.Columns); Include(index + mesh.Columns + 1);
            if (max.X < -1 || min.X > 1 || max.Y < -1 || min.Y > 1) continue;
            int x0 = Tile(min.X), x1 = Tile(max.X);
            int y0 = Tile(min.Y), y1 = Tile(max.Y);
            for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++) tiles[y * TileCount + x].Add(index);

            void Include(int i)
            {
                min = Vector2.Min(min, Vector2.Min(mesh.From[i], mesh.To[i]));
                max = Vector2.Max(max, Vector2.Max(mesh.From[i], mesh.To[i]));
            }
        }
        int width = 1;
        foreach (var tile in tiles) width = Math.Max(width, tile.Count + 1);
        var texels = new float[width * tiles.Length * 4];
        for (int tile = 0; tile < tiles.Length; tile++)
        {
            int offset = tile * width * 4;
            texels[offset] = tiles[tile].Count;
            for (int i = 0; i < tiles[tile].Count; i++) texels[offset + (i + 1) * 4] = tiles[tile][i];
        }
        return new Data(texels, width);
    }
    private static int Tile(float ndc) => Math.Clamp((int)MathF.Floor((ndc + 1) * .5f * TileCount), 0, TileCount - 1);
}
